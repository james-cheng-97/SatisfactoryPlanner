namespace SatisfactoryPlanner;

/// <summary>One consumer on a shared belt. Cap = the most it will pull (∞ for a drained final output or a sink).</summary>
public record SplitConsumer(string Key, string Label, double Need, double Cap, int Machines = 1)
{
    public bool Uncapped => double.IsPositiveInfinity(Cap);
}

public enum SplitKind { None, Splitter, Smart, Manifold }

public class SplitPlan
{
    public required string Item { get; init; }
    public System.Windows.Media.ImageSource? Icon => ImageCache.Get(Item);
    public string ItemName => GameData.Item(Item).Name;
    public double Supply { get; init; }
    public double Need { get; init; }
    public string Layout { get; init; } = "";
    public string Shares { get; init; } = "";
    public string Extra => Supply - Need > 1e-3 ? $"+{Supply - Need:0.##}" : "0";
    /// <summary>Share per consumer key (splitter layouts), or null for a manifold / single consumer.</summary>
    public Dictionary<string, double>? Weights { get; init; }
    /// <summary>Manifold: the consumer placed last (gets the remainder).</summary>
    public string? ManifoldLast { get; init; }
    public SplitKind Kind { get; init; }
    /// <summary>Splitters at the split point itself (machine-group manifolds are counted separately).</summary>
    public int SplitterCount { get; init; }
    /// <summary>Splitter outputs going to each consumer (&gt;1 means they're merged together).</summary>
    public Dictionary<string, int> OutputsPerConsumer { get; init; } = new();
    /// <summary>Smart splitter: consumers on Overflow outputs (they only get what the others can't take).</summary>
    public HashSet<string>? Overflow { get; init; }
    /// <summary>Parallel belt lines for this item (a flow too big for one belt is split into independent lines).</summary>
    public int Line { get; set; } = 1;
    public int Lines { get; set; } = 1;
    public string LineText => Lines <= 1 ? "" : Line == 0 ? $"×{Lines}" : $"{Line}/{Lines}";
    public string Belt { get; set; } = "";
    /// <summary>Machines of each consumer group on this line (a group may be spread over several lines).</summary>
    public Dictionary<string, int> MachinesPerConsumer { get; set; } = new();
    /// <summary>Drained outputs / sinks on this line.</summary>
    public int DrainsOnLine { get; set; }
    /// <summary>Set when a single machine needs more than one belt carries (it must be fed by several belts).</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// Chooses how to split one belt between several consumers, with at most two splitters in series (2- or 3-way each;
/// several outputs may merge into the same consumer, but no splitter/merger loops).
///
/// Splitter behaviour: each output gets a fixed share; an output only gives its share away when its consumer is full.
/// So with shares w and consumer caps c, the belt settles at a_i = min(c_i, λ·w_i) with Σa = supply. Every consumer
/// gets its need when λ ≥ need_i / w_i, which makes the required supply Σ min(c_i, λ*·w_i) — anything a greedy
/// (drained or over-built) consumer pulls beyond its need is the price of that layout. A manifold (priority chain,
/// greediest consumer last) is also considered.
/// </summary>
public static class Splitters
{
    record Structure(int K1, int[] K2) // first splitter K1 ways; branch j feeds a second K2[j]-way splitter (1 = none)
    {
        public List<(int branch, double share)> Leaves()
        {
            var l = new List<(int, double)>();
            for (int j = 0; j < K1; j++)
                for (int k = 0; k < K2[j]; k++) l.Add((j, 1.0 / (K1 * K2[j])));
            return l;
        }
    }

    static readonly List<Structure> Structures = Build();

    static List<Structure> Build()
    {
        var list = new List<Structure>();
        foreach (var k1 in new[] { 2, 3 })
            foreach (var combo in Combos(k1))
                list.Add(new Structure(k1, combo));
        return list;
    }

    static IEnumerable<int[]> Combos(int n) // non-decreasing second-level choices, e.g. [1,1,3]
    {
        IEnumerable<int[]> Rec(int left, int min) =>
            left == 0 ? [[]] : new[] { 1, 2, 3 }.Where(k => k >= min).SelectMany(k => Rec(left - 1, k).Select(r => r.Prepend(k).ToArray()));
        return Rec(n, 1);
    }

    /// <summary>
    /// Like <see cref="Plan"/>, but respects belt capacity. If the layout needs more than one belt can carry, the
    /// consumers are broken into single machines (and drained outputs into belt-sized chunks) and packed into the
    /// fewest parallel lines whose own layouts each fit on one belt. Lines are independent: spill on one line can't
    /// reach another, so each line is planned — and pays for its greedy consumers — on its own.
    /// </summary>
    public static List<SplitPlan> PlanLines(string item, List<SplitConsumer> cs, bool twoInRow, double beltCap, string beltLabel, bool loopFirst = false)
    {
        var single = Plan(item, cs, twoInRow, loopFirst);
        single.Belt = beltLabel;
        single.MachinesPerConsumer = cs.Where(c => !c.Uncapped).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Machines));
        single.DrainsOnLine = cs.Count(c => c.Uncapped);
        if (single.Supply <= beltCap + 1e-6) return [single];

        // break into units: one per machine, drained outputs into chunks that fit a belt
        var units = new List<SplitConsumer>();
        string? warning = null;
        foreach (var c in cs)
        {
            if (c.Uncapped)
            {
                int k = Math.Max(1, (int)Math.Ceiling(c.Need / (beltCap * 0.9)));
                for (int i = 0; i < k; i++) units.Add(c with { Need = c.Need / k, Machines = 1 });
            }
            else
            {
                // one unit per machine, but huge groups go in chunks so packing stays fast (≤ ~300 units per item)
                int m = Math.Max(1, c.Machines);
                double perMachine = c.Cap / m;
                if (perMachine > beltCap)
                {
                    // one machine eats more than a belt carries: feed it from several belts (merged at the machine)
                    int feeds = (int)Math.Ceiling(perMachine / (beltCap * 0.95));
                    warning = Loc.T("warn.machineOverBelt", c.Label, perMachine.ToString("0.#"), beltLabel, feeds);
                    for (int i = 0; i < m; i++)
                        for (int f = 0; f < feeds; f++)
                            units.Add(c with { Need = c.Need / m / feeds, Cap = perMachine / feeds, Machines = f == 0 ? 1 : 0 });
                    continue;
                }
                int chunk = Math.Max(1, (int)Math.Ceiling(m * cs.Count / 120.0));
                for (int done = 0; done < m; done += chunk)
                {
                    int k = Math.Min(chunk, m - done);
                    units.Add(c with { Need = c.Need * k / m, Cap = c.Cap * k / m, Machines = k });
                }
            }
        }
        double Load(SplitConsumer u) => u.Uncapped ? u.Need : u.Cap; // greedy consumers count at what they can pull

        int first = (int)Math.Ceiling(single.Supply / beltCap);
        for (int lines = first; lines <= Math.Min(units.Count, first + 4); lines++)
        {
            // longest-processing-time packing: biggest unit to the emptiest line
            var bins = Enumerable.Range(0, lines).Select(_ => new List<SplitConsumer>()).ToList();
            foreach (var u in units.OrderByDescending(Load))
                bins.OrderBy(b => b.Sum(Load)).First().Add(u);
            if (bins.Any(b => b.Count == 0)) continue;

            var plans = bins.Select(b =>
            {
                var merged = b.GroupBy(u => u.Key).Select(g => g.First() with
                {
                    Need = g.Sum(u => u.Need), Cap = g.First().Uncapped ? double.PositiveInfinity : g.Sum(u => u.Cap), Machines = g.Sum(u => u.Machines)
                }).ToList();
                var p = Plan(item, merged, twoInRow, loopFirst);
                p.MachinesPerConsumer = merged.Where(c => !c.Uncapped).ToDictionary(c => c.Key, c => c.Machines);
                p.DrainsOnLine = merged.Count(c => c.Uncapped);
                return p;
            }).ToList();
            if (plans.All(p => p.Supply <= beltCap + 1e-6) || lines == Math.Min(units.Count, first + 4))
            {
                for (int i = 0; i < plans.Count; i++) { plans[i].Line = i + 1; plans[i].Lines = plans.Count; plans[i].Belt = beltLabel; }
                if (warning != null) plans[0].Warning = warning;
                return plans;
            }
        }
        return [single];
    }

    /// <param name="twoInRow">Allow a second splitter behind the first (default: one splitter between output and input).</param>
    /// <param name="loopFirst">A recycling loop item: the machines must always come first (smart splitter, drained output on
    /// Overflow) even where a plain splitter would do — a fixed share to the output could starve the loop.</param>
    public static SplitPlan Plan(string item, List<SplitConsumer> cs, bool twoInRow = false, bool loopFirst = false)
    {
        double need = cs.Sum(c => c.Need);
        if (cs.Count <= 1)
            return new SplitPlan { Item = item, Supply = need, Need = need, Kind = SplitKind.None, Layout = Loc.T("split.none"), Shares = Describe(cs, null) };

        // fluids: pipes share by demand, not fixed ratios, and there is no smart splitter — plain junction manifold
        if (GameData.Item(item).IsFluid)
            return new SplitPlan { Item = item, Supply = need, Need = need, Kind = SplitKind.Manifold, Layout = Loc.T("split.pipe"), Shares = Describe(cs, null) };

        double bestS = double.MaxValue, bestCost = double.MaxValue; string bestLayout = ""; double[]? bestW = null;
        var bestOutputs = new Dictionary<string, int>();
        // one splitter by default; the toggle allows ONE more behind it (2 splitters total, never 3+)
        foreach (var st in Structures.Where(st => st.K2.Count(k => k > 1) <= (twoInRow ? 1 : 0)))
        {
            var leaves = st.Leaves();
            if (leaves.Count < cs.Count) continue;
            var assign = Assign(cs, leaves);
            var w = new double[cs.Count];
            for (int l = 0; l < leaves.Count; l++) w[assign[l]] += leaves[l].share;
            double lambda = cs.Select((c, i) => c.Need / w[i]).Max();
            double s = cs.Select((c, i) => Math.Min(c.Cap, lambda * w[i])).Sum();
            // every extra splitter must earn its keep: it has to save ≥ 2% of the belt's flow, otherwise keep it simple
            int splitters = 1 + st.K2.Count(k => k > 1);
            double cost = s * (1 + 0.02 * (splitters - 1));
            if (cost < bestCost - 1e-6)
            {
                bestCost = cost; bestS = s; bestW = w; bestLayout = Text(st, leaves, assign, cs);
                bestOutputs = assign.GroupBy(a => cs[a].Key).ToDictionary(g => g.Key, g => g.Count());
            }
        }

        // smart splitter (3 outputs): machines on normal outputs, drained outputs / sinks on Overflow, which only
        // receives what the machines can't take — one building instead of a manifold, and a sink can never steal a share
        var capped = cs.Where(c => !c.Uncapped).ToList();
        var uncapped = cs.Where(c => c.Uncapped).ToList();
        double smart = double.MaxValue;
        if (uncapped.Count >= 1 && capped.Count >= 1 && cs.Count <= 3)
            smart = capped.Sum(c => c.Cap) + uncapped.Count * uncapped.Max(c => c.Need); // overflow outputs share evenly

        // manifold: every consumer takes up to its cap in order; put the greediest (largest cap - need) last
        var last = cs.OrderByDescending(c => c.Cap - c.Need).First();
        double manifold = uncapped.Count > 1 ? double.MaxValue : cs.Where(c => c != last).Sum(c => c.Cap) + last.Need;

        // prefer: one plain splitter < one smart splitter < manifold, unless another option needs less supply
        double pick = Math.Min(bestS, Math.Min(smart, manifold));
        if (loopFirst && smart < double.MaxValue) pick = bestS = smart;
        if (bestW != null && bestS <= pick + 1e-6 && !(loopFirst && smart < double.MaxValue))
            return new SplitPlan
            {
                Item = item, Need = need, Supply = bestS, Layout = bestLayout, Shares = Describe(cs, bestW), Kind = SplitKind.Splitter,
                SplitterCount = bestLayout.Count(ch => ch == '⑂'), OutputsPerConsumer = bestOutputs,
                Weights = cs.Select((c, i) => (c.Key, bestW[i])).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Item2))
            };
        if (smart <= pick + 1e-6)
            return new SplitPlan
            {
                Item = item, Need = need, Supply = smart, Kind = SplitKind.Smart, SplitterCount = 1,
                Layout = Loc.T("split.smart", string.Join(", ", capped.Select(c => c.Label)), string.Join(", ", uncapped.Select(c => c.Label))),
                Shares = Describe(cs, null), Overflow = uncapped.Select(c => c.Key).ToHashSet(),
                OutputsPerConsumer = cs.ToDictionary(c => c.Key, _ => 1)
            };
        if (manifold == double.MaxValue) // several sinks/drains and nothing better: overflow them all from a manifold's end
            manifold = capped.Sum(c => c.Cap) + uncapped.Sum(c => c.Need) * uncapped.Count;
        return new SplitPlan
        {
            Item = item, Need = need, Supply = manifold, Kind = SplitKind.Manifold,
            Layout = Loc.T("split.manifold", last.Label), Shares = Describe(cs, null), ManifoldLast = last.Key
        };
    }

    /// <summary>Greedy: every consumer gets one leaf (biggest leaves to biggest needs), then each remaining leaf goes to
    /// whoever is currently the bottleneck (highest need/share), which lowers the required supply the most.</summary>
    static int[] Assign(List<SplitConsumer> cs, List<(int branch, double share)> leaves)
    {
        var order = Enumerable.Range(0, leaves.Count).OrderByDescending(l => leaves[l].share).ToList();
        var byNeed = Enumerable.Range(0, cs.Count).OrderByDescending(i => cs[i].Need).ToList();
        var assign = new int[leaves.Count];
        var w = new double[cs.Count];
        for (int i = 0; i < cs.Count; i++) { assign[order[i]] = byNeed[i]; w[byNeed[i]] += leaves[order[i]].share; }
        foreach (var l in order.Skip(cs.Count))
        {
            // bottleneck consumer, but never feed an uncapped (drained) consumer more than it needs to be the bottleneck
            int to = Enumerable.Range(0, cs.Count).OrderByDescending(i => cs[i].Need / w[i]).First();
            assign[l] = to; w[to] += leaves[l].share;
        }
        return assign;
    }

    static string Text(Structure st, List<(int branch, double share)> leaves, int[] assign, List<SplitConsumer> cs)
    {
        string Group(IEnumerable<int> leafIdx) => string.Join(", ", leafIdx.GroupBy(l => assign[l])
            .Select(g => (g.Count() > 1 ? $"{g.Count()}× " : "") + cs[g.Key].Label));
        var parts = new List<string>();
        for (int j = 0; j < st.K1; j++)
        {
            var mine = Enumerable.Range(0, leaves.Count).Where(l => leaves[l].branch == j).ToList();
            parts.Add(st.K2[j] == 1 ? Group(mine) : $"[⑂{st.K2[j]}: {Group(mine)}]");
        }
        // merge identical direct outputs of the first splitter ("2× Modular Frame")
        var merged = parts.GroupBy(p => p).Select(g => g.Count() > 1 && !g.Key.StartsWith("[") ? $"{g.Count()}× {g.Key}" : string.Join(" + ", g));
        return $"⑂{st.K1}: " + string.Join(" + ", merged);
    }

    static string Describe(List<SplitConsumer> cs, double[]? w) =>
        string.Join("; ", cs.Select((c, i) => $"{c.Label} {c.Need:0.##}" + (w != null ? $" ({w[i]:P0})" : "")));
}
