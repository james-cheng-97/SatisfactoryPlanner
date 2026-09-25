using System.Windows.Media;

namespace SatisfactoryPlanner;

public class MachineRow
{
    public required string Item { get; init; }
    public ImageSource? Icon => ImageCache.Get(Item);
    public string ItemName => GameData.Item(Item).Name;
    public required List<RecipeDef> Options { get; init; }
    public required RecipeDef Recipe { get; init; }
    public ImageSource? BuildingIcon => ImageCache.Get(Recipe.Building);
    public string BuildingName => GameData.Buildings.TryGetValue(Recipe.Building, out var b) ? b.Name : Recipe.Building;
    public double Exact { get; init; }
    public int Count { get; init; }
    public double Clock { get; init; }
    public string CountText { get; init; } = "";
    public string ClockText => $"{Clock:0.####}%";
    public double OutputRate { get; init; }
    public double Capacity { get; init; }
    public string Inputs { get; init; } = "";
    public string GoesTo { get; init; } = "";
    public string Outputs { get; init; } = "";
    public double Power { get; init; }
}

public class ResourceRow
{
    public required string Item { get; init; }
    public ImageSource? Icon => ImageCache.Get(Item);
    public string ItemName => GameData.Item(Item).Name;
    public double Rate { get; init; }
    public string Source { get; init; } = "";
    public ImageSource? SourceIcon { get; init; }
    public string CountText { get; init; } = "";
    public string ClockText { get; init; } = "";
    public string Note { get; init; } = "";
    public int ExtractorCount { get; init; }
}

public class BuildingTotal
{
    public required string Building { get; init; }
    public ImageSource? Icon => ImageCache.Get(Building);
    public string Name => GameData.Buildings.TryGetValue(Building, out var b) ? b.Name : Building;
    public int Count { get; set; }
    public double Power { get; set; }
    public bool IsExtractor { get; init; }
    public bool IsLogistics { get; init; }
    public string Kind => Loc.T(IsLogistics ? "kind.logistics" : IsExtractor ? "kind.extractor" : "kind.production");
}

public enum NodeKind { Raw, Import, Machine, Target, Surplus }

public class GraphNode
{
    public required string Key { get; init; }
    public NodeKind Kind { get; init; }
    public required string Item { get; init; }
    public string? Building { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public double Rate { get; init; }
    /// <summary>Outputs of this machine that need an overflow guard (null = none).</summary>
    public string? Guard { get; init; }
    /// <summary>Machine nodes: the recipe and how many machines this box stands for (used by the layout).</summary>
    public RecipeDef? Recipe { get; init; }
    public int Machines { get; init; }
}

public record GraphEdge(string From, string To, string Item, double Rate);

public class FlowRow
{
    public required string Item { get; init; }
    public ImageSource? Icon => ImageCache.Get(Item);
    public string ItemName => GameData.Item(Item).Name;
    public double Rate { get; init; }
    public string Kind { get; init; } = "";
    public bool IsTarget { get; init; }
    public bool IsSurplus { get; init; }
}

public class Plan
{
    public List<MachineRow> Machines = new();
    public List<ResourceRow> Resources = new();
    public List<FlowRow> Outputs = new();
    public List<BuildingTotal> Totals = new();
    public List<GraphNode> Nodes = new();
    public List<GraphEdge> Edges = new();
    public List<SplitPlan> Splits = new();
    public List<ClogGuard> Guards = new();
    /// <summary>"×3 Mk.5" when a link needs several parallel belts/pipes (used by the tree diagram).</summary>
    public Func<string, double, string?> BeltNote = (_, _) => null;
    public int ProductionMachines => Totals.Where(t => !t.IsExtractor && !t.IsLogistics).Sum(t => t.Count);
    public int Extractors => Totals.Where(t => t.IsExtractor).Sum(t => t.Count);
    public int SplitterParts => Totals.Where(t => t.IsLogistics && t.Building.Contains("Splitter")).Sum(t => t.Count);
    public int MergerParts => Totals.Where(t => t.IsLogistics && t.Building.Contains("Merger")).Sum(t => t.Count);
    public int ValveParts => Totals.Where(t => t.IsLogistics && t.Building == ClogGuards.Valve).Sum(t => t.Count);
    public int JunctionParts => Totals.Where(t => t.IsLogistics && t.Building.Contains("Junction")).Sum(t => t.Count);
    public double Scale = 1;
    /// <summary>A copy of this plan with other nodes / edges (the rest shared).</summary>
    public Plan With(List<GraphNode> nodes, List<GraphEdge> edges) { var p = (Plan)MemberwiseClone(); p.Nodes = nodes; p.Edges = edges; return p; }
    public double Power;
    public List<string> Warnings = new();

    const double PowerExp = 1.321928;
    static string Fmt(double v) => v.ToString("0.###");

    public static Plan Build(Settings s)
    {
        bool drained = s.Mode == RoundingMode.Drained;
        var sol = drained ? DrainedSolve(s) : s.Optimize ? Optimizer.Solve(s) : Solver.Solve(s);
        var plan = new Plan { Warnings = sol.Warnings, Splits = sol.Splits };
        bool fullClock = s.Mode is RoundingMode.SteadyRate or RoundingMode.Drained; // every machine & extractor at 100%

        // raw extraction needs (exact)
        var raw = sol.Inputs.Select(i => (item: i, need: sol.Demand.GetValueOrDefault(i) - sol.Net.GetValueOrDefault(i)))
            .Where(x => x.need > 1e-3).ToList();

        // Rounding: trace the whole chain back to the miners with a single flow scale factor.
        //  SteadyRate: every machine & miner rounded UP at 100% → flow can rise to the tightest ceil(n)/n (≥1).
        //  NoClog:     miners rounded DOWN → flow limited to the scarcest resource (≤1); machines rounded up so they never back up.
        double scale = 1;
        if (s.Mode == RoundingMode.SteadyRate)
        {
            scale = double.MaxValue;
            foreach (var n in sol.Machines.Values) scale = Math.Min(scale, Math.Ceiling(n - 1e-9) / n);
            foreach (var (item, need) in raw.Where(r => GameData.RawResources.Contains(r.item) && s.IsResourceAllowed(r.item)))
            {
                var a = s.Allocate(item, need);
                if (a.Unmet <= 1e-6) scale = Math.Min(scale, a.CeilCapacity / need);
            }
            if (scale == double.MaxValue) scale = 1;
        }
        else if (s.Mode == RoundingMode.NoClog)
        {
            foreach (var (item, need) in raw.Where(r => GameData.RawResources.Contains(r.item) && s.IsResourceAllowed(r.item)))
            {
                var a = s.Allocate(item, need);
                if (a.FloorCapacity > 1e-9) scale = Math.Min(scale, a.FloorCapacity / need);
                else plan.Warnings.Add(Loc.T("warn.lessThanOne", GameData.Item(item).Name));
            }
        }
        plan.Scale = scale;

        foreach (var (r, n) in sol.Machines.OrderBy(kv => kv.Key.Tier).ThenBy(kv => GameData.Item(sol.Owner[kv.Key]).Name))
        {
            var flow = n * scale;
            int count = (int)Math.Ceiling(flow - 1e-9);
            double clock = fullClock ? 100 : flow / count * 100;
            var item = sol.Owner[r];
            var bPower = r.MinPower is { } mn && r.MaxPower is { } mx ? (mn + mx) / 2
                : GameData.Buildings.TryGetValue(r.Building, out var b) ? b.Power : 0;
            var power = count * bPower * Math.Pow(clock / 100, PowerExp);
            plan.Power += power;
            var uses = new List<string>();
            if (sol.Demand.TryGetValue(item, out var d)) uses.Add(Loc.T("goes.target", Fmt(d * scale)));
            foreach (var (cr, cn) in sol.Machines)
            {
                var used = cr.In.Where(a => a.Item == item).Sum(a => cr.PerMin(a.Value)) * cn * scale;
                if (used > 1e-9) uses.Add($"{Fmt(used)} → {GameData.Item(sol.Owner[cr]).Name}");
            }
            string Io(List<Amount> list) => string.Join(", ", list.Select(a => $"{Fmt(r.PerMin(a.Value) * flow)} {GameData.Item(a.Item).Name}"));
            var row = new MachineRow
            {
                Item = item,
                Options = [RecipeDef.ImportOption(), .. s.OptionsFor(item)],
                Recipe = r,
                Exact = flow,
                Count = count,
                Clock = clock,
                CountText = s.Mode == RoundingMode.Exact ? $"{Fmt(flow)}  (→ {count})" : count.ToString(),
                OutputRate = r.NetPerMachine(item) * flow,
                Capacity = r.NetPerMachine(item) * count,
                Inputs = Io(r.In),
                GoesTo = string.Join(", ", uses),
                Outputs = Io(r.Out),
                Power = power
            };
            plan.Machines.Add(row);
            plan.AddTotal(r.Building, count, power, false);
        }

        foreach (var (item, need) in raw.OrderBy(x => GameData.Item(x.item).Name))
        {
            var rate = need * scale;
            if (!GameData.RawResources.Contains(item))
            {
                plan.Resources.Add(new ResourceRow { Item = item, Rate = rate, Source = Loc.T("import"), Note = Loc.T(s.IsSupplied(item) ? "note.supplied" : "note.noRecipe") });
                continue;
            }
            if (!s.IsResourceAllowed(item))
            {
                var why = Settings.ResourceTier(item) > s.MaxTier ? Loc.T("why.tier", Settings.ResourceTier(item)) : Loc.T("why.marked");
                plan.Resources.Add(new ResourceRow { Item = item, Rate = rate, Source = Loc.T("import"), Note = Loc.T("note.notAccessible", why) });
                plan.Warnings.Add(Loc.T("warn.notAccessible", GameData.Item(item).Name, why));
                continue;
            }
            var limit = s.EffectiveLimit(item);
            if (limit != null && rate > limit + 1e-6)
                plan.Warnings.Add(Loc.T("warn.overLimit", GameData.Item(item).Name, Fmt(rate), Fmt(limit.Value)));
            var ex = s.ExtractorFor(item);
            var alloc = s.Allocate(item, rate);
            var parts = new List<string>();
            var clocks = new List<string>();
            double left = rate;
            bool listed = s.NodeCapacity(item) != null;
            foreach (var u in alloc.Uses)
            {
                var label = listed ? $"{u.Count}× {Loc.T("purity." + u.Purity)}" : $"{u.Count}";
                string Who(string p) => listed ? Loc.T("purity." + p) + ": " : "";
                parts.Add(label);
                var used = Math.Min(left, u.Count * u.RatePerExtractor);
                left -= used;
                double lastClock = s.ExtractorClock * (used - (u.Count - 1) * u.RatePerExtractor) / u.RatePerExtractor;
                double c = fullClock ? s.ExtractorClock : lastClock;
                if (!fullClock && u.Count > 0 && Math.Abs(c - s.ExtractorClock) > 1e-6)
                    clocks.Add(u.Count > 1 ? $"{Who(u.Purity)}{u.Count - 1}@{s.ExtractorClock:0}% + 1@{c:0.#}%" : $"{Who(u.Purity)}{c:0.#}%");
                // power: full-clock extractors plus the (possibly underclocked) last one
                var exPower = (u.Count - 1) * ex.Power * Math.Pow(s.ExtractorClock / 100, PowerExp)
                              + ex.Power * Math.Pow(c / 100, PowerExp);
                plan.Power += exPower;
                plan.AddTotal(ex.Building, 0, exPower, true);
            }
            plan.AddTotal(ex.Building, alloc.Uses.Sum(u => u.Count), 0, true);
            string note = "";
            if (alloc.Unmet > 1e-6) note = Loc.T("note.short", Fmt(alloc.Unmet));
            else if (fullClock && alloc.CeilCapacity - rate > 1e-6)
                note = Loc.T("note.spare", Fmt(alloc.CeilCapacity - rate));
            else if (s.Mode == RoundingMode.NoClog && clocks.Count > 0) note = Loc.T("note.underclock");
            plan.Resources.Add(new ResourceRow
            {
                Item = item,
                Rate = rate,
                Source = GameData.Buildings.TryGetValue(ex.Building, out var b) ? b.Name : ex.Building,
                SourceIcon = ImageCache.Get(ex.Building),
                CountText = string.Join(" + ", parts),
                ExtractorCount = alloc.Uses.Sum(u => u.Count),
                ClockText = clocks.Count == 0 ? $"{s.ExtractorClock:0}%" : string.Join("; ", clocks),
                Note = note
            });
        }

        foreach (var (item, net) in sol.Net.Where(kv => kv.Value > 1e-4).OrderBy(kv => GameData.Item(kv.Key).Name))
        {
            var demand = sol.Demand.GetValueOrDefault(item);
            if (demand > 0)
                plan.Outputs.Add(new FlowRow { Item = item, Rate = net * scale, IsTarget = true, Kind = Loc.T("out.target", Fmt(demand)) + (Math.Abs(scale - 1) > 1e-9 ? Loc.T("out.actual", Fmt(net * scale)) : "") });
            if (net - demand > 1e-4)
            {
                // in Drained mode an intermediate that machines also consume is just unused capacity (it backs up), not waste
                bool headroom = drained && sol.Machines.Keys.Any(r => r.In.Any(a => a.Item == item));
                plan.Outputs.Add(new FlowRow { Item = item, Rate = (net - demand) * scale, IsSurplus = !headroom, Kind = Loc.T(headroom ? "out.headroom" : "out.byproduct") });
            }
        }
        plan.BeltNote = (item, rate) =>
        {
            var b = s.BeltFor(item);
            int n = (int)Math.Ceiling(rate / b.rate - 1e-6);
            return n > 1 ? Loc.T("edge.belts", n, GameData.Buildings.TryGetValue(b.cls, out var bd) ? bd.Name : b.cls) : null;
        };
        if (!drained) plan.AddLineRows(s);
        plan.CountLogistics(s, sol);
        plan.Guards = ClogGuards.Find(plan, sol);
        foreach (var g in plan.Guards.Where(g => !g.AllAway))
        {
            if (g.Fluid) { plan.AddTotal(ClogGuards.Valve, g.Lines, 0, false, true); plan.AddTotal(ClogGuards.Junction, g.Lines, 0, false, true); }
            // in Drained mode the belt's own layout may already be a smart splitter with this surplus on Overflow
            else if (!plan.Splits.Any(p => p.Item == g.Item && p.Kind == SplitKind.Smart && p.Overflow?.Any(k => k.StartsWith("S:")) == true))
                plan.AddTotal(ClogGuards.Smart, g.Lines, 0, false, true);
        }
        int required = plan.Guards.Count(g => g.Level == GuardLevel.Required);
        if (required > 0) plan.Warnings.Add(Loc.T("warn.clog", required));
        plan.Totals = plan.Totals.OrderBy(t => t.IsExtractor ? 0 : t.IsLogistics ? 2 : 1).ThenByDescending(t => t.Count).ToList();
        plan.BuildGraph(sol, scale, s);
        return plan;
    }

    /// <summary>
    /// "Drained outputs": final products are pulled away constantly, so they never back up, and a splitter only passes a
    /// share on when an output is blocked. For every item that feeds several consumers, pick a splitter layout (≤ 2 in
    /// series) or a manifold, and supply what that layout needs so EVERY consumer — the weakest included — gets its full
    /// need (greedy consumers keep what reaches them). That extra supply becomes demand on the item, flows upstream, and
    /// the whole thing repeats until stable. Machines then run at 100% (rounded up).
    /// </summary>
    static SolveResult DrainedSolve(Settings s)
    {
        var extra = new Dictionary<string, double>();
        SolveResult sol = null!;
        List<SplitPlan> splits = new();
        HashSet<RecipeDef>? recipes = null; // optimizer: fix the recipe set after the first pass so iterations converge
        // producers split over several belt lines are rounded up per line (their surplus can't cross to another line)
        var lineWhole = new Dictionary<RecipeDef, double>();
        double Whole(RecipeDef r, double x) => Math.Max(Math.Ceiling(x - 1e-6), lineWhole.GetValueOrDefault(r));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool converged = false, timedOut = false;
        for (int iter = 0; iter < 40; iter++)
        {
            // time budget: huge plans stop refining rather than freezing the window
            if (clock.ElapsedMilliseconds > 1500) { timedOut = true; break; }
            sol = s.Optimize ? Optimizer.Solve(s, recipes, extra) : Solver.Solve(s, extra);
            if (s.Optimize) recipes ??= sol.Machines.Keys.ToHashSet();

            var consumers = new Dictionary<string, List<SplitConsumer>>();
            void Add(string item, SplitConsumer c)
            {
                if (c.Need <= 1e-9 && c.Cap <= 1e-9) return;
                if (!consumers.TryGetValue(item, out var l)) consumers[item] = l = new();
                l.Add(c);
            }
            foreach (var (r, x) in sol.Machines)
            {
                var whole = Whole(r, x);
                var label = GameData.Item(sol.Owner[r]).Name;
                foreach (var a in r.In) Add(a.Item, new SplitConsumer("R:" + r.ClassName, label, x * r.PerMin(a.Value), whole * r.PerMin(a.Value), (int)whole));
            }
            foreach (var (item, d) in sol.Demand) // every drained output, even one no machine uses (it may need several belts)
                Add(item, new SplitConsumer("T:" + item, Loc.T("node.target"), d, double.PositiveInfinity));
            // surplus of an item that machines also use has to leave somewhere (sink/storage) — also an uncapped consumer
            foreach (var (item, net) in sol.Net)
            {
                var surplus = net - sol.Demand.GetValueOrDefault(item) - extra.GetValueOrDefault(item);
                if (surplus > 1e-4 && consumers.ContainsKey(item))
                    Add(item, new SplitConsumer("S:" + item, Loc.T("node.sink"), surplus, double.PositiveInfinity));
            }

            var next = new Dictionary<string, double>();
            splits = new();
            var nextWhole = new Dictionary<RecipeDef, double>();
            foreach (var (item, cs) in consumers.OrderBy(kv => kv.Key))
            {
                var belt = s.BeltFor(item);
                // backing up is fine for an intermediate only machines use, made by machines without byproducts: its belt
                // fills and every consumer gets its share — no extra supply (one belt's worth; more lines keep the plan)
                if (s.BackUpIntermediates && cs.All(c => c.Key.StartsWith("R:")) && cs.Sum(c => c.Need) <= belt.rate
                    && sol.Machines.Keys.Where(r => r.Out.Any(o => o.Item == item)).All(r => r.Out.Count == 1)) continue;
                if (cs.Count <= 1 && cs.Sum(c => c.Need) <= belt.rate) continue; // one consumer on one belt: nothing to plan
                // safety net: once the time budget is gone, skip line packing for the remaining items
                var lines = clock.ElapsedMilliseconds > 3000
                    ? [Splitters.Plan(item, cs, s.TwoSplittersInRow)]
                    : Splitters.PlanLines(item, cs, s.TwoSplittersInRow, belt.rate, s.BeltLabel(item));
                splits.AddRange(lines);
                var supply = lines.Sum(p => p.Supply);
                var need = cs.Sum(c => c.Need);
                if (supply - need > 1e-6) next[item] = supply - need;
                if (lines.Count > 1)
                {
                    // main producer of this item: one machine group per line, each rounded up
                    var main = sol.Machines.Keys.Where(r => r.Out.Any(o => o.Item == item)).OrderByDescending(r => sol.Machines[r] * r.NetPerMachine(item)).FirstOrDefault();
                    if (main != null && main.NetPerMachine(item) > 0)
                        nextWhole[main] = Math.Max(nextWhole.GetValueOrDefault(main), lines.Sum(p => Math.Ceiling(p.Supply / main.NetPerMachine(item) - 1e-6)));
                }
            }
            // never lower what a previous pass needed: recipe loops (e.g. recycled rubber ⇄ plastic) otherwise make the extra
            // supply swing back and forth; extra supply is always safe here, so taking the max settles in a few passes
            foreach (var (k, v) in extra) next[k] = Math.Max(next.GetValueOrDefault(k), v);
            foreach (var (k, v) in lineWhole) nextWhole[k] = Math.Max(nextWhole.GetValueOrDefault(k), v);
            bool sameWhole = nextWhole.Count == lineWhole.Count && nextWhole.All(kv => lineWhole.GetValueOrDefault(kv.Key) == kv.Value);
            lineWhole = nextWhole;
            bool same = sameWhole && next.Count == extra.Count && next.All(kv => kv.Value - extra.GetValueOrDefault(kv.Key) < 1e-3);
            extra = next;
            if (same) { converged = true; break; }
        }
        if (!converged) sol.Warnings.Add(Loc.T(timedOut ? "warn.notConverged" : "warn.notSettled"));

        // machines rounded up at 100%; production of each split item covers its layout's supply
        var full = new SolveResult { Demand = sol.Demand, Warnings = sol.Warnings, Owner = sol.Owner, Splits = splits };
        foreach (var w in splits.Select(p => p.Warning).OfType<string>().Distinct()) full.Warnings.Add(w);
        foreach (var (r, x) in sol.Machines)
        {
            var c = Whole(r, x);
            if (c <= 0) continue;
            full.Machines[r] = c;
            foreach (var it in r.In.Concat(r.Out).Select(a => a.Item).Distinct())
                full.Net[it] = full.Net.GetValueOrDefault(it) + c * r.NetPerMachine(it);
        }
        foreach (var i in sol.Inputs) full.Inputs.Add(i);
        foreach (var (i, n) in full.Net) if (n < -1e-6 && GameData.RawResources.Contains(i)) full.Inputs.Add(i);
        return full;
    }

    const string Splitter = "Desc_ConveyorAttachmentSplitter_C", SmartSplitter = "Desc_ConveyorAttachmentSplitterSmart_C",
                 Merger = "Desc_ConveyorAttachmentMerger_C", Junction = "Desc_PipelineJunction_Cross_C";

    /// <summary>
    /// Belt/pipe parts for every item flow:
    ///  • split point: the planned layout (Drained mode) — its splitters / smart splitter, plus mergers where several
    ///    splitter outputs go to the same consumer; otherwise one manifold across all consuming machines
    ///  • each consuming machine group of n machines is fed by its own manifold: n-1 splitters
    ///  • outputs of n producing machines/extractors merge onto one belt: n-1 mergers
    ///  • "splitter at the end of every manifold" adds one (does nothing, looks tidy)
    /// Fluids use pipeline junctions for both splitting and merging.
    /// </summary>
    void CountLogistics(Settings s, SolveResult sol)
    {
        int end = s.EndSplitter ? 1 : 0;
        int Manifold(int units) => units >= 2 ? units - 1 + end : 0;
        var items = Machines.SelectMany(m => m.Recipe.In.Concat(m.Recipe.Out)).Select(a => a.Item)
            .Concat(Outputs.Select(o => o.Item)).Distinct();
        foreach (var item in items)
        {
            bool fluid = GameData.Item(item).IsFluid;
            string split = fluid ? Junction : Splitter, merge = fluid ? Junction : Merger;
            var consumers = Machines.Where(m => m.Recipe.In.Any(a => a.Item == item)).ToList();
            int drains = Outputs.Count(o => o.Item == item && (o.IsTarget || o.IsSurplus));
            var lines = Splits.Where(p => p.Item == item).ToList();

            if (lines.Count > 0 && lines[0].Line > 0 && (lines[0].MachinesPerConsumer.Count > 0 || lines[0].DrainsOnLine > 0))
            {
                // planned layouts, line by line (Drained mode)
                foreach (var p in lines)
                {
                    int machineUnits = p.MachinesPerConsumer.Values.Sum();
                    int drainUnits = p.DrainsOnLine;
                    if (p.Kind is SplitKind.Splitter or SplitKind.Smart && !fluid)
                    {
                        AddTotal(p.Kind == SplitKind.Smart ? SmartSplitter : Splitter, p.SplitterCount, 0, false, true);
                        AddTotal(merge, p.OutputsPerConsumer.Values.Sum(n => Math.Max(0, n - 1)), 0, false, true);
                        foreach (var m in p.MachinesPerConsumer.Values) AddTotal(split, Manifold(m), 0, false, true);
                    }
                    else AddTotal(split, Manifold(machineUnits + drainUnits), 0, false, true);
                }
            }
            else if (consumers.Count > 0 || drains > 0)
            {
                // one manifold per belt line
                int units = consumers.Sum(m => m.Count) + drains;
                int n = Math.Max(1, lines.Count > 0 ? lines[0].Lines : 1);
                if (units > n) AddTotal(split, units - n + end * n, 0, false, true);
            }

            int producers = Machines.Where(m => m.Recipe.Out.Any(a => a.Item == item)).Sum(m => m.Count)
                            + Resources.Where(r => r.Item == item).Sum(r => r.ExtractorCount);
            int producerLines = Math.Max(1, lines.Count > 0 ? lines[0].Lines : 1);
            if (producers > producerLines && (consumers.Count > 0 || drains > 0)) AddTotal(merge, producers - producerLines, 0, false, true);
        }
        Totals.RemoveAll(t => t.IsLogistics && t.Count == 0);
    }

    /// <summary>Other rounding modes: list flows that need several parallel belts (informational; no split planning).</summary>
    void AddLineRows(Settings s)
    {
        var flows = new Dictionary<string, double>();
        foreach (var m in Machines)
            foreach (var a in m.Recipe.Out) flows[a.Item] = flows.GetValueOrDefault(a.Item) + m.Recipe.PerMin(a.Value) * m.Exact;
        foreach (var r in Resources) flows[r.Item] = flows.GetValueOrDefault(r.Item) + r.Rate;
        foreach (var (item, flow) in flows.OrderBy(kv => GameData.Item(kv.Key).Name))
        {
            var belt = s.BeltFor(item);
            int n = (int)Math.Ceiling(flow / belt.rate - 1e-6);
            if (n <= 1) continue;
            Splits.Add(new SplitPlan
            {
                Item = item, Supply = flow, Need = flow, Kind = SplitKind.Manifold, Lines = n, Line = 0,
                Belt = s.BeltLabel(item), Layout = Loc.T("split.lines", n)
            });
        }
    }

    void AddTotal(string building, int count, double power, bool extractor, bool logistics = false)
    {
        var t = Totals.FirstOrDefault(x => x.Building == building);
        if (t == null) Totals.Add(t = new BuildingTotal { Building = building, IsExtractor = extractor, IsLogistics = logistics });
        t.Count += count;
        t.Power += power;
    }

    /// <summary>
    /// Flow graph for the tree diagram, split into production lines.
    /// An item whose flow needs several belts runs as independent lines: its main producer is drawn as one box per line
    /// (machines per line from the Drained line plans, or an even split in other modes), raw resources likewise, and each
    /// line only feeds the consumers on that line. Consumer machines on an item's lines are matched in order to that
    /// consumer's own production lines, so links form bands rather than every-line-to-every-line. Within one line,
    /// several producers share each consumer's intake by their share of the line's production.
    /// </summary>
    void BuildGraph(SolveResult sol, double scale, Settings s)
    {
        // ---- line structure per item ----
        var planLines = Splits.Where(p => p.Line >= 1).GroupBy(p => p.Item).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Line).ToList());
        int RealLines(string item) =>
            planLines.TryGetValue(item, out var pl) ? pl.Count
            : Splits.Where(p => p.Item == item).Select(p => p.Lines).DefaultIfEmpty(1).Max();
        // a diagram with hundreds of line boxes is unreadable (and slow to lay out): then keep one box per group
        bool showLines = Machines.Sum(m => Math.Min(m.Count, RealLines(m.Item))) + Resources.Sum(r => RealLines(r.Item)) <= 200;
        int LinesOf(string item) => showLines ? RealLines(item) : 1;
        double[] LineWeights(string item)
        {
            int n = LinesOf(item);
            if (planLines.TryGetValue(item, out var pl) && pl.Count == n) return pl.Select(p => Math.Max(1e-9, p.Supply)).ToArray();
            return Enumerable.Repeat(1.0, n).ToArray();
        }

        // main producer of each item (the recipe whose production lines follow that item's belt lines)
        var mainProducer = new Dictionary<string, MachineRow>();
        foreach (var g in Machines.SelectMany(m => m.Recipe.Out.Select(o => (item: o.Item, row: m))).GroupBy(x => x.item))
            mainProducer[g.Key] = g.OrderByDescending(x => x.row.Recipe.PerMin(x.row.Recipe.Out.First(o => o.Item == g.Key).Value) * x.row.Exact).First().row;

        // ---- machine nodes: one per production line ----
        var nodeLines = new Dictionary<MachineRow, List<(string key, int machines)>>();
        foreach (var row in Machines)
        {
            var baseKey = "R:" + row.Recipe.ClassName;
            int n = LinesOf(row.Item);
            bool split = n > 1 && mainProducer.GetValueOrDefault(row.Item) == row && row.Count >= n;
            var counts = split ? Apportion(row.Count, LineWeights(row.Item)) : [row.Count];
            var list = new List<(string, int)>();
            for (int k = 0; k < counts.Length; k++)
            {
                if (counts[k] <= 0) continue;
                var key = split ? $"{baseKey}#{k + 1}" : baseKey;
                double share = row.Count > 0 ? (double)counts[k] / row.Count : 1;
                Nodes.Add(new GraphNode
                {
                    Key = key, Kind = NodeKind.Machine, Item = row.Item, Building = row.Recipe.Building,
                    Recipe = row.Recipe, Machines = counts[k],
                    Title = row.ItemName + (split ? "  ·  " + Loc.T("node.line", k + 1, counts.Length) : ""),
                    Guard = Guards.Where(g => g.Row == row).Select(g => $"{g.ItemName} ({g.LevelText})").DefaultIfEmpty().Aggregate((a, b) => a + ", " + b),
                    Subtitle = split ? $"{counts[k]}× {row.BuildingName}"
                        : $"{(s.Mode == RoundingMode.Exact ? Fmt(row.Exact) : row.Count.ToString())}× {row.BuildingName}"
                          + (RealLines(row.Item) > 1 ? "  ·  " + Loc.T("node.inLines", RealLines(row.Item)) : ""),
                    Rate = row.OutputRate * share
                });
                list.Add((key, counts[k]));
            }
            nodeLines[row] = list;
        }

        // ---- per item, per line: producers and consumers ----
        var prodOnLine = new Dictionary<(string item, int line), List<(string key, double rate)>>();
        var consOnLine = new Dictionary<(string item, int line), List<(string key, double rate)>>();
        void Add(Dictionary<(string, int), List<(string, double)>> d, string item, int line, string key, double rate)
        {
            if (rate <= 1e-6) return;
            if (!d.TryGetValue((item, line), out var l)) d[(item, line)] = l = new();
            l.Add((key, rate));
        }

        foreach (var row in Machines)
        {
            double util = row.Count > 0 ? row.Exact / row.Count : 1; // fraction each machine runs
            var mine = nodeLines[row];
            // outputs
            foreach (var a in row.Recipe.Out)
            {
                int n = LinesOf(a.Item);
                double perMachine = row.Recipe.PerMin(a.Value) * util;
                if (mine.Count == n && mainProducer.GetValueOrDefault(a.Item) == row)
                    for (int k = 0; k < n; k++) Add(prodOnLine, a.Item, k, mine[k].key, perMachine * mine[k].machines);
                else
                    for (int j = 0; j < mine.Count; j++) Add(prodOnLine, a.Item, j % n, mine[j].key, perMachine * mine[j].machines);
            }
            // inputs: this group's machines on each line of the input item, matched in order to its own lines
            foreach (var a in row.Recipe.In)
            {
                int n = LinesOf(a.Item);
                double perMachine = row.Recipe.PerMin(a.Value) * util;
                int[] onLine;
                if (planLines.TryGetValue(a.Item, out var pl) && pl.Count == n)
                {
                    var raw = pl.Select(p => (double)p.MachinesPerConsumer.GetValueOrDefault("R:" + row.Recipe.ClassName)).ToArray();
                    onLine = raw.Sum() > 0 ? Apportion(row.Count, raw) : Apportion(row.Count, Enumerable.Repeat(1.0, n).ToArray());
                }
                else onLine = Apportion(row.Count, Enumerable.Repeat(1.0, n).ToArray());
                foreach (var (line, key, machines) in Match(onLine, mine))
                    Add(consOnLine, a.Item, line, key, perMachine * machines);
            }
        }

        foreach (var r in Resources)
        {
            bool import = !GameData.RawResources.Contains(r.Item) || !s.IsResourceAllowed(r.Item);
            int n = LinesOf(r.Item);
            var w = LineWeights(r.Item);
            var extractors = n > 1 ? Apportion(Math.Max(r.ExtractorCount, n), w) : [r.ExtractorCount];
            for (int k = 0; k < n; k++)
            {
                var key = n > 1 ? $"X:{r.Item}#{k + 1}" : "X:" + r.Item;
                double rate = r.Rate * w[k] / w.Sum();
                Nodes.Add(new GraphNode
                {
                    Key = key, Kind = import ? NodeKind.Import : NodeKind.Raw, Item = r.Item,
                    Building = import ? null : s.ExtractorFor(r.Item).Building,
                    Title = r.ItemName + (n > 1 ? "  ·  " + Loc.T("node.line", k + 1, n) : ""),
                    Subtitle = import ? r.Source : n > 1 ? $"{extractors[k]}× {r.Source}" : $"{r.CountText}× {r.Source}",
                    Rate = rate
                });
                Add(prodOnLine, r.Item, k, key, rate);
            }
        }

        foreach (var o in Outputs)
        {
            var key = (o.IsTarget ? "T:" : "S:") + o.Item;
            Nodes.Add(new GraphNode
            {
                Key = key, Kind = o.IsTarget ? NodeKind.Target : NodeKind.Surplus, Item = o.Item,
                Title = o.ItemName, Subtitle = o.IsTarget ? Loc.T("node.target") : Loc.T("node.surplus"), Rate = o.Rate
            });
            int n = LinesOf(o.Item);
            if (n == 1) { Add(consOnLine, o.Item, 0, key, o.Rate); continue; }
            // which lines carry this output: in Drained mode the lines with a drained output on them; else all
            double[] w = planLines.TryGetValue(o.Item, out var pl) && pl.Count == n && pl.Any(p => p.DrainsOnLine > 0)
                ? pl.Select(p => p.DrainsOnLine > 0 ? Math.Max(1e-6, p.Supply - p.MachinesPerConsumer.Sum(kv => kv.Value) * 0) : 0).ToArray()
                : Enumerable.Repeat(1.0, n).ToArray();
            if (!o.IsTarget) w = Enumerable.Range(0, n).Select(i => i == n - 1 ? 1.0 : 0).ToArray(); // surplus leaves from the last line
            for (int k = 0; k < n; k++) if (w[k] > 0) Add(consOnLine, o.Item, k, key, o.Rate * w[k] / w.Sum());
        }

        // ---- edges, line by line (lines never exchange items) ----
        foreach (var ((item, line), cons) in consOnLine)
        {
            if (!prodOnLine.TryGetValue((item, line), out var prods)) continue;
            var total = prods.Sum(p => p.rate);
            foreach (var (ck, cr) in cons)
                foreach (var (pk, pr) in prods)
                    if (pk != ck && cr * pr / total > 1e-4)
                        Edges.Add(new GraphEdge(pk, ck, item, cr * pr / total));
        }
    }

    /// <summary>Splits an integer total by weights (largest remainder), so the parts add up exactly.</summary>
    static int[] Apportion(int total, double[] weights)
    {
        var sum = weights.Sum();
        if (sum <= 0 || total <= 0) return weights.Select(_ => 0).ToArray();
        var exact = weights.Select(w => total * w / sum).ToArray();
        var parts = exact.Select(x => (int)Math.Floor(x)).ToArray();
        foreach (var i in Enumerable.Range(0, weights.Length).OrderByDescending(i => exact[i] - parts[i]).Take(total - parts.Sum())) parts[i]++;
        return parts;
    }

    /// <summary>Pairs machines on an input item's lines with the consumer's own production lines, in order
    /// (two-pointer), so line 1 of the input feeds the first production lines, and so on.</summary>
    static IEnumerable<(int line, string key, int machines)> Match(int[] onLine, List<(string key, int machines)> own)
    {
        int j = 0, leftInOwn = own.Count > 0 ? own[0].machines : 0;
        for (int line = 0; line < onLine.Length; line++)
        {
            int need = onLine[line];
            while (need > 0 && j < own.Count)
            {
                int take = Math.Min(need, leftInOwn);
                if (take > 0) yield return (line, own[j].key, take);
                need -= take; leftInOwn -= take;
                if (leftInOwn == 0 && ++j < own.Count) leftInOwn = own[j].machines;
            }
            if (need > 0 && own.Count > 0) yield return (line, own[^1].key, need); // rounding leftovers
        }
    }
}
