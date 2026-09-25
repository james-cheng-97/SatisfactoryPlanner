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
}

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

    public static List<ClogGuard> Find(Plan plan, SolveResult sol)
    {
        var guards = new List<ClogGuard>();
        foreach (var row in plan.Machines.Where(m => m.Recipe.Out.Count > 1))
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

                guards.Add(new ClogGuard
                {
                    Row = row, Item = item, Fluid = fluid, Level = level.Value, Produced = produced,
                    ConsumerNeed = consumerNeed, Surplus = surplus, Lines = lines, Placement = placement, AllAway = allAway
                });
            }
        }
        return guards.OrderBy(g => g.Level).ThenBy(g => g.Row.ItemName).ToList();
    }
}
