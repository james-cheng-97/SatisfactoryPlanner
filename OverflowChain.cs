namespace SatisfactoryPlanner;

/// <summary>
/// Overflow chains (Clog guards → how to process). A clogging surplus is only safe once it ends somewhere that never
/// fills: an AWESOME Sink (items) or a generator (fuels). A box fills up, and an output isn't guaranteed to be taken.
/// Processing a surplus with a recipe makes a NEW overflow (e.g. resin → Residual Plastic → plastic): that product
/// never counts towards the plan's targets (it only exists while there is surplus), so it's kept as its own item,
/// "&lt;item&gt;@ovf", with its own handling — processed again, burnt, sunk, or merged into the product's output through a
/// smart splitter whose Overflow goes to a sink (so a full output can't back the chain up).
/// The main plan is solved as usual; the chain is added on top (machines, water, power) without changing it.
/// </summary>
public static class OverflowChain
{
    public const string Tag = "@ovf";
    public const string GenPrefix = "gen:", Merge = "merge", BurnPrefix = "__burn__";

    public static string Of(string item) => GameData.BaseItem(item) + Tag;
    public static bool IsOverflow(string item) => item.Contains(Tag);

    /// <summary>Generators (building, MW, water /min per generator) and the energy (MJ per unit) of what they burn.</summary>
    static readonly (string building, double mw, double water, string[] fuels)[] Gens =
    [
        ("Desc_GeneratorFuel_C", 250, 0, ["Desc_LiquidFuel_C", "Desc_LiquidTurboFuel_C", "Desc_LiquidBiofuel_C", "Desc_RocketFuel_C", "Desc_IonizedFuel_C"]),
        ("Desc_GeneratorCoal_C", 75, 45, ["Desc_Coal_C", "Desc_CompactedCoal_C", "Desc_PetroleumCoke_C"]),
    ];
    static readonly Dictionary<string, double> Energy = new()
    {
        ["Desc_LiquidFuel_C"] = 750, ["Desc_LiquidTurboFuel_C"] = 2000, ["Desc_LiquidBiofuel_C"] = 750, ["Desc_RocketFuel_C"] = 3600,
        ["Desc_IonizedFuel_C"] = 5000, ["Desc_Coal_C"] = 300, ["Desc_CompactedCoal_C"] = 630, ["Desc_PetroleumCoke_C"] = 180,
    };

    /// <summary>Generators that burn an item: (building, fuel /min per generator, MW).</summary>
    public static List<(string building, double rate, double mw)> GeneratorsFor(string item)
    {
        var b = GameData.BaseItem(item);
        return Gens.Where(g => g.fuels.Contains(b) && Energy.ContainsKey(b)).Select(g => (g.building, g.mw * 60 / Energy[b], g.mw)).ToList();
    }

    /// <summary>A generator burning an item, as a recipe: one cycle a minute, no products, power out.</summary>
    public static RecipeDef? Burn(string item, string building)
    {
        var g = Gens.FirstOrDefault(x => x.building == building);
        var gen = GeneratorsFor(item).FirstOrDefault(x => x.building == building);
        if (g.building == null || gen.building == null) return null;
        var ins = new List<Amount> { new(item, gen.rate) };
        if (g.water > 0) ins.Add(new("Desc_Water_C", g.water));
        return new RecipeDef
        {
            ClassName = BurnPrefix + building + "|" + item, Name = GameData.Buildings.GetValueOrDefault(building)?.Name ?? building,
            Duration = 60, In = ins, Out = [], Building = building, PowerOut = g.mw, OverflowOf = item,
        };
    }

    /// <summary>A recipe processing an overflow: the overflow in, its products as new overflows.</summary>
    public static RecipeDef Process(RecipeDef r, string item) => new()
    {
        ClassName = r.ClassName + "|" + item, Name = r.Name, Duration = r.Duration, Building = r.Building, Alternate = r.Alternate,
        Tier = r.Tier, IsMam = r.IsMam, NoMamTier = r.NoMamTier, MinPower = r.MinPower, MaxPower = r.MaxPower, UnlockedBy = r.UnlockedBy,
        In = r.In.Select(a => a.Item == GameData.BaseItem(item) ? a with { Item = item } : a).ToList(),
        Out = r.Out.Select(a => a with { Item = Of(a.Item) }).ToList(),
        OverflowOf = item,
    };

    /// <summary>
    /// Adds the chains to a solved plan: every surplus with a handling set is processed / burnt as far as its chain goes
    /// (items made on the way are overflows of their own). What's left over stays a surplus (box, sink, or merged).
    /// </summary>
    public static void Apply(SolveResult sol, Settings s, Dictionary<string, double>? extra)
    {
        if (s.ClogHandling.Count == 0) return;
        var queue = new Queue<(string item, double rate, int depth)>();
        foreach (var (item, net) in sol.Net.ToList())
        {
            double surplus = net - sol.Demand.GetValueOrDefault(item) - (extra?.GetValueOrDefault(item) ?? 0);
            if (surplus > 1e-6 && !GameData.RawResources.Contains(item) && s.ClogHandling.ContainsKey(item)) queue.Enqueue((item, surplus, 0));
        }
        void Run(RecipeDef r, double machines)
        {
            sol.Machines[r] = sol.Machines.GetValueOrDefault(r) + machines;
            sol.Owner[r] = r.Out.Count > 0 ? r.Out[0].Item : r.OverflowOf!;
            foreach (var a in r.In)
            {
                sol.Net[a.Item] = sol.Net.GetValueOrDefault(a.Item) - r.PerMin(a.Value) * machines;
                if (GameData.RawResources.Contains(a.Item)) sol.Inputs.Add(a.Item);
            }
            foreach (var a in r.Out) sol.Net[a.Item] = sol.Net.GetValueOrDefault(a.Item) + r.PerMin(a.Value) * machines;
        }
        while (queue.Count > 0)
        {
            var (item, rate, depth) = queue.Dequeue();
            if (depth > 6 || !s.ClogHandling.TryGetValue(item, out var h)) continue;
            if (h.StartsWith(GenPrefix))
            {
                if (Burn(item, h[GenPrefix.Length..]) is { } burn) Run(burn, rate / burn.PerMin(burn.In[0].Value));
                continue;
            }
            var r = GameData.Recipes.FirstOrDefault(x => x.ClassName == h);
            if (r == null || !s.IsAvailable(r) || !r.In.Any(a => a.Item == GameData.BaseItem(item))) continue; // box / sink / merge: stays a surplus
            var p = Process(r, item);
            double machines = rate / p.PerMin(p.In.First(a => a.Item == item).Value);
            Run(p, machines);
            foreach (var o in p.Out) queue.Enqueue((o.Item, p.PerMin(o.Value) * machines, depth + 1));
        }
    }
}
