namespace SatisfactoryPlanner;

public class UnlockSuggestion
{
    public required RecipeDef Recipe { get; init; }
    public System.Windows.Media.ImageSource? Icon => ImageCache.Get(Recipe.Out[0].Item);
    public string Name => (Recipe.Alternate ? Loc.T("alt") : "") + Recipe.Name;
    public bool IsRequired { get; init; }
    public string UnlockedBy => string.IsNullOrEmpty(Recipe.UnlockedBy) ? "—" : Recipe.UnlockedBy;
    public string Benefit { get; init; } = "";
    public double Rank { get; init; }
}

/// <summary>
/// The milestone tier is a hard cap — suggestions only include hard-drive / MAM recipes at or below it.
/// Pass 1 (required): if the plan needs imports, re-solve allowing locked recipes within the tier with a heavy
///   per-machine cost, so what remains is the minimal set to unlock. If it's still impossible, find the lowest
///   milestone tier where it becomes possible and report that instead.
/// Pass 2 (optional): allow locked recipes within the tier at no penalty; score each by its individual saving.
/// </summary>
public static class Unlocks
{
    const double Penalty = 1000;

    static List<string> Imports(SolveResult r) => r.Inputs.Where(i => !GameData.RawResources.Contains(i)).ToList();
    static string Names(IEnumerable<string> items) => string.Join(", ", items.Select(i => GameData.Item(i).Name));

    public static (string summary, bool notPossible, List<UnlockSuggestion> list) Suggest(Settings current, CancellationToken ct)
    {
        var baseS = current.Clone(); baseS.Optimize = true;
        var baseline = Optimizer.Solve(baseS);
        var list = new List<UnlockSuggestion>();
        var imports = Imports(baseline);
        var summary = "";
        bool notPossible = false;

        if (imports.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var req = baseS.Clone(); req.IgnoreRecipeLocks = true; req.LockedPenalty = Penalty;
            var sol = Optimizer.Solve(req);
            var still = Imports(sol);
            if (still.Count > 0)
            {
                // impossible within the milestone cap: find the tier where it becomes possible
                int? need = null;
                for (int t = current.MaxTier + 1; t <= 9 && need == null; t++)
                {
                    ct.ThrowIfCancellationRequested();
                    var up = req.Clone(); up.MaxTier = t; up.Unlocked = null; up.IncludeAlternates = up.IncludeMam = true;
                    if (Imports(Optimizer.Solve(up)).Count == 0) need = t;
                }
                summary = Loc.T("unlock.notPossible", current.MaxTier, Names(still)) +
                          (need != null ? Loc.T("unlock.untilTier", need) : Loc.T("unlock.checkResources"));
                notPossible = true;
            }
            var fixable = imports.Except(still).ToList();
            var needed = sol.Machines.Keys.Where(r => !baseS.IsUnlocked(r)).ToList();
            foreach (var r in needed)
                list.Add(new UnlockSuggestion { Recipe = r, Rank = 1000, IsRequired = true, Benefit = Loc.T("unlock.required", Names(fixable)) });
            if (needed.Count > 0 && fixable.Count > 0)
                summary += (summary.Length > 0 ? " " : "") + Loc.T("unlock.requiredSummary", needed.Count, Names(fixable));
            baseS.ExtraRecipes.UnionWith(needed.Select(r => r.ClassName));
            baseline = Optimizer.Solve(baseS);
        }

        ct.ThrowIfCancellationRequested();
        var imp = baseS.Clone(); imp.IgnoreRecipeLocks = true; imp.RecipeOverrides.Clear();
        var best = Optimizer.Solve(imp);
        var better = best.Machines.Keys.Where(r => !baseS.IsUnlocked(r)).ToList();
        var total = baseline.Score > 0 ? (baseline.Score - best.Score) / baseline.Score : 0;

        if (better.Count > 0 && total > 1e-4)
        {
            foreach (var r in better)
            {
                ct.ThrowIfCancellationRequested();
                var one = baseS.Clone(); one.ExtraRecipes.Add(r.ClassName);
                var saving = (baseline.Score - Optimizer.Solve(one).Score) / baseline.Score;
                list.Add(new UnlockSuggestion
                {
                    Recipe = r, Rank = saving,
                    Benefit = saving > 1e-4 ? Loc.T("unlock.saves", saving) : Loc.T("unlock.combined")
                });
            }
            summary += (summary.Length > 0 ? " " : "") + Loc.T("unlock.optionalSummary", better.Count, current.MaxTier, total);
        }
        if (summary.Length == 0) summary = Loc.T("unlock.nothing", current.MaxTier);
        return (summary, notPossible, list.OrderByDescending(s => s.Rank).ToList());
    }
}
