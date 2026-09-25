namespace SatisfactoryPlanner;

public enum RoundingMode { Exact, SteadyRate, NoClog, Drained }

public class Extractor
{
    public required string Building { get; init; }
    public required double RatePerUnit { get; init; } // at chosen clock
    public double Power { get; init; }
}

public class Settings
{
    public int MaxTier { get; set; } = 9;
    public bool IncludeAlternates { get; set; } = true;
    public bool IncludeMam { get; set; } = true;
    public string Miner { get; set; } = "Desc_MinerMk1_C";
    public double ExtractorClock { get; set; } = 100;
    public RoundingMode Mode { get; set; } = RoundingMode.Exact;
    public Dictionary<string, string> RecipeOverrides { get; set; } = new();
    public List<TargetSpec> Targets { get; set; } = new();
    public bool Optimize { get; set; }
    public string? Language { get; set; }
    public bool CountExtractors { get; set; } = true;
    /// <summary>Drained mode: allow two splitters in series between an output and its consumers (default one).</summary>
    public bool TwoSplittersInRow { get; set; }
    /// <summary>Drained mode: an intermediate that only machines use may back up on its belt — its splitters then
    /// balance by themselves, so it needs no extra supply — as long as every machine making it has no byproduct (a
    /// backed-up byproduct would stall it and cut production).</summary>
    public bool BackUpIntermediates { get; set; } // (off by default for now: see docs/LAYOUT_DESIGN.md)
    /// <summary>Also put a splitter in front of the last machine of each manifold (does nothing; for looks).</summary>
    public bool EndSplitter { get; set; }
    /// <summary>Belt / pipe tier to plan with (1-6 / 1-2); 0 = best unlocked at the current tier.</summary>
    public int BeltTier { get; set; }
    /// <summary>Layout: most floors the factory may use (1 = single floor).</summary>
    public int Floors { get; set; } = 1;
    /// <summary>Layout: blueprint tile size in foundations (0 = off, 5 = Mk2 designer, 6 = Mk3). Nothing may cross a
    /// tile border except belts and pipes, which are cut there.</summary>
    public int BlueprintTile { get; set; } = 6; // (the app plans for Mk3 blueprints by default)
    /// <summary>Layout engine: free placement + belt routing (board style) instead of machine rows.</summary>
    public bool PlaceAndRoute { get; set; } = true;
    /// <summary>Layout: machines may cross a blueprint tile border — they're left out of the blueprints and placed by hand.
    /// A group with at least one machine inside a tile lines the others up from it; a group with every machine placed by
    /// hand has its corner on a foundation corner (the anchor rule).</summary>
    public bool HandPlaceAcrossTiles { get; set; }
    /// <summary>Fluid input / output boxes: the Industrial Fluid Buffer (2400 m³, 12 m across) instead of the Fluid
    /// Buffer (400 m³, 4 m). Bigger layouts (plastic 4 -> 7 blueprints when measured).</summary>
    public bool IndustrialFluidBox { get; set; }
    /// <summary>How long the place &amp; route planner may search (s). Short limits make big plans fall back to the row layout.</summary>
    public int LayoutSeconds { get; set; } = 120;
    /// <summary>Plastic + rubber plan: the user's answer to "use the recycling loop?" (null = not asked yet).</summary>
    public bool? PolymerLoopAnswer { get; set; }
    public int PipeTier { get; set; }
    /// <summary>Items this factory imports instead of making them (delivered like a raw resource from a site elsewhere):
    /// plastic and rubber by default — they're usually made at a stand-alone oil site, and making a little here drags
    /// the whole plastic / rubber refinery loop into every factory. Anything can be added (the recipe list's
    /// "Import instead", or the Imported bar).</summary>
    public List<string> ImportedItems { get; set; } = [.. Polymers];
    public static readonly string[] Polymers = ["Desc_Plastic_C", "Desc_Rubber_C"];
    /// <summary>Older settings: "plastic and rubber as inputs" off → they're made here. (Read only, never written.)</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? SupplyPolymers { get => null; set { if (value == false) ImportedItems.RemoveAll(i => Polymers.Contains(i)); } }
    /// <summary>An item this plan takes as an input rather than making it (like a raw resource) — never one it's asked
    /// to produce.</summary>
    public bool IsSupplied(string item) => ImportedItems.Contains(item) && !Targets.Any(t => t.Item == item && t.Rate > 0);

    public static readonly (string cls, double rate)[] Belts =
    [
        ("Desc_ConveyorBeltMk1_C", 60), ("Desc_ConveyorBeltMk2_C", 120), ("Desc_ConveyorBeltMk3_C", 270),
        ("Desc_ConveyorBeltMk4_C", 480), ("Desc_ConveyorBeltMk5_C", 780), ("Desc_ConveyorBeltMk6_C", 1200),
    ];
    public static readonly (string cls, double rate)[] Pipes = [("Desc_Pipeline_C", 300), ("Desc_PipelineMK2_C", 600)];

    /// <summary>Best belt (or pipe for fluids) to use for an item: the chosen tier, or the best unlocked one.</summary>
    public (string cls, double rate) BeltFor(string item)
    {
        bool fluid = GameData.Item(item).IsFluid;
        var list = fluid ? Pipes : Belts;
        var (min, max) = AllowedTiers(fluid);
        int chosen = fluid ? PipeTier : BeltTier;
        return list[(chosen > 0 ? Math.Clamp(chosen, min, max) : max) - 1];
    }

    /// <summary>
    /// Belt tiers worth planning with at the current milestone tier: the best unlocked one and one below it
    /// (e.g. Mk.3–Mk.4 at Tier 6). Older belts are unrealistic for a factory that size and explode the line count.
    /// Pipes only have two tiers, so both stay available once unlocked.
    /// </summary>
    public (int min, int max) AllowedTiers(bool fluid)
    {
        var list = fluid ? Pipes : Belts;
        int best = 1;
        for (int i = 0; i < list.Length; i++)
            if (!GameData.Buildings.TryGetValue(list[i].cls, out var bd) || bd.UnlockTier <= MaxTier) best = i + 1;
        return (fluid ? 1 : Math.Max(1, best - 1), best);
    }

    public string BeltLabel(string item)
    {
        var b = BeltFor(item);
        return $"{(GameData.Buildings.TryGetValue(b.cls, out var bd) ? bd.Name : b.cls)} ({b.rate:0})";
    }
    /// <summary>Plan tabs; the active one's targets/overrides/modes are mirrored in the fields above while it's open.</summary>
    public List<PlanTab> Plans { get; set; } = new();
    public int ActivePlan { get; set; }
    public List<string> DisabledResources { get; set; } = new();
    public Dictionary<string, double> ResourceLimits { get; set; } = new();

    /// <summary>Tier at which a resource's extractor is unlocked.</summary>
    public static int ResourceTier(string item) => item switch
    {
        "Desc_Water_C" => 3, "Desc_LiquidOil_C" => 5, "Desc_NitrogenGas_C" => 8, _ => 0
    };

    public bool IsResourceAllowed(string item) =>
        !DisabledResources.Contains(item) && ResourceTier(item) <= (ResourceTierLimit ?? MaxTier);

    /// <summary>Overrides MaxTier for resource access (used when asking "what would I need to unlock").</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int? ResourceTierLimit { get; set; }

    /// <summary>Recipes unlocked in a loaded save file; when set it replaces the tier/alternate/MAM rules.</summary>
    public HashSet<string>? Unlocked { get; set; }
    public string? SaveName { get; set; }
    /// <summary>Quick on/off for the loaded save without unloading it (off = plan with the tab's tier/alt/MAM rules).</summary>
    public bool UseSave { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public bool SaveActive => Unlocked != null && UseSave;
    [System.Text.Json.Serialization.JsonIgnore] public bool IgnoreRecipeLocks { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public HashSet<string> ExtraRecipes { get; set; } = new();
    /// <summary>With IgnoreRecipeLocks: highest tier of locked recipes to consider.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int RelaxTier { get; set; } = 9;
    /// <summary>With IgnoreRecipeLocks: optimizer cost per machine of a locked recipe (to find the minimal unlock set).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public double LockedPenalty { get; set; }

    public bool IsUnlocked(RecipeDef r) =>
        ExtraRecipes.Contains(r.ClassName) || (SaveActive
            ? Unlocked!.Contains(r.ClassName) && r.Tier <= MaxTier
            : (IncludeMam ? r.Tier : r.NoMamTier) <= MaxTier && (IncludeAlternates || !r.Alternate));

    /// <summary>The milestone tier is a hard cap; relaxing only opens hard-drive / MAM recipes within it.</summary>
    public bool IsAvailable(RecipeDef r) => IsUnlocked(r) || (IgnoreRecipeLocks && r.Tier <= Math.Min(RelaxTier, MaxTier));

    /// <summary>A copy holding only what belongs to a plan tab (no save, language or other tabs).</summary>
    public Settings TabState()
    {
        var c = Clone();
        c.Plans = new();
        c.ActivePlan = 0;
        c.Unlocked = null;
        c.SaveName = null;
        c.Language = null;
        return c;
    }

    public Settings Clone()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(this))!;
        c.IgnoreRecipeLocks = IgnoreRecipeLocks;
        c.ExtraRecipes = new(ExtraRecipes);
        c.RelaxTier = RelaxTier;
        c.LockedPenalty = LockedPenalty;
        c.ResourceTierLimit = ResourceTierLimit;
        c.Unlocked = Unlocked; // read-only after load; share rather than copy
        return c;
    }

    public List<RecipeDef> OptionsFor(string item) =>
        GameData.Recipes.Where(r => IsAvailable(r) && r.Out.Any(o => o.Item == item))
            // order by class name, not display name, so default picks don't change with the UI language
            .OrderBy(r => r.Out[0].Item == item ? 0 : 1).ThenBy(r => r.Alternate).ThenBy(r => r.ClassName, StringComparer.Ordinal).ToList();

    public RecipeDef? RecipeFor(string item)
    {
        if (GameData.RawResources.Contains(item) || IsSupplied(item)) return null;
        var opts = OptionsFor(item);
        if (RecipeOverrides.TryGetValue(item, out var cls) && opts.FirstOrDefault(r => r.ClassName == cls) is { } o)
            return o;
        // default: standard main-product recipe, then a standard byproduct (e.g. Heavy Oil Residue from Plastic),
        // then alternates; packaging/unpackaging and matter conversion only as a last resort (they form loops)
        static bool Loopy(RecipeDef r) => r.Building is "Desc_Packager_C" or "Desc_Converter_C";
        return opts.FirstOrDefault(r => r.Out[0].Item == item && !r.Alternate && !Loopy(r))
               ?? opts.FirstOrDefault(r => !r.Alternate && !Loopy(r))
               ?? opts.FirstOrDefault(r => r.Out[0].Item == item && !Loopy(r))
               ?? opts.FirstOrDefault(r => !Loopy(r))
               ?? opts.FirstOrDefault();
    }

    /// <summary>Nodes the player has for each resource. No entry (or all blank) = unlimited normal nodes.</summary>
    public Dictionary<string, NodeCounts> Nodes { get; set; } = new();

    public static bool HasPurity(string item) => item != "Desc_Water_C";

    /// <summary>Extractor for a resource at 100% purity factor ("normal") and the chosen clock.</summary>
    public Extractor ExtractorFor(string item)
    {
        double clock = ExtractorClock / 100.0;
        string b; double baseRate;
        switch (item)
        {
            case "Desc_Water_C": b = "Desc_WaterPump_C"; baseRate = 120; break;
            case "Desc_LiquidOil_C": b = "Desc_OilPump_C"; baseRate = 120; break;
            case "Desc_NitrogenGas_C": b = "Desc_FrackingExtractor_C"; baseRate = 60; break;
            default:
                b = Miner;
                baseRate = Miner switch { "Desc_MinerMk2_C" => 120, "Desc_MinerMk3_C" => 240, _ => 60 };
                break;
        }
        var power = GameData.Buildings.TryGetValue(b, out var bd) ? bd.Power : 0;
        if (b == "Desc_FrackingExtractor_C") power = 150.0 / 4; // pressurizer shared by ~4 satellite nodes (rough)
        return new Extractor { Building = b, RatePerUnit = baseRate * clock, Power = power };
    }

    /// <summary>Node slots to fill, best purity first.</summary>
    public List<(string purity, double factor, int count)> NodeSlots(string item)
    {
        if (!HasPurity(item) || !Nodes.TryGetValue(item, out var n) || !n.Any)
            return [("Normal", 1.0, int.MaxValue)];
        var list = new List<(string, double, int)>();
        if (n.Pure > 0) list.Add(("Pure", 2.0, n.Pure.Value));
        if (n.Normal > 0) list.Add(("Normal", 1.0, n.Normal.Value));
        if (n.Impure > 0) list.Add(("Impure", 0.5, n.Impure.Value));
        return list;
    }

    /// <summary>Max extraction from the listed nodes, or null if nodes aren't limited.</summary>
    public double? NodeCapacity(string item)
    {
        var slots = NodeSlots(item);
        if (slots.Any(s => s.count == int.MaxValue)) return null;
        var per = ExtractorFor(item).RatePerUnit;
        return slots.Sum(s => s.count * s.factor * per);
    }

    /// <summary>Tightest of the manual per-minute limit and the node capacity.</summary>
    public double? EffectiveLimit(string item)
    {
        double? manual = ResourceLimits.TryGetValue(item, out var l) && l > 0 ? l : null;
        var cap = NodeCapacity(item);
        return manual == null ? cap : cap == null ? manual : Math.Min(manual.Value, cap.Value);
    }

    /// <summary>Fills nodes pure → normal → impure to extract <paramref name="need"/>/min.</summary>
    public NodeAllocation Allocate(string item, double need)
    {
        var per = ExtractorFor(item).RatePerUnit;
        var a = new NodeAllocation();
        double remaining = need;
        foreach (var (purity, factor, count) in NodeSlots(item))
        {
            if (remaining <= 1e-9) break;
            var r = per * factor;
            int k = (int)Math.Min(count, Math.Ceiling(remaining / r - 1e-9));
            int full = (int)Math.Min(k, Math.Floor(remaining / r + 1e-9));
            var used = Math.Min(remaining, k * r);
            a.Uses.Add(new NodeUse(purity, k, full, r));
            a.CeilCapacity += k * r;
            a.FloorCapacity += full * r;
            remaining -= used;
        }
        a.Unmet = Math.Max(0, remaining);
        return a;
    }
}

public class NodeCounts
{
    public int? Impure { get; set; }
    public int? Normal { get; set; }
    public int? Pure { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Any => Impure > 0 || Normal > 0 || Pure > 0;
}

/// <summary>Count = extractors used on that purity, Full = how many of them run at 100%.</summary>
public record NodeUse(string Purity, int Count, int Full, double RatePerExtractor);

public class NodeAllocation
{
    public List<NodeUse> Uses = new();
    public double CeilCapacity;   // every used extractor at full clock
    public double FloorCapacity;  // only the fully-used extractors
    public double Unmet;          // demand beyond all listed nodes
}

public class TargetSpec
{
    public string Item { get; set; } = "";
    public double Rate { get; set; } = 10;
}

public class SolveResult
{
    public Dictionary<RecipeDef, double> Machines = new();     // exact machine count at 100%
    public Dictionary<RecipeDef, string> Owner = new();        // item the recipe was chosen for
    public Dictionary<string, double> Net = new();             // net production /min (exact)
    public Dictionary<string, double> Demand = new();          // external demand (targets)
    public HashSet<string> Inputs = new();                     // raw or un-craftable items
    public List<string> Warnings = new();
    public double Score;                                       // weighted resource use (optimizer only)
    public List<SplitPlan> Splits = new();                     // belt split layouts (Drained mode)
}

/// <summary>
/// Solves the linear system "net production of each item = demand" with one chosen recipe per item.
/// Handles byproducts and loops (e.g. recycled plastic/rubber) since it's a full linear solve.
/// </summary>
public static class Solver
{
    /// <param name="extra">Additional consumption per item on top of the targets (used by the Drained rounding mode).</param>
    public static SolveResult Solve(Settings s, Dictionary<string, double>? extra = null)
    {
        var res = new SolveResult();
        foreach (var t in s.Targets.Where(t => t.Rate > 0 && t.Item != null && GameData.Items.ContainsKey(t.Item)))
            res.Demand[t.Item] = res.Demand.GetValueOrDefault(t.Item) + t.Rate;

        // discover the recipe graph
        var chosen = new Dictionary<string, RecipeDef>();
        var queue = new Queue<string>(res.Demand.Keys);
        var seen = new HashSet<string>();
        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (!seen.Add(item)) continue;
            var r = s.RecipeFor(item);
            if (r == null) { res.Inputs.Add(item); continue; }
            chosen[item] = r;
            foreach (var i in r.In) queue.Enqueue(i.Item);
        }

        var recipes = new List<RecipeDef>();
        foreach (var (item, r) in chosen)
            if (!res.Owner.ContainsKey(r)) { res.Owner[r] = item; recipes.Add(r); }

        // solve, dropping recipes that come out negative (need fully covered by byproducts)
        double[] x = Array.Empty<double>();
        for (int iter = 0; iter < 50; iter++)
        {
            int n = recipes.Count;
            var a = new double[n, n + 1];
            for (int row = 0; row < n; row++)
            {
                var item = res.Owner[recipes[row]];
                for (int col = 0; col < n; col++) a[row, col] = recipes[col].NetPerMachine(item);
                a[row, n] = res.Demand.GetValueOrDefault(item) + (extra?.GetValueOrDefault(item) ?? 0);
            }
            x = Gauss(a, n);
            if (x.Length == 0 && n > 0)
            {
                // a pure loop (e.g. package ⇄ unpackage) makes the system singular — let the optimizer
                // balance the same chosen recipes instead
                var looped = Optimizer.Solve(s, chosen.Values.ToHashSet(), extra);
                looped.Warnings.Insert(0, Loc.T("warn.loop"));
                return looped;
            }
            int neg = Array.FindIndex(x, v => v < -1e-9);
            if (neg < 0) break;
            res.Owner.Remove(recipes[neg]);
            recipes.RemoveAt(neg);
        }

        for (int i = 0; i < recipes.Count; i++)
        {
            if (x[i] < 1e-12) continue;
            res.Machines[recipes[i]] = x[i];
            foreach (var io in recipes[i].In.Concat(recipes[i].Out).Select(a => a.Item).Distinct())
                res.Net[io] = res.Net.GetValueOrDefault(io) + x[i] * recipes[i].NetPerMachine(io);
        }
        foreach (var (item, d) in res.Demand)
        {
            var net = res.Net.GetValueOrDefault(item);
            if (!res.Inputs.Contains(item) && net < d - 1e-6)
            {
                res.Inputs.Add(item);
                res.Warnings.Add(Loc.T("warn.deficit", GameData.Item(item).Name, d - net));
            }
        }
        foreach (var (item, net) in res.Net)
            if (net < -1e-6 && !res.Inputs.Contains(item)) res.Inputs.Add(item);
        return res;
    }

    static double[] Gauss(double[,] a, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[p, c])) p = r;
            if (Math.Abs(a[p, c]) < 1e-12) return Array.Empty<double>();
            if (p != c) for (int k = 0; k <= n; k++) (a[c, k], a[p, k]) = (a[p, k], a[c, k]);
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int k = c; k <= n; k++) a[r, k] -= f * a[c, k];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = a[i, n] / a[i, i];
        return x;
    }
}
