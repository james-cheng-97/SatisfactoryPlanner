namespace SatisfactoryPlanner;

/// <summary>
/// "Minimal resources" mode: picks the best mix of recipes available at the current tech level with a linear program.
/// Minimises weighted raw resource use (scarcer resources weigh more), respecting which resources are accessible
/// and their per-minute limits. Anything impossible to make is imported at a huge penalty so the problem is always feasible.
/// </summary>
public static class Optimizer
{
    // Approximate total world extraction rates (/min, normal-clock) — used as scarcity weights like other planners do.
    static readonly Dictionary<string, double> WorldSupply = new()
    {
        ["Desc_OreIron_C"] = 92100, ["Desc_OreCopper_C"] = 36900, ["Desc_Stone_C"] = 69900, ["Desc_Coal_C"] = 42300,
        ["Desc_OreGold_C"] = 15000, ["Desc_RawQuartz_C"] = 13500, ["Desc_Sulfur_C"] = 10800, ["Desc_OreBauxite_C"] = 12300,
        ["Desc_OreUranium_C"] = 2100, ["Desc_SAM_C"] = 10200, ["Desc_LiquidOil_C"] = 12600, ["Desc_NitrogenGas_C"] = 12000,
        ["Desc_Water_C"] = 9_000_000,
    };

    const double ImportCost = 1e5;
    const double MachineCost = 1e-4; // tiny tie-breaker: fewer machines when resource use is equal

    public static double Weight(string raw) => WorldSupply["Desc_OreIron_C"] / WorldSupply.GetValueOrDefault(raw, 10000);

    /// <param name="only">If given, restrict to exactly these recipes (used to balance manual picks that loop).</param>
    public static SolveResult Solve(Settings s, HashSet<RecipeDef>? only = null, Dictionary<string, double>? extra = null)
    {
        var res = new SolveResult();
        foreach (var t in s.Targets.Where(t => t.Rate > 0 && t.Item != null && GameData.Items.ContainsKey(t.Item)))
            res.Demand[t.Item] = res.Demand.GetValueOrDefault(t.Item) + t.Rate;
        if (res.Demand.Count == 0) return res;

        // pinned recipes: a manual pick in the table forbids the other recipes whose main product is that item
        // (a pin that can't work here — it needs a resource marked not accessible — is skipped)
        var recipes = only != null ? only.ToList() : GameData.Recipes.Where(s.IsAvailable).Where(r =>
            s.WorkingPin(r.Out[0].Item) is not { } pin || pin == r.ClassName).ToList();
        recipes.RemoveAll(r => s.IsSupplied(r.Out[0].Item)); // supplied items (plastic / rubber) come in, not made here
        // not sustainable (needs something gathered by hand, e.g. alien protein → biomass → biocoal): only when pinned
        // nor one tracing back to a resource marked not accessible (petroleum coke when crude oil is off): importing a
        // little oil must not beat importing the item — the player said that resource isn't there
        if (only == null) recipes.RemoveAll(r => !s.Reaches(r) || !GameData.IsSustainable(r) && !s.RecipeOverrides.ContainsValue(r.ClassName));

        var items = recipes.SelectMany(r => r.In.Concat(r.Out)).Select(a => a.Item).Concat(res.Demand.Keys).Distinct().ToList();
        var row = items.Select((it, i) => (it, i)).ToDictionary(x => x.it, x => x.i);
        var raws = items.Where(i => (GameData.RawResources.Contains(i) && s.IsResourceAllowed(i)) || s.IsSupplied(i)).ToList();
        var bounded = raws.Where(r => s.EffectiveLimit(r) != null).ToList();

        int m = items.Count + bounded.Count;
        // columns: recipes | extraction | surplus | import | bound slack
        int cR = 0, cE = recipes.Count, cS = cE + raws.Count, cI = cS + items.Count, cT = cI + items.Count, n = cT + bounded.Count;
        var lp = new double[m, n + 1];
        var cost = new double[n];
        for (int j = 0; j < recipes.Count; j++)
        {
            var r = recipes[j];
            foreach (var it in r.In.Concat(r.Out).Select(a => a.Item).Distinct())
                lp[row[it], cR + j] = r.NetPerMachine(it);
            cost[cR + j] = MachineCost + (s.IsUnlocked(r) ? 0 : s.LockedPenalty * (r.Alternate ? 50 : 1)); // hard drives are random, prefer milestones
        }
        for (int k = 0; k < raws.Count; k++) { lp[row[raws[k]], cE + k] = 1; cost[cE + k] = Weight(raws[k]); }
        for (int i = 0; i < items.Count; i++)
        {
            lp[i, cS + i] = -1;
            lp[i, cI + i] = 1; cost[cI + i] = ImportCost;
            lp[i, n] = res.Demand.GetValueOrDefault(items[i]) + (extra?.GetValueOrDefault(items[i]) ?? 0) + 1e-7 * (1 + (i * 7919 % 101) / 101.0);
        }
        for (int b = 0; b < bounded.Count; b++)
        {
            int r = items.Count + b;
            lp[r, cE + raws.IndexOf(bounded[b])] = 1;
            lp[r, cT + b] = 1;
            lp[r, n] = s.EffectiveLimit(bounded[b])!.Value;
        }
        var basis = Enumerable.Range(0, items.Count).Select(i => cI + i).Concat(Enumerable.Range(0, bounded.Count).Select(b => cT + b)).ToArray();

        var x = Simplex(lp, cost, basis, m, n);
        if (x == null) { res.Warnings.Add(Loc.T("warn.noConverge")); return res; }

        for (int k = 0; k < raws.Count; k++) res.Score += x[cE + k] * cost[cE + k];
        for (int i = 0; i < items.Count; i++) res.Score += x[cI + i] * ImportCost;
        for (int j = 0; j < recipes.Count; j++)
        {
            if (x[cR + j] < 1e-3) continue; // ignore tie-breaking noise
            var r = recipes[j];
            res.Machines[r] = x[cR + j];
            res.Owner[r] = r.Out[0].Item;
            foreach (var it in r.In.Concat(r.Out).Select(a => a.Item).Distinct())
                res.Net[it] = res.Net.GetValueOrDefault(it) + x[cR + j] * r.NetPerMachine(it);
        }
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (x[cI + i] > 1e-4 && !s.IsSupplied(it))
            {
                res.Inputs.Add(it);
                res.Warnings.Add(Loc.T("warn.cantMake", GameData.Item(it).Name, x[cI + i]));
            }
            if ((GameData.RawResources.Contains(it) || s.IsSupplied(it)) && res.Net.GetValueOrDefault(it) < -1e-4) res.Inputs.Add(it);
        }
        return res;
    }

    /// <summary>Primal simplex on a tableau already in canonical form for the given feasible basis. Minimises cost·x.</summary>
    static double[]? Simplex(double[,] t, double[] cost, int[] basis, int m, int n)
    {
        // express the objective in terms of non-basic variables (reduced costs)
        var z = new double[n + 1];
        for (int j = 0; j < n; j++) z[j] = cost[j];
        for (int i = 0; i < m; i++)
        {
            var cb = cost[basis[i]];
            if (cb == 0) continue;
            for (int j = 0; j <= n; j++) z[j] -= cb * t[i, j];
        }

        const double eps = 1e-9;
        for (int iter = 0; iter < 20000; iter++)
        {
            bool bland = iter > 3000; // anti-cycling fallback
            int enter = -1; double best = -eps;
            for (int j = 0; j < n; j++)
            {
                if (z[j] < best) { enter = j; if (bland) break; best = z[j]; }
            }
            if (enter < 0) break; // optimal

            int leave = -1; double ratio = double.MaxValue;
            for (int i = 0; i < m; i++)
            {
                if (t[i, enter] <= eps) continue;
                var q = t[i, n] / t[i, enter];
                if (q < ratio - 1e-12 || (Math.Abs(q - ratio) <= 1e-12 && leave >= 0 && basis[i] < basis[leave])) { ratio = q; leave = i; }
            }
            if (leave < 0) return null; // unbounded (shouldn't happen)

            var pv = t[leave, enter];
            for (int j = 0; j <= n; j++) t[leave, j] /= pv;
            for (int i = 0; i < m; i++)
            {
                if (i == leave) continue;
                var f = t[i, enter];
                if (Math.Abs(f) < 1e-15) continue;
                for (int j = 0; j <= n; j++) t[i, j] -= f * t[leave, j];
            }
            var fz = z[enter];
            for (int j = 0; j <= n; j++) z[j] -= fz * t[leave, j];
            basis[leave] = enter;
            if (iter == 19999) return null;
        }

        var x = new double[n];
        for (int i = 0; i < m; i++) x[basis[i]] = Math.Max(0, t[i, n]);
        return x;
    }
}
