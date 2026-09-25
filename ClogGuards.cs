using System.Windows.Media;

namespace SatisfactoryPlanner;

public enum GuardLevel { Required, Recommended }

/// <summary>An overflow protection to place at one output of a multi-output machine group.</summary>
public class ClogGuard
{
    public required MachineRow Row { get; init; }
    public required string Item { get; init; }
    public ImageSource? MachineIcon => ImageCache.Get(Row.Recipe.Building);
    public string MachineText => $"{Row.Count}× {Row.BuildingName} ({Row.Recipe.Name})";
    public ImageSource? ItemIcon => ImageCache.Get(Item);
    public string ItemName => GameData.Item(Item).Name;
    public bool Fluid { get; init; }
    public GuardLevel Level { get; init; }
    public string LevelText => Loc.T(Level == GuardLevel.Required ? "guard.required" : "guard.recommended");
    public double Produced { get; init; }
    public double ConsumerNeed { get; init; }
    public double Surplus { get; init; }
    public int Lines { get; init; } = 1;
    /// <summary>Nothing consumes this output: no splitter/valve, the whole output just needs a route away.</summary>
    public bool AllAway { get; init; }
    public string Device => AllAway ? Loc.T("guard.routeAway") : (Lines > 1 ? $"{Lines}× " : "") + Loc.T(Fluid ? "guard.valve" : "guard.smart");
    public string Placement { get; init; } = "";
    /// <summary>How to process the surplus: "" = overflow into a box, "sink", or a recipe class.</summary>
    public List<ClogOption> Options { get; init; } = new();
    public ClogOption? Choice { get; set; }
}

/// <summary>One way to deal with a clogging surplus (Clog guards → how to process).</summary>
public record ClogOption(string Key, string Label);

/// <summary>
/// Clog detection. A machine with several outputs (refinery, blender, packager, particle accelerator, converter…)
/// stops completely as soon as ANY output backs up — e.g. a plastic refinery whose heavy oil residue can't leave also
/// stops making plastic. For every output of such a machine group:
///  • Required    — the plan makes more than anything consumes (a surplus): it WILL back up.
///  • Recommended — all of it is consumed, but only by machines that can themselves back up (then it clogs too).
///  • none        — it only goes to a final output (taken away).
/// Guard: items → smart splitter right at the output, consumers on a normal output, Overflow to wherever the player
/// likes; fluids → junction with a valve on the consumer branch set to their need, the other branch takes the rest.
/// </summary>
public static class ClogGuards
{
    public const string Valve = "Desc_Valve_C", Junction = "Desc_PipelineJunction_Cross_C", Smart = "Desc_ConveyorAttachmentSplitterSmart_C";

    /// <summary>Ways to deal with a surplus of an item: a box (fills up — not safe), an AWESOME Sink (items; not fluids),
    /// generators (fuels), merging into that product's output through a smart splitter with a sink on Overflow (an overflow
    /// of one of the plan's products), or a direct recipe (the surplus + raw resources only) whose products are new
    /// overflows to deal with in turn.</summary>
    public static List<ClogOption> OptionsFor(string item, Settings s)
    {
        var o = new List<ClogOption> { new("", Loc.T("clog.box")) };
        var baseItem = GameData.BaseItem(item);
        bool fluid = GameData.Item(item).IsFluid;
        if (!fluid) o.Add(new(Settings.SinkHandling, Loc.T("clog.sink")));
        foreach (var g in OverflowChain.GeneratorsFor(item))
            o.Add(new(OverflowChain.GenPrefix + g.building, Loc.T("clog.gen", GameData.Buildings.GetValueOrDefault(g.building)?.Name ?? g.building, g.rate, g.mw)));
        if (!fluid && OverflowChain.IsOverflow(item) && s.Targets.Any(t => t.Rate > 0 && t.Item == baseItem))
            o.Add(new(OverflowChain.Merge, Loc.T("clog.merge", GameData.Item(baseItem).Name)));
        foreach (var r in GameData.Recipes.Where(r => s.IsAvailable(r) && r.In.Any(a => a.Item == baseItem) && r.Out.Count > 0 && r.Out.All(a => a.Item != baseItem)
                     && r.In.All(a => a.Item == baseItem || GameData.RawResources.Contains(a.Item))
                     && r.Building is not ("Desc_Packager_C" or "Desc_Converter_C"))
                     .OrderBy(r => r.Alternate).ThenBy(r => r.Name))
        {
            string Names(IEnumerable<Amount> l) => string.Join(" + ", l.Select(a => GameData.Item(a.Item).Name));
            o.Add(new(r.ClassName, Loc.T("clog.recipe", (r.Alternate ? Loc.T("alt") : "") + r.Name, Names(r.In), Names(r.Out))));
        }
        return o;
    }

    /// <summary>Safe ends of an overflow: nothing downstream can fill up.</summary>
    public static bool Safe(string key) => key == Settings.SinkHandling || key == OverflowChain.Merge || key.StartsWith(OverflowChain.GenPrefix);

    public static List<ClogGuard> Find(Plan plan, SolveResult sol, Settings s)
    {
        var guards = new List<ClogGuard>();
        // machines of an overflow chain: every product is a new overflow (it only exists while there is surplus)
        foreach (var row in plan.Machines.Where(m => m.Recipe.OverflowOf != null))
            foreach (var output in row.Recipe.Out)
            {
                double made = row.Recipe.PerMin(output.Value) * row.Exact;
                if (made <= 1e-6) continue;
                var options = OptionsFor(output.Item, s);
                var choice = options.FirstOrDefault(o => o.Key == s.ClogHandling.GetValueOrDefault(output.Item, "")) ?? options[0];
                bool fl = GameData.Item(output.Item).IsFluid;
                guards.Add(new ClogGuard
                {
                    Row = row, Item = output.Item, Fluid = fl, Level = GuardLevel.Required, Produced = made, Surplus = made, AllAway = true,
                    Placement = Loc.T("clog.chain", GameData.Item(output.Item).Name, row.Recipe.Name, made, fl ? "m³/min" : "/min") + " " + How(choice),
                    Options = options, Choice = choice
                });
            }
        foreach (var row in plan.Machines.Where(m => m.Recipe.Out.Count > 1 && m.Recipe.OverflowOf == null))
        {
            foreach (var output in row.Recipe.Out)
            {
                var item = output.Item;
                double produced = row.Recipe.PerMin(output.Value) * row.Exact;
                if (produced <= 1e-6) continue;

                var consumers = plan.Machines.Where(m => m != row && m.Recipe.In.Any(a => a.Item == item)).ToList();
                double consumerNeed = consumers.Sum(m => m.Recipe.PerMin(m.Recipe.In.First(a => a.Item == item).Value) * m.Exact);
                // the plan's leftover of this item, shared among its producers by how much each makes
                double totalProduced = plan.Machines.Sum(m => m.Recipe.Out.Where(a => a.Item == item).Sum(a => m.Recipe.PerMin(a.Value) * m.Exact));
                double leftover = plan.Outputs.Where(o => o.Item == item && !o.IsTarget).Sum(o => o.Rate);
                double surplus = totalProduced > 0 ? leftover * produced / totalProduced : 0;

                GuardLevel? level = surplus > 1e-3 ? GuardLevel.Required : consumers.Count > 0 ? GuardLevel.Recommended : null;
                if (level == null) continue; // only feeds a final output that is taken away

                bool fluid = GameData.Item(item).IsFluid;
                int lines = Math.Max(1, plan.Splits.Where(p => p.Item == item).Select(p => p.Lines).DefaultIfEmpty(1).Max());
                string who = consumers.Count > 0 ? string.Join(", ", consumers.Select(c => c.ItemName).Distinct()) : Loc.T("node.target");
                bool allAway = consumers.Count == 0 && !sol.Demand.ContainsKey(item);
                string placement = allAway
                    ? Loc.T("guard.allAwayHow", row.BuildingName, surplus, fluid ? "m³/min" : "/min")
                    : fluid
                        ? Loc.T("guard.valveHow", row.BuildingName, who, consumerNeed / lines, surplus / lines)
                        : Loc.T("guard.smartHow", row.BuildingName, who, surplus / lines);
                if (level == GuardLevel.Recommended) placement += " " + Loc.T("guard.recommendedWhy", who);
                if (lines > 1) placement += " " + Loc.T("guard.perLine", lines);
                var options = OptionsFor(item, s);
                var choice = options.FirstOrDefault(o => o.Key == s.ClogHandling.GetValueOrDefault(item, "")) ?? options[0];
                placement += " " + How(choice);

                guards.Add(new ClogGuard
                {
                    Row = row, Item = item, Fluid = fluid, Level = level.Value, Produced = produced,
                    ConsumerNeed = consumerNeed, Surplus = surplus, Lines = lines, Placement = placement, AllAway = allAway,
                    Options = options, Choice = choice
                });
            }
        }
        return guards.OrderBy(g => g.Level).ThenBy(g => g.Row.ItemName).ToList();

        static string How(ClogOption c) => c.Key == "" ? Loc.T("clog.boxHow")
            : c.Key == Settings.SinkHandling ? Loc.T("clog.sinkHow")
            : c.Key == OverflowChain.Merge ? Loc.T("clog.mergeHow")
            : c.Key.StartsWith(OverflowChain.GenPrefix) ? Loc.T("clog.genHow")
            : Loc.T("clog.recipeHow", c.Label);
    }
}
