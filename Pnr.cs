using System.Globalization;

namespace SatisfactoryPlanner;

/// <summary>
/// Board-style place &amp; route (single floor). Each machine group is a fixed cell — its machines in a line with an input
/// manifold (splitters) on one side and an output collector (mergers) on the other — that can sit anywhere on the
/// foundation grid in any of four orientations. Input / output boxes are pads on the south edge. Simulated annealing
/// places the cells to minimise the estimated belt length (half-perimeter per item line) plus the size of the factory;
/// then every item line is routed as a tree on a 1 m grid with two levels (port height, and 2 m up to cross other
/// belts; a ramp is the "via"). Trees grow from the first producer to the nearest consumer; further producers join with
/// mergers, further consumers branch off with splitters downstream of every merger, so items always flow the right way.
/// </summary>
public partial class Layout
{
    sealed class Cell
    {
        public GraphNode Node = null!;
        public string Building = "";
        public double W, D, P;            // machine footprint and pitch
        public int K = 1;                 // machines in the cell
        public int Sub, Subs;
        public double Share = 1;
        public List<(Lane lane, double rate)> Ins = new(), Outs = new();
        public double[] InOff = [], OutOff = [];
        public Slot? Pad;                 // an input / output box instead of machines
        public bool Spare;
        public int Depth;
        public double Halo = 2;           // routing room kept around the cell (grows where routing failed)
        public bool Fixed;                // multi-floor: never moved (a crossing in the column, a hole above a tall machine)
        public string? Void;              // a fixed blocker with no ports: "hole" (open to the sky) or "pass" (a crossing passing through)
        public string? Note;              // label for a crossing / blocker
        public string? CrossKey;          // a crossing: "line@floor" it serves
        public int Cluster = -1;          // a cluster of low-level groups placed as one block (-1: none)
        public double RelX, RelY;         // position relative to its cluster's first group
        // local geometry (unrotated: machines along +x, inputs south, outputs north)
        public double BX0, BX1, BY0, BY1;
        public List<Term> Terms = new();
        // placement
        public double X, Y; public int Rot;
        // lanes 3 m apart (a drop from an outer lane ramps 2 m up over the inner ones within that)
        // lanes: one input → a ground lane 2 m out. Two inputs → the inner lane 4 m up at 6 m (or 4 m) out (lifts at its end, and
        // down to each machine 1 m past its edge: 5 m of belt between a lift and its splitter, as built in game), the
        // outer lane on the ground at 9 m out, its drops passing under the inner one.
        // Outputs the same on the other side. (Cells with more than two are single machines: no lanes.)
        // (wide, 6 m / 9 m: lift → 5 m of belt → splitter, as in the "Sample connections" blueprint; narrow, 4 m / 7 m:
        // about 1 m of belt — smaller groups. Neither suits every plan: single-floor plans try both, see Build)
        public double InY(int j) => Ins.Count < 2 ? -2 : j == 0 ? -(NarrowLanes ? 4 : 6) : -(NarrowLanes ? 7 : 9);
        public double OutY(int j) => Outs.Count < 2 ? D + 2 : j == 0 ? D + (NarrowLanes ? 4 : 6) : D + (NarrowLanes ? 7 : 9);
        public bool Up(int side, int j) => j == 0 && (side == 0 ? Ins.Count : Outs.Count) == 2;
        public double PortX(int m, double off) => m * P + W / 2 + off;
        public double Right => (K - 1) * P + W;
        public bool[] East = [];            // per manifold lane: belt connection on the east end (else west)
        public bool Direct => Pad == null && K == 1;   // a single machine: belts go straight to its ports, no manifold
        public (double lx, double ly, int dir) Local(Term t)
        {
            if (t.Track < 0) return (t.LX, t.LY, t.Dir);
            if (Direct)
                return t.Track < Ins.Count ? (PortX(0, InOff[t.Track]), -2, 3) : (PortX(0, OutOff[t.Track - Ins.Count]), D + 2, 1);
            return East[t.Track] ? (Right + 4, t.LY, 0) : (-4, t.LY, 2);
        }
        public void Bounds()
        {
            if (Pad != null) return;
            if (Direct) { BX0 = -1; BX1 = W + 1; BY0 = -1; BY1 = D + 1; return; }
            BX0 = East.All(e => e) ? -1 : -3;
            BX1 = East.Any(e => e) ? Right + 3 : Right + 1;
        }
    }
    sealed record Term(Lane Lane, bool Source, double LX, double LY, int Dir, int Track = -1); // Dir: 0 E, 1 N, 2 W, 3 S (local); Track: manifold lane index (inputs first), -1 for a box

    static (double x, double y) Rotate(double x, double y, int rot) => (rot & 3) switch
    {
        0 => (x, y), 1 => (-y, x), 2 => (-x, -y), _ => (y, -x),
    };
    static (double x, double y) World(Cell c, double lx, double ly)
    {
        var (x, y) = Rotate(lx, ly, c.Rot);
        return (c.X + x, c.Y + y);
    }
    static (double x0, double y0, double x1, double y1) Box(Cell c, double x0, double y0, double x1, double y1)
    {
        var a = World(c, x0, y0); var b = World(c, x1, y1);
        return (Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Max(a.x, b.x), Math.Max(a.y, b.y));
    }
    static readonly int[] DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
    // placement weighting of busy lines (set per run): weight = 1 + items/min ÷ RateDiv, lines from an input box × RawMul
    [ThreadStatic] static double RateDiv, RawMul;
    /// <summary>When &gt; 0: groups of big machines (refinery size and up) hold at most this many (less empty space around them).</summary>
    [ThreadStatic] static int BigPer;
    /// <summary>Set by the multi-floor planner when the plan needs only the ground floor.</summary>
    [ThreadStatic] static bool OneFloor;
    // routing strategy (multi-floor tries a second one on a floor that fails): Corr keeps the approach to each opening
    // clear of the line's other paths; LooseAt > 0 gives every group more room from that failed attempt on
    [ThreadStatic] static bool Corr;
    [ThreadStatic] static int LooseAt;
    /// <summary>Multi-floor: when this floor's time is up (routing stops there too); MinValue = none.</summary>
    [ThreadStatic] static DateTime StopAt;
    /// <summary>Multi-floor: blueprint tiles the floors placed so far use (a floor above reuses them cheaply).</summary>
    [ThreadStatic] static HashSet<(int, int)>? UsedTiles;
    /// <summary>Two-stage routing (local lines flat first, then global lines free to stack): the fallback when the
    /// usual one-stage routing fails — it rescues crowded plans but makes easy ones bigger.</summary>
    [ThreadStatic] static bool TwoStage;
    /// <summary>Two-lane groups with the narrow lane spacing (4 m / 7 m) instead of the wide one (6 m / 9 m).</summary>
    [ThreadStatic] static bool NarrowLanes;
    /// <summary>Plan without clusters (single-floor plans are laid out both ways and the better kept).</summary>
    [ThreadStatic] static bool NoClusters;
    /// <summary>Multi-floor: split floors on production-step boundaries (tried after the usual split, the better kept).</summary>
    [ThreadStatic] static bool StepFloors;
    [ThreadStatic] static System.Runtime.CompilerServices.StrongBox<long>? CancelAt; // portfolio: shared stop (Stopwatch ticks; 0 none)
    [ThreadStatic] static int Seed;                                   // placement annealing seed (portfolio runs several)
    /// <summary>Parallel workers for a portfolio (leaves a core for the UI); PNR_THREADS overrides (1 = one after another).</summary>
    static int Workers => int.TryParse(Environment.GetEnvironmentVariable("PNR_THREADS"), out var w) && w > 0 ? w : Math.Max(1, Environment.ProcessorCount - 1);
    /// <summary>
    /// Runs every job (each sets its own thread-static strategy flags) in parallel and returns the results in job order.
    /// Luck of the annealing path swings a plan by several blueprints; the best of many paths at once is reliable.
    /// </summary>
    static T?[] Portfolio<T>(IReadOnlyList<Func<T?>> jobs) where T : class
    {
        var res = new T?[jobs.Count];
        System.Threading.Tasks.Parallel.For(0, jobs.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Workers }, i =>
        {
            try { res[i] = jobs[i](); }
            catch (Exception ex) { if (Environment.GetEnvironmentVariable("PNR_DEBUG") == "1") Console.WriteLine($"portfolio job {i}: {ex.Message}"); }
        });
        return res;
    }
    /// <summary>One strategy for the single-floor planner, run on the calling thread (flags set, then cleared).</summary>
    sealed record Strat(double Div, double Raw, bool Narrow = false, bool NoClust = false, int Big = 0, bool Two = false, int Seed = 0);
    static Layout? RunStrat(Plan plan, Settings s, Strat st)
    {
        (RateDiv, RawMul, NarrowLanes, NoClusters, BigPer, TwoStage, Seed) = (st.Div, st.Raw, st.Narrow, st.NoClust, st.Big, st.Two, st.Seed);
        (Corr, LooseAt, OneFloor, UsedTiles, StopAt) = (false, 0, false, null, DateTime.MinValue);
        try { return BuildPnr(plan, s); }
        finally { (NarrowLanes, NoClusters, BigPer, TwoStage, Seed) = (false, false, 0, false, 0); }
    }
    /// <summary>Tests: time per step (PNR_PROFILE=1), printed by the test harness.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int n, long ms)> Profile = new();
    static void Prof(string step, long ms) { if (Environment.GetEnvironmentVariable("PNR_PROFILE") == "1") Profile.AddOrUpdate(step, (1, ms), (_, v) => (v.n + 1, v.ms + ms)); }
    /// <summary>Height of the upper belt level above the ports (m): conveyor lifts change level on the spot.</summary>
    public const double LiftH = 4;
    /// <summary>Routing levels: the ground (port height) and stacked levels 2 m apart above it (as on stackable conveyor
    /// poles / pipe supports), reached by a 3 m ramp from the level next to it or a conveyor lift.</summary>
    public const int NL = 4;
    public const double LevelH = 2;
    /// <summary>Scales every time limit of the planner: 2 = a layout may take up to about two minutes (big factories with
    /// stacked levels need it). PNR_TIMEX overrides it (tests).</summary>
    static readonly double? EnvTimeX = double.TryParse(Environment.GetEnvironmentVariable("PNR_TIMEX"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tx) ? tx : null;
    static double _timeX = 2; // set from Settings.LayoutSeconds at the start of each build (1 = about a minute)
    static double TimeX => EnvTimeX ?? _timeX;
    internal static double TimeScale => TimeX;
    /// <summary>Sets the planner's time limit (Settings.LayoutSeconds; PNR_TIMEX still wins, for tests).</summary>
    public static void SetTimeLimit(int seconds) => _timeX = Math.Clamp(seconds, 20, 1800) / 60.0;

    /// <summary>
    /// Several floors: machines taller than 15 m stay on the ground floor and the floors above leave them open to the sky;
    /// the rest go up floor by floor — machines with fluids first (no pumping up), then in production order (earliest
    /// first) — about the same floor area each. An item made
    /// on one floor and used on another rises / drops in a fixed column on the west edge (a conveyor lift or a pumped
    /// pipe), the same spot on every floor. Floor 0 is laid out first, then each floor above with its holes fixed.
    /// </summary>
    static Layout? BuildPnrFloors(Plan plan, Settings s)
    {
        int F = Math.Clamp(s.Floors, 2, 4);
        var nodes = plan.Nodes.GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.First());
        var machines = nodes.Values.Where(n => n.Kind == NodeKind.Machine && n.Recipe != null).ToList();
        if (machines.Count == 0) return null;
        var outs = plan.Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().ToList());
        var depth = new Dictionary<string, int>(); var onStack = new HashSet<string>();
        int Depth(string k)
        {
            if (depth.TryGetValue(k, out var d)) return d;
            onStack.Add(k); int best = 0;
            foreach (var t in outs.GetValueOrDefault(k) ?? []) if (!onStack.Contains(t)) best = Math.Max(best, Depth(t) + 1);
            onStack.Remove(k); return depth[k] = best;
        }
        // floor space a group needs: its machines with their lanes, more for every belt past two (busier to route)
        double AreaOf(GraphNode n)
        {
            var (w, d) = Footprint(n.Recipe!.Building);
            int belts = plan.Edges.Where(e => e.From == n.Key || e.To == n.Key).Select(e => e.Item).Distinct().Count();
            return (w + 2) * (d + 12) * Math.Max(1, n.Machines) * (1 + 0.3 * Math.Max(0, belts - 2));
        }
        bool Tall(GraphNode n) => HeightOf(n.Recipe!.Building) > 15;
        // floors: tall ones on the ground; then the earliest stages first, a floor's share of area each
        var floorOf = machines.Where(Tall).ToDictionary(n => n.Key, _ => 0);
        // the ground floor also holds every input / output box (with room to reach them): counted as its area
        double boxes = nodes.Values.Count(n => n.Kind != NodeKind.Machine) * 5 * 16;
        double target = (machines.Sum(AreaOf) + boxes) / F, filled = machines.Where(Tall).Sum(AreaOf) + boxes;
        int fl = 0;
        // machines that take or make a fluid first (lowest floors: pipes up need pumps), then the earliest stages
        bool Wet(GraphNode n) => plan.Edges.Any(e => (e.From == n.Key || e.To == n.Key) && GameData.Item(e.Item).IsFluid);
        var ordered = machines.Where(n => !Tall(n)).OrderBy(n => Wet(n) ? 0 : 1).ThenByDescending(n => Depth(n.Key)).ThenBy(n => n.Key).ToList();
        bool bySteps = StepFloors && machines.Sum(n => Math.Max(1, n.Machines)) >= 20;
        if (!bySteps)
            foreach (var n in ordered)
            {
                if (filled >= target * 0.9 && fl < F - 1) { fl++; filled = 0; }
                floorOf[n.Key] = fl; filled += AreaOf(n);
            }
        else
        {
            // step-aligned floors (bigger plans): a production step (groups at the same depth) stays on one floor where it
            // can — the next floor starts at a step boundary when the whole step would overshoot this floor's share by
            // more than 25 %; only a step too big for that is split group by group
            var steps = new List<List<GraphNode>>();
            foreach (var n in ordered)
            {
                if (steps.Count == 0 || Depth(steps[^1][0].Key) != Depth(n.Key) || Wet(steps[^1][0]) != Wet(n)) steps.Add(new());
                steps[^1].Add(n);
            }
            foreach (var step in steps)
            {
                double a = step.Sum(AreaOf);
                if (fl < F - 1 && filled >= target * 0.5 && filled + a > target * 1.25) { fl++; filled = 0; }
                if (fl < F - 1 && filled + a > target * 1.25)
                    foreach (var n in step) // too big for one floor: group by group
                    {
                        if (filled >= target * 0.9 && fl < F - 1) { fl++; filled = 0; }
                        floorOf[n.Key] = fl; filled += AreaOf(n);
                    }
                else { foreach (var n in step) floorOf[n.Key] = fl; filled += a; }
            }
        }
        int used = floorOf.Values.Max() + 1;
        if (used < 2) { OneFloor = true; return null; } // everything belongs on one floor (e.g. all tall): the single-floor planner
        // lanes and the floors they touch (ids as BuildPnr names them: item#line of the producer)
        static int LineOf(string key) => key.Contains('#') && int.TryParse(key[(key.LastIndexOf('#') + 1)..], out var k) ? k : 1;
        // crossings: one per line and floor that uses it but doesn't make it (key "line@floor"), from the nearest
        // floor that makes it — each a single lift / pipe between two floors (the maker splits the line to each)
        var makers = new Dictionary<string, HashSet<int>>();
        var use = new Dictionary<(string id, int f), double>();
        foreach (var e in plan.Edges)
        {
            if (!nodes.ContainsKey(e.From) || !nodes.ContainsKey(e.To)) continue;
            string id = $"{e.Item}#{LineOf(e.From)}";
            (makers.TryGetValue(id, out var ms) ? ms : makers[id] = new()).Add(floorOf.GetValueOrDefault(e.From));
            var k2 = (id, floorOf.GetValueOrDefault(e.To));
            use[k2] = use.GetValueOrDefault(k2) + e.Rate;
        }
        var span = new Dictionary<string, (HashSet<int> src, HashSet<int> snk, double rate)>();
        foreach (var ((id, to), rate) in use)
        {
            if (makers[id].Contains(to)) continue;
            int from = makers[id].OrderBy(f => Math.Abs(f - to)).First();
            span[$"{id}@{to}"] = (new() { from }, new() { to }, rate);
        }
        var slot = span.Keys.OrderBy(k => k).Select((k, i) => (k, i)).ToDictionary(q => q.k, q => q.i);
        bool dbg = Environment.GetEnvironmentVariable("PNR_DEBUG") == "1";
        if (dbg) Console.WriteLine($"floors: {string.Join(" | ", Enumerable.Range(0, used).Select(f => string.Join(",", floorOf.Where(kv => kv.Value == f).Select(kv => kv.Key))))}; crossings {slot.Count}");

        var holes = new List<(double x, double y, double w, double d, string note)>();
        var per = new List<Layout>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool dense = machines.Sum(n => Math.Max(1, n.Machines)) >= 40; // a big plan: the looser strategy first
        var riserPos = new Dictionary<string, (double x, double y, int rot)>();
        var used2 = new HashSet<(int, int)>();
        UsedTiles = used2;
        for (int f = 0; f < used; f++)
        {
            // two strategies: the tight one, then (if this floor fails) approaches kept clear and early loosening —
            // within ~55 s for all floors (big factories take a while; the whole layout stays under a minute)
            // (once a floor needed the second, the floors above start with it: the plan is dense)
            Layout? l = null;
            var order = (dense ? new[] { (true, 2), (false, 4) } : new[] { (false, 4), (true, 2) }).ToList();
            order.Insert(1, order[0]); int narrowAt = 1; // second: the first strategy again with the narrow lane spacing
            // machines across tile borders (by hand) is a freedom, not a must: a floor that fails with it tries without
            var sNoHand = s.HandPlaceAcrossTiles ? s.Clone() : s; sNoHand.HandPlaceAcrossTiles = false;
            if (s.HandPlaceAcrossTiles) order.Add(order[0]);
            order.Add((true, 2)); int twoAt = order.Count - 1; // last: two-stage routing
            for (int v = 0; v < order.Count; v++)
            {
                var (corr, loose) = order[v];
                var sv = v == 3 && s.HandPlaceAcrossTiles ? sNoHand : s;
                TwoStage = v == twoAt;
                NarrowLanes = v == narrowAt;
                long left = (long)(55000 * TimeX) - clock.ElapsedMilliseconds;
                if (left < 3000) break;
                (Corr, LooseAt) = (corr, loose);
                long fair = left / (used - f); // this floor's share of what's left
                if (f == 0) fair = fair * 3 / 2; // the ground floor (boxes, and it places the crossings) is the hardest
                Layout.Say("status.floor", f + 1, used);
                var fc = new FloorCtx { Floor = f, FloorOf = floorOf, Slot = slot, Span = span, Holes = f > 0 ? holes : [], RiserPos = riserPos,
                    Budget = Math.Max(3000, v == 0 ? fair / 2 : fair) };
                StopAt = DateTime.UtcNow.AddMilliseconds(fc.Budget + 500);
                l = BuildPnr(plan, sv, fc);
                StopAt = DateTime.MinValue;
                if (dbg) Console.WriteLine($"floors: floor {f} {(l == null ? "failed" : "ok")} (strategy {(corr ? 2 : 1)}, {clock.ElapsedMilliseconds} ms)");
                if (l != null) { if (corr) dense = true; break; }
            }
            (Corr, LooseAt) = (false, 0); TwoStage = false; NarrowLanes = false;
            (Corr, LooseAt) = (false, 0); TwoStage = false; NarrowLanes = false;
            if (l == null) { UsedTiles = null; return null; }
            if (f == 0)
                foreach (var b in l.Buildings.Where(b => b.Kind is "machine" or "surplus" && HeightOf(b.Building) > 15))
                    holes.Add((b.X - 1, b.Y - 1, b.W + 2, b.H + 2, Loc.T("layout.openAbove", GameData.Buildings.GetValueOrDefault(b.Building)?.Name ?? "")));
            per.Add(l);
            foreach (var (id, pos) in l.RiserSpots) if (!riserPos.ContainsKey(id)) riserPos[id] = pos;
            if (s.BlueprintTile > 0)
            {
                // the tiles this floor uses: the floors above prefer them (one blueprint per tile, all floors)
                double tl = s.BlueprintTile * Foundation;
                foreach (var b in l.Buildings)
                    for (int tx = (int)Math.Floor(b.X / tl); tx <= (int)Math.Floor((b.X + b.W - 0.01) / tl); tx++)
                        for (int ty = (int)Math.Floor(b.Y / tl); ty <= (int)Math.Floor((b.Y + b.H - 0.01) / tl); ty++) used2.Add((tx, ty));
            }
        }
        // one layout, floor by floor
        var L = new Layout { HasLevels = true, Floors = used };
        for (int f = 0; f < used; f++)
        {
            var l = per[f];
            L.Buildings.AddRange(l.Buildings.Select(b => b with { Floor = f }));
            L.Belts.AddRange(l.Belts.Select(b => b with { Floor = f }));
            L.Markers.AddRange(l.Markers.Select(m => m with { Floor = f }));
            L.Corridors.AddRange(l.Corridors.Select(c => c with { Floor = f }));
            if (f == 0) L.Wall.AddRange(l.Wall);
            L.Lanes += l.Lanes; L.Rows += l.Rows; L.RowLength = Math.Max(L.RowLength, l.RowLength);
        }
        L.Width = per.Max(l => l.Width); L.Height = per.Max(l => l.Height);
        // storey height: the tallest machine on the floor below (≤ 15 m: taller ones go through the hole) plus ~4 m of
        // headroom, in whole 4 m walls — a floor sitting right on the machines' roofs feels cramped in game
        L.FloorElevation = [0];
        for (int f = 1; f < used; f++)
        {
            double h = L.Buildings.Where(b => b.Floor == f - 1 && b.Kind == "machine" && HeightOf(b.Building) <= 15).Select(b => HeightOf(b.Building)).DefaultIfEmpty(8).Max();
            L.FloorElevation.Add(L.FloorElevation[^1] + Math.Max(12, Math.Ceiling((h + 4) / 4) * 4));
        }
        foreach (var (key, k) in slot)
        {
            int from = span[key].src.First(), to = span[key].snk.First();
            if (from >= used || to >= used) continue;
            L.LiftLength += Math.Abs(L.FloorElevation[to] - L.FloorElevation[from]);
            // the vertical run: a lift / pipe straight up or down from the crossing's face on the maker's floor
            if (!riserPos.TryGetValue(key, out var rp)) continue;
            var rc = new Cell { W = 2, D = 2, X = rp.x, Y = rp.y, Rot = rp.rot };
            var face = World(rc, 1, 2);
            string id = key[..key.LastIndexOf('@')], item = id[..id.LastIndexOf('#')];
            bool fluid = GameData.Item(item).IsFluid;
            L.Belts.Add(new Segment(face.x, face.y, face.x, face.y, fluid, item, true, from, id, 0, L.FloorElevation[to] - L.FloorElevation[from]));
            L.Crossings.Add((id, face.x, face.y, Math.Min(from, to), Math.Max(from, to), fluid));
        }
        UsedTiles = null;
        return L;
    }

    /// <summary>The biggest empty rectangle inside a layout (no building, no belt), as a share of its whole area (4 m grid).</summary>
    static double LargestHole(Layout L)
    {
        const double C = 4;
        int w = (int)Math.Ceiling(L.Width / C), h = (int)Math.Ceiling(L.Height / C);
        if (w == 0 || h == 0) return 0;
        var used = new bool[w, h];
        void Mark(double x0, double y0, double x1, double y1)
        {
            for (int x = Math.Max(0, (int)Math.Floor(x0 / C)); x <= Math.Min(w - 1, (int)Math.Floor(x1 / C)); x++)
                for (int y = Math.Max(0, (int)Math.Floor(y0 / C)); y <= Math.Min(h - 1, (int)Math.Floor(y1 / C)); y++) used[x, y] = true;
        }
        foreach (var b in L.Buildings) Mark(b.X, b.Y, b.X + b.W, b.Y + b.H);
        foreach (var sg in L.Belts) Mark(Math.Min(sg.X1, sg.X2), Math.Min(sg.Y1, sg.Y2), Math.Max(sg.X1, sg.X2), Math.Max(sg.Y1, sg.Y2));
        // largest all-empty rectangle: histogram per row
        var hist = new int[w]; int best = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) hist[x] = used[x, y] ? 0 : hist[x] + 1;
            for (int x = 0; x < w; x++)
            {
                int m = int.MaxValue;
                for (int x2 = x; x2 < w && hist[x2] > 0; x2++) { m = Math.Min(m, hist[x2]); best = Math.Max(best, m * (x2 - x + 1)); }
            }
        }
        return best / (double)(w * h);
    }
    /// <summary>One floor of a multi-floor factory: which machines are on it, the crossing column, holes above tall machines.</summary>
    sealed class FloorCtx
    {
        public int Floor;
        public Dictionary<string, int> FloorOf = new();                 // machine node key → floor (boxes: floor 0)
        public Dictionary<string, int> Slot = new();                    // lane id → its place in the crossing column
        public Dictionary<string, (HashSet<int> src, HashSet<int> snk, double rate)> Span = new();
        public List<(double x, double y, double w, double d, string note)> Holes = new();
        public long Budget = 13000;                                     // ms for this floor's attempts
        public Dictionary<string, (double x, double y, int rot)> RiserPos = new(); // crossings already placed (lower floors)
    }
    /// <summary>Where a crossing sits in the column on the west edge (the same spot on every floor).</summary>
    /// (Every 6 m up the edge, skipping spots whose box or opening would come within 4 m of a tile border.)
    static (double x, double y) RiserAt(int slot, double tile)
    {
        bool Near(double v) => tile > 0 && Math.Abs(v - Math.Round(v / tile) * tile) < 4 && Math.Round(v / tile) >= 1;
        double y = 6;
        for (int k = 0; ; y += 6)
        {
            if (Near(y - 3) || Near(y + 1) || Near(y - 1)) continue; // box y-3…y+1, opening at y-1
            if (k++ == slot) return (1, y);
        }
    }

    static Layout? BuildPnr(Plan plan, Settings s, FloorCtx? fc = null)
    {
        var nodes = plan.Nodes.GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.First());
        if (nodes.Count == 0) return null;
        static int LineOf(string key) => key.Contains('#') && int.TryParse(key[(key.LastIndexOf('#') + 1)..], out var k) ? k : 1;

        // ---- item lines (nets) and who makes / uses them ----
        var lanes = new Dictionary<string, Lane>();
        Lane LaneFor(string producerKey, string item)
        {
            var id = $"{item}#{LineOf(producerKey)}";
            if (!lanes.TryGetValue(id, out var l)) lanes[id] = l = new Lane { Id = id, Item = item, Fluid = GameData.Item(item).IsFluid };
            return l;
        }
        var rowIn = new Dictionary<string, List<(Lane lane, double rate)>>();
        var rowOut = new Dictionary<string, List<(Lane lane, double rate)>>();
        var padIn = new List<(GraphNode node, Lane lane, double rate)>();
        var padOut = new List<(GraphNode node, Lane lane, double rate)>();
        void Add(Dictionary<string, List<(Lane, double)>> d, string key, Lane lane, double rate)
        {
            if (!d.TryGetValue(key, out var l)) d[key] = l = new();
            var i = l.FindIndex(x => x.Item1 == lane);
            if (i < 0) l.Add((lane, rate)); else l[i] = (lane, l[i].Item2 + rate);
        }
        foreach (var e in plan.Edges)
        {
            if (!nodes.TryGetValue(e.From, out var from) || !nodes.TryGetValue(e.To, out var to)) continue;
            var lane = LaneFor(e.From, e.Item);
            if (from.Kind == NodeKind.Machine) Add(rowOut, e.From, lane, e.Rate);
            else if (!padIn.Any(w => w.node == from && w.lane == lane)) padIn.Add((from, lane, from.Rate));
            if (to.Kind == NodeKind.Machine) Add(rowIn, e.To, lane, e.Rate);
            else
            {
                var i = padOut.FindIndex(w => w.node == to && w.lane == lane);
                if (i < 0) padOut.Add((to, lane, e.Rate)); else padOut[i] = (to, lane, padOut[i].rate + e.Rate);
            }
        }
        var outs = plan.Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().ToList());
        var depth = new Dictionary<string, int>(); var onStack = new HashSet<string>();
        int Depth(string k)
        {
            if (depth.TryGetValue(k, out var d)) return d;
            onStack.Add(k); int best = 0;
            foreach (var t in outs.GetValueOrDefault(k) ?? []) if (!onStack.Contains(t)) best = Math.Max(best, Depth(t) + 1);
            onStack.Remove(k); return depth[k] = best;
        }

        // ---- cells: machine groups (long ones split) and pads ----
        const int MaxPerCell = 4; // bigger groups split into parts that are placed independently (more compact)
        var cells = new List<Cell>();
        foreach (var n in nodes.Values.Where(n => n.Kind == NodeKind.Machine && n.Recipe != null).OrderByDescending(n => Depth(n.Key)))
        {
            if (fc != null && fc.FloorOf.GetValueOrDefault(n.Key) != fc.Floor) continue; // on another floor
            var building = n.Recipe!.Building;
            var (fw, fd) = Footprint(building);
            var ins = rowIn.GetValueOrDefault(n.Key) ?? [];
            var ous = rowOut.GetValueOrDefault(n.Key) ?? [];
            var p = Ports(building, fw, ins.Count(x => !x.lane.Fluid), ins.Count(x => x.lane.Fluid), ous.Count(x => !x.lane.Fluid), ous.Count(x => x.lane.Fluid));
            double[] Offsets(List<(Lane lane, double rate)> list, double[] itemPorts, double[] pipePorts)
            {
                int a = 0, b = 0;
                return list.Select(x => x.lane.Fluid
                    ? (pipePorts.Length > 0 ? pipePorts[Math.Min(b++, pipePorts.Length - 1)] : 0)
                    : (itemPorts.Length > 0 ? itemPorts[Math.Min(a++, itemPorts.Length - 1)] : 0)).ToArray();
            }
            int machines = Math.Max(1, n.Machines);
            int per = ins.Count > 2 || ous.Count > 2 ? 1 : MaxPerCell; // 3+ inputs can't share lanes without crossing
            if (BigPer > 0 && fw * fd >= 150) per = Math.Min(per, BigPer); // big machines: smaller groups pack tighter
            int subs = (int)Math.Ceiling(machines / (double)per);
            for (int sr = 0; sr < subs; sr++)
            {
                int count = machines / subs + (sr < machines % subs ? 1 : 0);
                cells.Add(new Cell
                {
                    Node = n, Building = building, W = fw, D = fd, P = fw + 2, K = count, Sub = sr, Subs = subs,
                    Share = (double)count / machines, Ins = ins, Outs = ous, Depth = Depth(n.Key),
                    InOff = Offsets(ins, p.itemIn, p.pipeIn), OutOff = Offsets(ous, p.itemOut, p.pipeOut),
                });
            }
        }
        string BoxOf(Lane l, GraphNode n) => l.Fluid ? (s.IndustrialFluidBox ? "Desc_IndustrialTank_C" : "Desc_PipeStorageTank_C")
            : n.Kind == NodeKind.Surplus && s.Sinks(l.Item.Split('#')[0]) ? SinkBox : "Desc_StorageContainerMk2_C";
        foreach (var (node, lane, rate) in padIn.Concat(padOut))
        {
            if (fc != null && fc.Floor != 0) break; // the boxes are on the ground floor
            bool input = padIn.Any(w => w.node == node && w.lane == lane);
            var box = BoxOf(lane, node);
            var (bw, bd) = Footprint(box);
            cells.Add(new Cell { Node = node, Building = box, W = bw, D = bd, Pad = new Slot(node, lane, rate, input, box, bw, bd, 0), Spare = node.Kind == NodeKind.Surplus });
        }
        if (fc != null)
        {
            // crossings: a lane made on one floor and used on another goes through a lift / pipe in the column. Here it
            // is a fixed box whose opening faces east: it takes the lane (made here) or delivers it (not made here).
            foreach (var (key, k) in fc.Slot)
            {
                var sp = fc.Span[key];
                var all = sp.src.Union(sp.snk).ToList();
                var (rx, ry) = RiserAt(k, s.BlueprintTile * Foundation);
                if (!lanes.TryGetValue(key[..key.LastIndexOf('@')], out var lane)) continue;
                var dummy = new GraphNode { Key = "riser:" + key, Item = lane.Item, Kind = NodeKind.Import };
                bool known = fc.RiserPos.TryGetValue(key, out var rp);
                if (known) (rx, ry) = (rp.x, rp.y);
                if (all.Contains(fc.Floor))
                {
                    bool input = !sp.src.Contains(fc.Floor);
                    string note = "⇅ " + GameData.Item(lane.Item).Name + " " + (input ? "↓ F" + sp.src.Min() : "→ F" + string.Join("/F", sp.snk.Where(f => f != fc.Floor)));
                    // the lowest floor it touches places it (like a small machine); the floors above keep that spot
                    cells.Add(new Cell { Node = dummy, Building = "riser", W = 2, D = 2, Pad = new Slot(dummy, lane, sp.rate, input, "riser", 2, 2, 0), Fixed = known, X = rx, Y = ry, Rot = known ? rp.rot : 3, Note = note, CrossKey = key });
                }
                else if (all.Min() < fc.Floor && fc.Floor < all.Max())
                {
                    // passing through: keep its hole clear (the same box as the crossing below)
                    var pc = new Cell { Node = dummy, Building = "riser", W = 2, D = 2, Rot = known ? rp.rot : 3, X = rx, Y = ry };
                    var pb = Box(pc, 0, 0, 2, 2);
                    cells.Add(new Cell { Node = dummy, Building = "riser", W = 2, D = 2, K = 1, Fixed = true, Void = "pass", X = pb.x0, Y = pb.y0, Note = "⇅ " + GameData.Item(lane.Item).Name });
                }
            }
            foreach (var h in fc.Holes)
                cells.Add(new Cell { Node = new GraphNode { Key = "hole", Item = "" }, Building = "hole", W = h.w, D = h.d, K = 1, Fixed = true, Void = "hole", X = h.x, Y = h.y, Note = h.note });
        }
        // local geometry and terminals
        foreach (var c in cells)
        {
            if (c.Pad != null)
            {
                c.BX0 = -1; c.BX1 = c.W + 1; c.BY0 = 0; c.BY1 = c.D + 1;
                c.Terms.Add(new Term(c.Pad.Lane, c.Pad.Input, c.W / 2, c.D + 2, 1)); // the port's centre (boxes sit on half metres when W is odd)
                continue;
            }
            double right = (c.K - 1) * c.P + c.W;
            c.BX0 = -5; c.BX1 = right + 1;
            c.BY0 = c.Ins.Count > 0 ? c.InY(c.Ins.Count - 1) - 1.5 : -1;
            c.BY1 = c.Outs.Count > 0 ? c.OutY(c.Outs.Count - 1) + 1.5 : c.D + 1;
            for (int j = 0; j < c.Ins.Count; j++) c.Terms.Add(new Term(c.Ins[j].lane, false, -6, c.InY(j), 2, j));
            for (int j = 0; j < c.Outs.Count; j++) c.Terms.Add(new Term(c.Outs[j].lane, true, -6, c.OutY(j), 2, c.Ins.Count + j));
            c.East = new bool[c.Ins.Count + c.Outs.Count];
            c.Bounds();
        }
        var nets = lanes.Values.Select(l => (lane: l, terms: cells.SelectMany(c => c.Terms.Where(t => t.Lane == l).Select(t => (c, t))).ToList()))
            .Where(n => n.terms.Any(t => t.t.Source) && n.terms.Any(t => !t.t.Source)).ToList();
        if (!NoClusters && Environment.GetEnvironmentVariable("PNR_NOCLUSTER") != "1") FormClusters(cells, nets, plan, s);

        double tile = s.BlueprintTile * Foundation;
        // ---- placement ----
        // place tight; where an item line can't be routed, give the cells on it more room and place again from there
        var total = System.Diagnostics.Stopwatch.StartNew();
        bool dbg = Environment.GetEnvironmentVariable("PNR_DEBUG") == "1";
        long budget = (long)((fc?.Budget ?? 25000) * TimeX); // (single floor: 25 s per weighting)
        // step 2: find the congested corridors (belts side by side) and route again with those corridors stacked —
        // the same placement; kept if it routes (the flat result otherwise)
        // compaction: push the groups together (the stacked corridors freed room), route and stack again; kept only if
        // it takes fewer blueprints (then less belt). Tried with the spacing reached so far, and with the tightest.
        double Score(Layout x) => tile > 0 ? BlueprintCount(x, s) * 1e6 + x.BeltLength : x.Width * x.Height * 10 + x.BeltLength;
        // (worth it only when the layout sticks just past a tile edge — ≤ 2 foundations — where pulling it back saves a
        // row / column of blueprints: measured, it's most of the time spent and otherwise almost never changes the count)
        bool Promising(Layout x) => tile > 0 && ((x.Width % tile > 0 && x.Width % tile <= 16) || (x.Height % tile > 0 && x.Height % tile <= 16));
        Layout Compacted(Layout cur, double tl)
        {
            if (Environment.GetEnvironmentVariable("PNR_NOCOMPACT") == "1" || !Promising(cur)) return cur;
            Layout.Say("status.compact");
            var keep = cells.Select(c => (c.X, c.Y, c.Rot, (bool[])c.East.Clone(), c.Halo)).ToArray();
            // (pushed with extra room kept for the belts: 4 m, then 2 m; the halo is only raised while pushing)
            foreach (int extra in new[] { 4, 2 })
            {
                if (total.ElapsedMilliseconds > budget * 1.5) break;
                bool tight = false;
                var halo0 = cells.Select(c => c.Halo).ToArray();
                foreach (var c in cells) c.Halo += extra;
                var tC = System.Diagnostics.Stopwatch.StartNew();
                bool moved = Place(cells, nets, s, tile, keep: true, compact: true);
                for (int i = 0; i < cells.Count; i++) cells[i].Halo = halo0[i];
                var fc2 = new List<Lane>();
                var lc = moved ? Route(cells, nets, s, tl, plan, fc2) : null;
                if (lc != null) lc = Stacked(lc, tl);
                Prof("compaction pass", tC.ElapsedMilliseconds);
                if (dbg) Console.WriteLine($"pnr: compaction (+{extra} m){(tight ? "" : "")}: {(lc == null ? "failed" : $"{lc.Width / 8}x{lc.Height / 8}, belt {lc.BeltLength:0}")} vs {cur.Width / 8}x{cur.Height / 8}, belt {cur.BeltLength:0}");
                if (lc != null && Score(lc) < Score(cur)) { cur = lc; keep = cells.Select(c => (c.X, c.Y, c.Rot, (bool[])c.East.Clone(), c.Halo)).ToArray(); }
                else for (int i = 0; i < cells.Count; i++) (cells[i].X, cells[i].Y, cells[i].Rot, cells[i].East, cells[i].Halo) = keep[i];
            }
            return cur;
        }
        // corridors found and shown, no stacking pass (it only pays off with compaction)
        Layout Corridored(Layout flat) { flat.Corridors = Layout.FindCorridors(flat); return flat; }
        Layout Stacked(Layout flat, double tl)
        {
            var found = Layout.FindCorridors(flat);
            if (found.Count == 0 || Environment.GetEnvironmentVariable("PNR_NOSTACK") == "1") { flat.Corridors = found; return flat; }
            Layout.Say("status.stack", found.Count);
            var f3 = new List<Lane>();
            var tS = System.Diagnostics.Stopwatch.StartNew();
            var st = Route(cells, nets, s, tl, plan, f3, found);
            Prof("stacking pass", tS.ElapsedMilliseconds);
            if (dbg) Console.WriteLine($"pnr: stacking {found.Count} corridor(s): {(st == null ? "failed — keeping the flat routing" : "ok")}");
            if (st == null) { flat.Corridors = found; return flat; }
            st.Corridors = found.Select(c => c with { Stacked = true }).ToList();
            return st;
        }
        bool Cancelled() => CancelAt is { } ca && System.Threading.Volatile.Read(ref ca.Value) is long cv && cv != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > cv;
        for (int attempt = 0; attempt < 12 && total.ElapsedMilliseconds < budget && !Cancelled(); attempt++)
        {
            var tPlace = System.Diagnostics.Stopwatch.StartNew();
            if (!Place(cells, nets, s, tile, keep: attempt > 0)) { if (dbg) Console.WriteLine("pnr: placement failed"); return null; }
            Prof("place", tPlace.ElapsedMilliseconds);
            // an opening walled in by other groups can never be reached: more room there and place again (no routing)
            var shut = Unreachable(cells, nets);
            if (shut.Count > 0 && attempt < 11)
            {
                if (dbg) Console.WriteLine($"pnr: attempt {attempt}: walled in: {string.Join(", ", shut.Select(l => l.Id))}");
                foreach (var c in cells.Where(c => !c.Fixed && c.Terms.Any(t => shut.Contains(t.Lane)))) c.Halo += 2;
                continue;
            }
            var failed = new List<Lane>();
            Layout.Say(attempt == 0 ? "status.route" : "status.reroute");
            var tRoute = System.Diagnostics.Stopwatch.StartNew();
            double predOver = RudyLog ? RudyOf(cells, nets) : 0;
            var L = Route(cells, nets, s, tile, plan, failed);
            Prof(L == null ? "route (failed)" : "route (ok)", tRoute.ElapsedMilliseconds);
            if (RudyLog) Console.WriteLine($"RUDYLOG {(L == null ? "fail" : "ok")} over {predOver:0} groups {cells.Count(c => c.Pad == null)} nets {nets.Count} attempt {attempt} ms {tRoute.ElapsedMilliseconds} why {(L == null ? FailWhy : "-")}");
            if (dbg) Console.WriteLine($"pnr: attempt {attempt}: routing {(L == null ? "failed: " + string.Join(", ", failed.Select(f => f.Id)) : "ok")}; cells " +
                string.Join(" ", cells.Select(c => $"{(c.Pad != null ? "pad" : c.Node.Item.Replace("Desc_", "").Replace("_C", ""))}@{c.X},{c.Y}r{c.Rot}h{c.Halo}")));
            if (L != null)
            {
                // everything could fit one blueprint tile but doesn't: one more placement aimed squarely at one tile
                if (tile > 0 && Math.Ceiling(L.Width / tile - 1e-9) * Math.Ceiling(L.Height / tile - 1e-9) > 1)
                {
                    double need = cells.Sum(c => (c.BX1 - c.BX0 + c.Halo) * (c.BY1 - c.BY0 + c.Halo));
                    if (dbg) Console.WriteLine($"pnr: one-tile check: cells need {need:0} m² of {tile * tile:0}");
                    if (need < tile * tile * 1.0)
                    {
                        var saved = cells.Select(c => (c.X, c.Y, c.Rot, (bool[])c.East.Clone(), c.Halo)).ToArray();
                        foreach (var c in cells) c.Halo = 2; // (packing into one tile: the tightest spacing again)
                        for (int k = 0; k < 6 && total.ElapsedMilliseconds < budget + 1000; k++)
                        {
                            // (every try a fresh pack in another order: a near miss often fits packed differently)
                            if (!Place(cells, nets, s, tile, keep: false, tileWeight: 20000, innerRules: false, bound: tile, seed: 99 + 7 * k)) continue;
                            var f2 = new List<Lane>();
                            var L2 = Route(cells, nets, s, 0, plan, f2); // no inner borders inside one tile
                            if (dbg) Console.WriteLine($"pnr: one-tile try {k}: {(L2 == null ? "failed" : $"{L2.Width / 8}x{L2.Height / 8}")}");
                            if (L2 != null && L2.Width <= tile + 1e-6 && L2.Height <= tile + 1e-6) return Stacked(L2, 0); // only a true one-tile result
                            foreach (var c in cells.Where(c => c.Terms.Any(t => f2.Contains(t.Lane)))) c.Halo += 1;
                        }
                        for (int i = 0; i < cells.Count; i++) (cells[i].X, cells[i].Y, cells[i].Rot, cells[i].East, cells[i].Halo) = saved[i];
                    }
                }
                return Promising(L) ? Compacted(Stacked(L, tile), tile) : Corridored(L);
            }
            // a cluster a failed line touches is dissolved: its groups get room like any other from now on
            foreach (int cid in cells.Where(c => c.Cluster >= 0 && c.Terms.Any(t => failed.Contains(t.Lane))).Select(c => c.Cluster).Distinct().ToList())
                foreach (var c in cells.Where(c => c.Cluster == cid)) c.Cluster = -1;
            foreach (var c in cells.Where(c => c.Terms.Any(t => failed.Contains(t.Lane)))) c.Halo += 2;
            // a big floor (many groups) that keeps failing: loosen it everywhere a little too
            if (LooseAt > 0 && attempt >= LooseAt && cells.Count(c => c.Pad == null && c.Void == null) >= 8)
                foreach (var c in cells.Where(c => !c.Fixed && c.Pad == null)) c.Halo += 1;
        }
        return null;
    }

    /// <summary>Lines with an opening that open floor doesn't connect to the line's other openings (walled in by groups).</summary>
    static List<Lane> Unreachable(List<Cell> cells, List<(Lane lane, List<(Cell c, Term t)> terms)> nets)
    {
        double maxX = cells.Max(c => Box(c, c.BX0, c.BY0, c.BX1, c.BY1).x1), maxY = cells.Max(c => Box(c, c.BX0, c.BY0, c.BX1, c.BY1).y1);
        int W = (int)Math.Ceiling(maxX) + 16, H = (int)Math.Ceiling(maxY) + 16;
        var obst = new bool[W * H];
        foreach (var c in cells)
        {
            var b = Box(c, c.BX0, c.BY0, c.BX1, c.BY1);
            for (int x = Math.Max(0, (int)Math.Floor(b.x0)); x < Math.Min(W, b.x1); x++)
                for (int y = Math.Max(0, (int)Math.Floor(b.y0)); y < Math.Min(H, b.y1); y++) obst[y * W + x] = true;
        }
        // connected open areas (4-neighbour flood fill)
        var comp = new int[W * H]; int nc = 0;
        var q = new Queue<int>();
        for (int i = 0; i < W * H; i++)
        {
            if (obst[i] || comp[i] != 0) continue;
            comp[i] = ++nc; q.Enqueue(i);
            while (q.Count > 0)
            {
                int k = q.Dequeue(), x = k % W, y = k / W;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    int j = ny * W + nx;
                    if (!obst[j] && comp[j] == 0) { comp[j] = nc; q.Enqueue(j); }
                }
            }
        }
        var bad = new List<Lane>();
        foreach (var (lane, terms) in nets)
        {
            var areas = terms.Select(q2 =>
            {
                var lt = q2.c.Local(q2.t); var (tx, ty) = World(q2.c, lt.lx, lt.ly);
                int x = (int)Math.Round(tx), y = (int)Math.Round(ty);
                return x >= 0 && y >= 0 && x < W && y < H ? comp[y * W + x] : 0;
            }).Distinct().ToList();
            if (areas.Count > 1 || areas.Contains(0)) bad.Add(lane);
        }
        return bad;
    }

    /// <summary>
    /// Clusters (floorplanning): a low-level product (at most 4 steps from raw: ingot → plate / rod → screw → RIP) with
    /// the groups that feed only it, as long as it fits comfortably in one tile. Each cluster is placed on its own
    /// first (its groups' arrangement is kept); the factory placement then moves it as one block.
    /// </summary>
    static void FormClusters(List<Cell> cells, List<(Lane lane, List<(Cell c, Term t)> terms)> nets, Plan plan, Settings s)
    {
        var mcells = cells.Where(c => c.Pad == null && c.Void == null && !c.Fixed).ToList();
        var byNode = mcells.GroupBy(c => c.Node.Key).ToDictionary(g => g.Key, g => g.ToList());
        var cons = byNode.Keys.ToDictionary(k => k, _ => new HashSet<string>());
        var prods = byNode.Keys.ToDictionary(k => k, _ => new HashSet<string>());
        var outside = new HashSet<string>(); // nodes with an output leaving the floor's machines (a box, another floor)
        foreach (var e in plan.Edges)
        {
            if (!byNode.ContainsKey(e.From) || e.From == e.To) continue;
            if (byNode.ContainsKey(e.To)) { cons[e.From].Add(e.To); prods[e.To].Add(e.From); }
            else outside.Add(e.From);
        }
        var depth = new Dictionary<string, int>(); var busy = new HashSet<string>();
        int Depth(string k)
        {
            if (depth.TryGetValue(k, out var d)) return d;
            if (!busy.Add(k)) return 1;
            d = 1 + prods[k].Select(Depth).DefaultIfEmpty(0).Max();
            busy.Remove(k); return depth[k] = d;
        }
        double tile = s.BlueprintTile > 0 ? s.BlueprintTile * Foundation : 48;
        double cap = 0.7 * tile * tile;
        double AreaOf(string k) => byNode[k].Sum(c => (c.BX1 - c.BX0 + 4) * (c.BY1 - c.BY0 + 4));
        // bottom-up clustering by connection strength (as in chip floorplanning): every group on its own, then merge the
        // two connected clusters with the strongest link (items / min between them) for their combined size, while it
        // fits the cap
        var flow = new Dictionary<(string, string), double>();
        foreach (var e in plan.Edges)
            if (byNode.ContainsKey(e.From) && byNode.ContainsKey(e.To) && e.From != e.To)
            {
                var k2 = string.CompareOrdinal(e.From, e.To) < 0 ? (e.From, e.To) : (e.To, e.From);
                flow[k2] = flow.GetValueOrDefault(k2) + e.Rate;
            }
        var owner = byNode.Keys.ToDictionary(k => k, k => k);          // node → its cluster's key
        var parts = byNode.Keys.ToDictionary(k => k, k => new HashSet<string> { k });
        var areaOf = byNode.Keys.ToDictionary(k => k, AreaOf);
        while (true)
        {
            var link = new Dictionary<(string, string), double>();
            foreach (var ((u, v), r) in flow)
            {
                string a = owner[u], b = owner[v];
                if (a == b) continue;
                var kk = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
                link[kk] = link.GetValueOrDefault(kk) + r;
            }
            var pick = link.Where(kv => areaOf[kv.Key.Item1] + areaOf[kv.Key.Item2] <= cap)
                .OrderByDescending(kv => kv.Value / (areaOf[kv.Key.Item1] + areaOf[kv.Key.Item2])).ThenBy(kv => kv.Key).FirstOrDefault();
            if (pick.Key == default) break;
            var (ka, kb) = pick.Key;
            foreach (var n in parts[kb]) { owner[n] = ka; parts[ka].Add(n); }
            areaOf[ka] += areaOf[kb];
            parts.Remove(kb); areaOf.Remove(kb);
        }
        int id = 0;
        foreach (var (key0, set) in parts.OrderBy(kv => kv.Key))
        {
            if (set.Count < 2) continue;
            double area = areaOf[key0];
            var members = set.SelectMany(k => byNode[k]).ToList();
            // lay the cluster out on its own: its groups and the lines among them only
            var sub = nets.Select(n => (n.lane, terms: n.terms.Where(q => members.Contains(q.c)).ToList()))
                .Where(n => n.terms.Any(q => q.t.Source) && n.terms.Any(q => !q.t.Source)).ToList();
            if (!Place(members, sub, s, 0, keep: false)) continue;
            var a = members[0];
            foreach (var c in members) { c.Cluster = id; c.RelX = c.X - a.X; c.RelY = c.Y - a.Y; }
            if (Environment.GetEnvironmentVariable("PNR_DEBUG") == "1") Console.WriteLine($"pnr: cluster {id}: {string.Join(", ", set)} ({members.Count} groups, {area:0} m²)");
            id++;
        }
    }

    // ================= placement =================
    // ================= crowding estimate (RUDY) =================
    // Each line spreads its expected belt length evenly over the box around its ends; summed per 4 m bin, that is the
    // belt demand there. Supply is the bin's free floor (machines taken out) at a 2 m belt pitch, ~1.5 levels (the ground
    // plus some stacking). The overflow (belt metres over supply) is what makes routing fail: placement pays for it.
    const double RudyBin = 4;
    static double RudyW = double.TryParse(Environment.GetEnvironmentVariable("PNR_RUDYW"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rw) ? rw : 0; // off: as a placement cost it spreads layouts over tile edges (see guide §2.8d)
    static double Rudy(List<Cell> cells, List<(Lane lane, List<(Cell c, Term t)> terms)> nets, double W, double H,
        Func<Cell, (double x0, double y0, double x1, double y1)> box, ref double[] buf)
    {
        const double B = RudyBin;
        int nx = Math.Max(1, (int)Math.Ceiling((W + 2) / B)), ny = Math.Max(1, (int)Math.Ceiling((H + 2) / B)), n = nx * ny;
        if (buf.Length < 2 * n) buf = new double[2 * n];
        var bb = buf;
        Array.Clear(bb, 0, 2 * n); // [0, n): demand · [n, 2n): blocked area
        void Spread((double x0, double y0, double x1, double y1) r, double dens, int off)
        {
            int bx0 = Math.Clamp((int)(r.x0 / B), 0, nx - 1), bx1 = Math.Clamp((int)((r.x1 - 1e-6) / B), 0, nx - 1);
            int by0 = Math.Clamp((int)(r.y0 / B), 0, ny - 1), by1 = Math.Clamp((int)((r.y1 - 1e-6) / B), 0, ny - 1);
            for (int by = by0; by <= by1; by++)
            {
                double oy = Math.Min(r.y1, (by + 1) * B) - Math.Max(r.y0, by * B); if (oy <= 0) continue;
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    double ox = Math.Min(r.x1, (bx + 1) * B) - Math.Max(r.x0, bx * B); if (ox <= 0) continue;
                    bb[off + by * nx + bx] += dens * ox * oy;
                }
            }
        }
        foreach (var c in cells) { if (c.X < -500) continue; Spread(box(c), 1, n); }
        foreach (var (lane, terms) in nets)
        {
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (var (c, t) in terms) { if (c.X < -500) continue; var lt = c.Local(t); var (wx, wy) = World(c, lt.lx, lt.ly); x0 = Math.Min(x0, wx); x1 = Math.Max(x1, wx); y0 = Math.Min(y0, wy); y1 = Math.Max(y1, wy); }
            if (x0 > x1) continue;
            // (a straight line still takes a belt's width: at least a bin across)
            if (x1 - x0 < B) { double m = (x0 + x1) / 2; x0 = m - B / 2; x1 = m + B / 2; }
            if (y1 - y0 < B) { double m = (y0 + y1) / 2; y0 = m - B / 2; y1 = m + B / 2; }
            double len = (x1 - x0) + (y1 - y0), dens = len / ((x1 - x0) * (y1 - y0)) * (lane.Fluid ? 2 : 1); // pipes: wider, ground only
            Spread((x0, y0, x1, y1), dens, 0);
        }
        double over = 0;
        for (int i = 0; i < n; i++)
        {
            double free = Math.Max(0, B * B - bb[n + i]);
            double cap = free / 2 * 1.5;
            if (bb[i] > cap) over += bb[i] - cap;
        }
        return over;
    }

    static readonly int FixMask = int.TryParse(Environment.GetEnvironmentVariable("PNR_FIX"), out var fm) ? fm : 0; // routing-failure fixes (1 opening clash, 2 border run-out, 4 hub exit, 8 bigger A* budget): each swings results by several blueprints, off (guide 2.8e)
    // belts: no splitter / merger within this many metres of a bend — one there sits in the curve and nothing snaps into
    // it (seen in game). 2 m: HMF keeps 11 blueprints with no such spot left (3 m: 15 blueprints). With machines placed
    // by hand it cost blueprints (10 -> 12) without clearing them, so it's off there. PNR_BENDJOIN overrides.
    [ThreadStatic] static int BendJoinR;
    static readonly bool RudyLog = Environment.GetEnvironmentVariable("PNR_RUDYLOG") == "1";
    static double RudyOf(List<Cell> cells, List<(Lane lane, List<(Cell c, Term t)> terms)> nets)
    {
        (double x0, double y0, double x1, double y1) WB(Cell c) => Box(c, c.BX0, c.BY0, c.BX1, c.BY1);
        var live = cells.Where(c => c.X > -500).ToList();
        double[] buf = [];
        return Rudy(cells, nets, live.Max(c => WB(c).x1), live.Max(c => WB(c).y1), WB, ref buf);
    }

    static bool Place(List<Cell> cells, List<(Lane lane, List<(Cell c, Term t)> terms)> nets, Settings s, double tile, bool keep, double tileWeight = 1500, bool innerRules = true, double bound = 0, int seed = 99, bool compact = false)
    {
        static bool Riser(Cell c) => c.Building == "riser" && c.Pad != null;
        var pads = cells.Where(c => c.Pad != null && !c.Fixed && !Riser(c)).ToList();
        var macros = cells.Where(c => (c.Pad == null || Riser(c)) && !c.Fixed).OrderByDescending(c => c.Depth).ToList();
        double halo = 2;
        // clusters move as one block (not in the one-tile pack, nor inside a cluster's own layout)
        bool clustered = bound == 0 && cells.Any(c => c.Cluster >= 0) && !cells.All(c => c.Cluster == cells[0].Cluster);
        var clusterOf = clustered ? cells.Where(c => c.Cluster >= 0 && !c.Fixed).GroupBy(c => c.Cluster).ToDictionary(g => g.Key, g => g.ToList()) : new();
        List<Cell> Block(Cell c) => clustered && c.Cluster >= 0 && clusterOf.ContainsKey(c.Cluster) ? clusterOf[c.Cluster] : [c];
        bool Lead(Cell c) => Block(c)[0] == c;
        void Shift(List<Cell> m, double dx, double dy) { foreach (var q in m) { q.X += dx; q.Y += dy; } }
        void Snap(List<Cell> m) { var a = m[0]; foreach (var q in m) { q.X = a.X - a.RelX + q.RelX; q.Y = a.Y - a.RelY + q.RelY; } }
        foreach (var m in clusterOf.Values) Snap(m);
        if (!keep) {
        // initial: pads along the south edge (spares a foundation apart), machine cells shelf-packed above
        double x = 2;
        foreach (var p in pads.OrderBy(p => p.Spare).ThenBy(p => p.Pad!.Input ? 0 : 1))
        {
            if (p.Spare && x > 2 && !pads.Any(q => q.Spare && q.X < x && q.X > 0)) x += 8;
            p.X = x + 1 + (p.W % 2 == 1 ? 0.5 : 0); p.Y = 0; p.Rot = 0; x += p.W + 4;
        }
        double area = macros.Sum(c => (c.BX1 - c.BX0 + 2 * halo) * (c.BY1 - c.BY0 + 2 * halo));
        double rowW = Math.Max(48, Math.Sqrt(area) * 1.3);
        double cx = x + halo, cy = 0, rowH = 14;
        foreach (var c in macros)
        {
            if (Riser(c)) continue; // starts in the column
            var blk = Block(c);
            if (blk.Count > 1)
            {
                // a cluster: its whole box as one item of the shelf
                if (!Lead(c)) continue;
                Snap(blk);
                double bx0 = blk.Min(q => Box(q, q.BX0, q.BY0, q.BX1, q.BY1).x0), by0 = blk.Min(q => Box(q, q.BX0, q.BY0, q.BX1, q.BY1).y0);
                double bx1 = blk.Max(q => Box(q, q.BX0, q.BY0, q.BX1, q.BY1).x1), by1 = blk.Max(q => Box(q, q.BX0, q.BY0, q.BX1, q.BY1).y1);
                double bw = bx1 - bx0 + 2 * halo, bh = by1 - by0 + 2 * halo;
                if (cx + bw > rowW && cx > 0) { cx = 0; cy += rowH; rowH = 0; }
                Shift(blk, SnapTo(cx + halo - bx0, 2), SnapTo(cy + halo - by0, 2));
                cx += bw; rowH = Math.Max(rowH, bh);
                continue;
            }
            c.Rot = 0;
            double w = c.BX1 - c.BX0 + 2 * halo, h = c.BY1 - c.BY0 + 2 * halo;
            if (cx + w > rowW && cx > 0) { cx = 0; cy += rowH; rowH = 0; }
            c.X = SnapTo(cx - c.BX0 + halo, 2); c.Y = SnapTo(cy - c.BY0 + halo, 2);
            cx = c.X + c.BX1 + halo; rowH = Math.Max(rowH, c.Y + c.BY1 + halo - cy);
        }
        }

        (double x0, double y0, double x1, double y1) WB(Cell c) => Box(c, c.BX0, c.BY0, c.BX1, c.BY1);
        bool multiFloor = cells.Any(c => c.Fixed);
        // tile rules apply at borders between tiles only (x, y = k·tile with k ≥ 1), not at the factory's own edges; the
        // one-tile pass drops them (a single tile has no inner borders)
        bool Straddles(double a, double b) => tile > 0 && innerRules && Math.Floor((a + 0.01) / tile) != Math.Floor((b - 0.01) / tile);
        // an opening's clear run-out: 5 m, and where a border zone lies in it (belts cross those straight only) on
        // through the zone and 3 m past it, so the bend after the crossing fits
        bool Band(double v) => tile > 0 && innerRules && !s.HandPlaceAcrossTiles && Math.Round(v / tile) >= 1 && Math.Abs(v - Math.Round(v / tile) * tile) < 2.5;
        int RunOut(double tx, double ty, int dir)
        {
            int run = 5;
            for (int i = 1; i <= run && i < 20 && (FixMask & 2) != 0; i++)
                if (Band(tx + DX[dir] * i) || Band(ty + DY[dir] * i)) run = Math.Max(run, i + 4);
            return run;
        }
        // one line's run-out may not run into another line's opening (its 2 m approach is reserved): no room to bend
        bool OpeningsClash(Cell a, Cell o)
        {
            foreach (var ta in a.Terms)
            {
                var la = a.Local(ta); int da = (la.dir + a.Rot) & 3; var (ax, ay) = World(a, la.lx, la.ly);
                foreach (var tb in o.Terms)
                {
                    if (tb.Lane == ta.Lane) continue; // (a producer facing its own consumer: one straight belt / curve)
                    var lb = o.Local(tb); int db = (lb.dir + o.Rot) & 3; var (bx, by) = World(o, lb.lx, lb.ly);
                    if (Math.Abs(ax - bx) + Math.Abs(ay - by) > 12) continue;
                    for (int i = 1; i <= 4; i++)
                    {
                        double px = ax + DX[da] * i, py = ay + DY[da] * i;
                        for (int j = 0; j <= 2; j++)
                            if (Math.Abs(px - (bx + DX[db] * j)) < 1.5 && Math.Abs(py - (by + DY[db] * j)) < 1.5) return true;
                    }
                }
            }
            return false;
        }
        bool Valid(Cell c)
        {
            if (c.Fixed) return true; // others keep clear of it (they check against every cell)
            var b = WB(c);
            if (b.x0 < 1 || b.y0 < (c.Pad != null ? 0 : 1)) return false;
            if (bound > 0 && (b.x1 > bound - 1 || b.y1 > bound - 1)) return false; // hard: stay inside one tile
            // every lane end needs a clear run-out (5 m) before a belt can turn: not into any other cell
            bool RunOutHits(Cell from, (double x0, double y0, double x1, double y1) box)
            {
                foreach (var tm in from.Terms)
                {
                    var lt = from.Local(tm);
                    int dir = (lt.dir + from.Rot) & 3;
                    var (tx, ty) = World(from, lt.lx, lt.ly);
                    int run = RunOut(tx, ty, dir);
                    for (int i = 0; i <= run; i++)
                    {
                        double px = tx + DX[dir] * i, py = ty + DY[dir] * i;
                        if (px > box.x0 - 1.5 && px < box.x1 + 1.5 && py > box.y0 - 1.5 && py < box.y1 + 1.5) return true;
                    }
                }
                return false;
            }
            // multi-floor (a crossing column on the west edge): the run-out stays inside the factory too
            foreach (var tm in c.Terms)
            {
                var lt = c.Local(tm); int dir = (lt.dir + c.Rot) & 3; var (tx, ty) = World(c, lt.lx, lt.ly);
                if (multiFloor && (tx + DX[dir] * 3 < 0 || ty + DY[dir] * 3 < 0)) return false;
            }
            foreach (var o in cells)
            {
                if (o == c || o.X < -500) continue;
                if (clustered && c.Cluster >= 0 && o.Cluster == c.Cluster) continue; // (a cluster's own arrangement stands)
                var q = WB(o);
                if (RunOutHits(c, q) || RunOutHits(o, b)) return false;
                if ((FixMask & 1) != 0 && (OpeningsClash(c, o) || OpeningsClash(o, c))) return false;
                double h = (c.Pad != null && o.Pad != null) ? 1 : Math.Max(c.Halo, o.Halo);
                if ((c.Pad != null) != (o.Pad != null)) h = Math.Max(h, 3); // room in front of a box's opening
                if (b.x0 < q.x1 + h && q.x0 < b.x1 + h && b.y0 < q.y1 + h && q.y0 < b.y1 + h) return false;
            }
            if (tile > 0)
            {
                if (Riser(c))
                {
                    // a crossing (a floor hole): not across a border, its opening 4 m clear of any border
                    var (tx, ty, _) = (World(c, c.Terms[0].LX, c.Terms[0].LY).x, World(c, c.Terms[0].LX, c.Terms[0].LY).y, 0);
                    return !Straddles(b.x0, b.x1) && !Straddles(b.y0, b.y1) && !(innerRules && (Near(tx) || Near(ty)));
                }
                if (c.Pad != null) return !Straddles(b.x0, b.x1);
                int across = 0;
                for (int m = 0; m < c.K; m++)
                {
                    var r = Box(c, m * c.P, 0, m * c.P + c.W, c.D);
                    if (Straddles(r.x0, r.x1) || Straddles(r.y0, r.y1)) { if (!s.HandPlaceAcrossTiles) return false; across++; }
                }
                // placed by hand across a border: fine while one machine of the group sits inside a tile (the others line
                // up from it); if every machine is placed by hand, the group's corner must be on a foundation corner
                if (across == c.K)
                {
                    var r0 = Box(c, 0, 0, c.W, c.D);
                    if (Math.Abs(r0.x0 / Foundation - Math.Round(r0.x0 / Foundation)) > 1e-6 || Math.Abs(r0.y0 / Foundation - Math.Round(r0.y0 / Foundation)) > 1e-6) return false;
                }
                // manifold tracks may not run along a border (they'd be cut lengthwise)
                bool horiz = (c.Rot & 1) == 0;
                for (int j = 0; j < c.Ins.Count + c.Outs.Count; j++)
                {
                    double ly = j < c.Ins.Count ? c.InY(j) : c.OutY(j - c.Ins.Count);
                    var (wx, wy) = World(c, 0, ly);
                    double v = horiz ? wy : wx;
                    if (innerRules && !s.HandPlaceAcrossTiles && Math.Round(v / tile) >= 1 && Math.Abs(v - Math.Round(v / tile) * tile) < 2.5) return false; // a lane (and its splitters) off the border
                }
                // an upper lane's lift (at its connection end) stands clear of borders too: nothing but a straight belt crosses
                bool Near(double v) => Math.Round(v / tile) >= 1 && Math.Abs(v - Math.Round(v / tile) * tile) < 4;
                for (int j = 0; j < c.Ins.Count + c.Outs.Count; j++)
                {
                    int side = j < c.Ins.Count ? 0 : 1, jj = side == 0 ? j : j - c.Ins.Count;
                    if (!innerRules || s.HandPlaceAcrossTiles || !c.Up(side, jj)) continue;
                    double ly = side == 0 ? c.InY(jj) : c.OutY(jj);
                    var (lx, lyw) = World(c, c.East[j] ? c.Right + 2 : -2, ly);
                    if (Near(lx) || Near(lyw)) return false;
                }
            }
            return true;
        }
        // busy lines weigh more (1 + items/min ÷ 60); lines from an input box twice: ore processing sits by its box
        var weight = nets.ToDictionary(n => n.lane, n =>
        {
            double rate = n.terms.Where(q => q.t.Source).Sum(q => q.c.Pad != null ? q.c.Pad.Rate
                : q.c.Outs.Where(o => o.lane == n.lane).Sum(o => o.rate * q.c.Share));
            return (1 + rate / RateDiv) * (n.terms.Any(q => q.t.Source && q.c.Pad != null) ? RawMul : 1);
        });
        double avgW = weight.Values.DefaultIfEmpty(1).Average();
        double[] rudyBuf = [];
        var groups = cells.Where(c => c.Pad == null && c.Subs > 1).GroupBy(c => c.Node).Select(gp => gp.ToList()).ToList();
        double Cost()
        {
            double sum = 0;
            foreach (var (lane, terms) in nets)
            {
                double w = weight[lane];
                double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
                foreach (var (c, t) in terms) { var lt = c.Local(t); var (wx, wy) = World(c, lt.lx, lt.ly); x0 = Math.Min(x0, wx); x1 = Math.Max(x1, wx); y0 = Math.Min(y0, wy); y1 = Math.Max(y1, wy); }
                sum += w * ((x1 - x0) + (y1 - y0));
                // a producer end and a consumer end a little off-line force a jog: line them up instead
                var src = terms.FirstOrDefault(q => q.t.Source);
                if (src.c != null)
                {
                    var ls = src.c.Local(src.t); var ps = World(src.c, ls.lx, ls.ly); int sd = (ls.dir + src.c.Rot) & 3;
                    foreach (var (c2, t2) in terms)
                    {
                        if (t2.Source) continue;
                        var l2 = c2.Local(t2); var p2 = World(c2, l2.lx, l2.ly);
                        double off = (sd & 1) == 0 ? Math.Abs(p2.y - ps.y) : Math.Abs(p2.x - ps.x);
                        if (off > 0.5 && off < 6) sum += 60 * w; // near-misses force S-bends: line them up
                        // bends this connection will need, from how its two ends face (one producer / one consumer)
                        if (terms.Count == 2)
                        {
                            int td = (l2.dir + c2.Rot) & 3, arrive = (td + 2) & 3;
                            double run = (p2.x - ps.x) * DX[sd] + (p2.y - ps.y) * DY[sd];
                            double sideOff = Math.Abs((p2.x - ps.x) * DY[sd] - (p2.y - ps.y) * DX[sd]);
                            int bends = arrive == sd ? (run >= 3 && (sideOff < 0.5 || (run <= 16 && run * run >= 12 * sideOff)) ? 0 : 2)
                                      : (arrive & 1) != (sd & 1) ? 1 : 3;
                            sum += 12 * w * bends;
                        }
                        else
                        {
                            // several consumers: one whose opening faces away from the supply (flow must arrive heading
                            // back towards the producer) needs the line to overshoot and hook back — two extra bends
                            int arrive = ((l2.dir + c2.Rot) & 3) ^ 2;
                            double along = (p2.x - ps.x) * DX[arrive] + (p2.y - ps.y) * DY[arrive];
                            if (along < 0 && lane.Fluid) sum += 12 * w; // pipes: a hook needs room a belt tucks in more easily
                        }
                    }
                }
            }
            // parts of one machine group: a mild pull together (splitting is fine, drifting apart for nothing is not)
            foreach (var grp in groups)
                for (int i = 1; i < grp.Count; i++)
                {
                    var a = WB(grp[i - 1]); var b2 = WB(grp[i]);
                    sum += 0.3 * avgW * (Math.Abs((a.x0 + a.x1) / 2 - (b2.x0 + b2.x1) / 2) + Math.Abs((a.y0 + a.y1) / 2 - (b2.y0 + b2.y1) / 2));
                }
            double W = cells.Max(c => WB(c).x1), H = cells.Max(c => WB(c).y1);
            double cost = sum + avgW * (1.0 * (W + H) + 0.01 * W * H);
            if (RudyW > 0) cost += avgW * RudyW * Rudy(cells, nets, W, H, WB, ref rudyBuf);
            if (tile > 0 && s.HandPlaceAcrossTiles && innerRules && UsedTiles is { Count: > 0 }) // (floors above the first)
            {
                // machines may cross tiles: count the blueprints it takes — the tiles the groups actually cover (one
                // blueprint holds every floor of a tile: tiles the floors below already use cost little)
                var occ = new HashSet<(int, int)>();
                foreach (var c in cells)
                {
                    if (c.X < -500) continue;
                    var b = WB(c);
                    for (int tx = (int)Math.Floor(b.x0 / tile); tx <= (int)Math.Floor((b.x1 - 0.01) / tile); tx++)
                        for (int ty = (int)Math.Floor(b.y0 / tile); ty <= (int)Math.Floor((b.y1 - 0.01) / tile); ty++) occ.Add((tx, ty));
                }
                int fresh = UsedTiles == null ? occ.Count : occ.Count(q => !UsedTiles.Contains(q));
                cost += avgW * tileWeight * (fresh + 0.2 * (occ.Count - fresh));
            }
            if (tile > 0) cost += avgW * tileWeight * Math.Ceiling(W / tile - 1e-9) * Math.Ceiling(H / tile - 1e-9);
            return cost;
        }
        // make the start valid: push overlapping cells up until they fit
        if (bound > 0 && !keep)
        {
            // one tile: pack greedily inside it (bottom-left first, any rotation); try several cell orders
            var prng = new Random(seed);
            bool packed = false;
            for (int tryN = 0; tryN < 40 && !packed; tryN++)
            {
                var orderP = tryN == 0 ? macros.OrderByDescending(c => (c.BX1 - c.BX0) * (c.BY1 - c.BY0)).ToList() : macros.OrderBy(_ => prng.Next()).ToList();
                foreach (var c in macros) { c.X = -1000; c.Y = -1000; }
                packed = true;
                foreach (var c in orderP)
                {
                    bool placed = false;
                    for (double yy = 0; yy < bound && !placed; yy += 2)
                        for (double xx = 0; xx < bound && !placed; xx += 2)
                            for (int rot = 0; rot < 4 && !placed; rot++)
                            {
                                c.Rot = rot;
                                var (bx, by) = Rotate(c.BX0, c.BY0, rot); var (bx2, by2) = Rotate(c.BX1, c.BY1, rot);
                                c.X = Math.Round((xx - Math.Min(bx, bx2) + c.Halo) / 2) * 2; c.Y = Math.Round((yy - Math.Min(by, by2) + c.Halo) / 2) * 2;
                                placed = Valid(c);
                            }
                    if (!placed) { c.X = -1000; packed = false; break; }
                }
            }
            if (!packed) { if (Environment.GetEnvironmentVariable("PNR_DEBUG") == "1") Console.WriteLine("pnr: one-tile pack: no order fits"); return false; }
        }
        // make every cell valid: the nearest free spot (2 m steps, searching outwards in both directions)
        foreach (var c in pads) { c.X = Math.Floor(c.X) + (c.W % 2 == 1 ? 0.5 : 0); for (int g = 0; g < 200 && !Valid(c); g++) c.X += 1; } // odd-width boxes on half metres: port centre on the grid
        foreach (var c in macros)
        {
            var blk = Block(c);
            if (blk.Count > 1)
            {
                // a cluster: slide the whole block to the nearest spot where every group of it is valid
                if (!Lead(c) || blk.All(Valid)) continue;
                bool okB = false;
                for (int r = 1; r < 120 && !okB; r++)
                    for (int dx = -r; dx <= r && !okB; dx++)
                        foreach (int dy in new[] { r - Math.Abs(dx), -(r - Math.Abs(dx)) })
                        {
                            Shift(blk, dx * 2, dy * 2);
                            if (blk.All(Valid)) { okB = true; break; }
                            Shift(blk, -dx * 2, -dy * 2);
                        }
                continue;
            }
            if (Valid(c)) continue;
            double x0 = c.X, y0 = c.Y; bool ok = false;
            for (int r = 1; r < 120 && !ok; r++)
                for (int dx = -r; dx <= r && !ok; dx++)
                    foreach (int dy in new[] { r - Math.Abs(dx), -(r - Math.Abs(dx)) })
                    {
                        c.X = x0 + dx * 2; c.Y = y0 + dy * 2;
                        foreach (int rot in new[] { c.Rot, (c.Rot + 1) & 3 }) { c.Rot = rot; if (Valid(c)) { ok = true; break; } }
                        if (ok) break;
                    }
        }
        if (cells.Any(c => !Valid(c))) return false;

        if (compact)
        {
            // compaction: push every group west as far as the rules allow (2 m steps), then south; twice over
            var movers = cells.Where(c => !c.Fixed && (c.Pad == null || c.Building == "riser") && Lead(c)).ToList();
            var boxes = cells.Where(c => c.Pad != null && c.Building != "riser" && !c.Fixed).ToList();
            // (and every opening keeps 3 m of run-out inside the factory: the edge row / column is no place for belts)
            bool Ok(Cell c) => Valid(c) && c.Terms.All(tm =>
            {
                var lt = c.Local(tm); int dir = (lt.dir + c.Rot) & 3; var (tx, ty) = World(c, lt.lx, lt.ly);
                return tx + DX[dir] * 3 >= 2 && ty + DY[dir] * 3 >= 2;
            });
            for (int round = 0; round < 2; round++)
            {
                foreach (var c in movers.OrderBy(c => WB(c).x0)) { var bk = Block(c); while (true) { Shift(bk, -2, 0); if (!bk.All(Ok)) { Shift(bk, 2, 0); break; } } }
                foreach (var c in boxes.OrderBy(c => c.X)) { while (true) { c.X -= 1; if (!Ok(c)) { c.X += 1; break; } } }
                foreach (var c in movers.OrderBy(c => WB(c).y0)) { var bk = Block(c); while (true) { Shift(bk, 0, -2); if (!bk.All(Ok)) { Shift(bk, 0, 2); break; } } }
            }
            return cells.All(Valid);
        }
        var rng = new Random(4242 + 7919 * Seed); // (portfolio: another seed, another annealing path)
        double cur = Cost(), t0 = Math.Max(10, cur * 0.05);
        int iters = 20000 + 2000 * cells.Count; // fixed, so results repeat; the time limit is only a safety cutoff
        var best = cells.Select(c => (c.X, c.Y, c.Rot, (bool[])c.East.Clone())).ToArray(); double bestCost = cur;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int it = 0; it < iters; it++)
        {
            if ((it & 255) == 0 && clock.ElapsedMilliseconds > 2500 * TimeX) break; // safety cutoff
            double temp = t0 * Math.Pow(0.001, it / (double)iters);
            var c = cells[rng.Next(cells.Count)];
            if (c.Fixed) continue;
            var blkM = Block(c);
            if (blkM.Count > 1)
            {
                // a cluster: translate the whole block
                int bmv = rng.Next(2);
                double ddx = bmv == 0 ? (rng.Next(5) - 2) * 2 : (rng.Next(9) - 4) * 4, ddy = bmv == 0 ? (rng.Next(5) - 2) * 2 : (rng.Next(9) - 4) * 4;
                if (ddx == 0 && ddy == 0) continue;
                Shift(blkM, ddx, ddy);
                if (!blkM.All(Valid)) { Shift(blkM, -ddx, -ddy); continue; }
                double ncB = Cost();
                if (ncB < cur || rng.NextDouble() < Math.Exp((cur - ncB) / temp))
                {
                    cur = ncB;
                    if (ncB < bestCost) { bestCost = ncB; best = cells.Select(q => (q.X, q.Y, q.Rot, (bool[])q.East.Clone())).ToArray(); }
                }
                else Shift(blkM, -ddx, -ddy);
                continue;
            }
            var (ox, oy, orot) = (c.X, c.Y, c.Rot);
            var oEast = (bool[])c.East.Clone();
            Cell? other = null; (double, double, int) otherOld = default;
            int mv = rng.Next(5);
            if (c.Pad == null && mv == 4 && c.East.Length > 0 && !c.Direct) { int j = rng.Next(c.East.Length); c.East[j] = !c.East[j]; c.Bounds(); }
            else if (c.Pad != null && !Riser(c)) { c.X = Math.Max(2, c.X + (rng.Next(2) == 0 ? -1 : 1) * 2 * (1 + rng.Next(6))); c.X = Math.Floor(c.X) + (c.W % 2 == 1 ? 0.5 : 0); }
            else if (mv == 0) { c.X += (rng.Next(5) - 2) * 2; c.Y += (rng.Next(5) - 2) * 2; }
            else if (mv == 1) { c.Rot = rng.Next(4); }
            else if (mv == 2)
            {
                // swap with another machine cell
                other = macros[rng.Next(macros.Count)];
                if (other == c || Block(other).Count > 1) continue;
                otherOld = (other.X, other.Y, other.Rot);
                (c.X, c.Y, other.X, other.Y) = (other.X, other.Y, c.X, c.Y);
            }
            else { c.X += (rng.Next(9) - 4) * 4; c.Y += (rng.Next(9) - 4) * 4; }
            if (!Valid(c) || (other != null && !Valid(other)))
            {
                (c.X, c.Y, c.Rot) = (ox, oy, orot); c.East = oEast; c.Bounds();
                if (other != null) (other.X, other.Y, other.Rot) = otherOld;
                continue;
            }
            double nc = Cost();
            if (nc < cur || rng.NextDouble() < Math.Exp((cur - nc) / temp))
            {
                cur = nc;
                if (nc < bestCost) { bestCost = nc; best = cells.Select(q => (q.X, q.Y, q.Rot, (bool[])q.East.Clone())).ToArray(); }
            }
            else
            {
                (c.X, c.Y, c.Rot) = (ox, oy, orot); c.East = oEast; c.Bounds();
                if (other != null) (other.X, other.Y, other.Rot) = otherOld;
            }
        }
        for (int i = 0; i < cells.Count; i++) { (cells[i].X, cells[i].Y, cells[i].Rot, cells[i].East) = best[i]; cells[i].Bounds(); }
        return true;
    }

    // ================= routing =================
    sealed class Grid
    {
        public int W, H;
        public bool[] Obst = [];                          // machines / boxes (both levels)
        public bool[] LiftSpot = [];                      // lifts inside machine groups (their upper lanes)
        public bool[]? Stack;                             // stacking pass: a congested corridor (cheap up high, dear on the ground)
        public int[] Res = [];                            // terminal cells: reserved for one net (id), else 0
        public short[][] Area = new short[NL][];          // per level: how many nets' belts (with clearance) cover a cell
        public float[][] Hist = new float[NL][];          // per level: congestion history (grows where nets fought)
        public float Pres = 0.5f;                         // price of sharing space with another net this round
        public HashSet<int> Own = new();                  // the current net's own cells (level·W·H + index)
        public HashSet<object> Smooth = new();            // paths drawn as one smooth curve (not grid steps)
        public bool CurFluid;                             // routing a pipe: it stays on the ground (no rise → no pump)
        public bool CurLocal;                             // routing a local line: flat on the ground (no lifts / ramps)
        public int[][] OwnStamp = new int[NL][];          // = Stamp where the current net already is (fast Own test)
        public int[][] CenStamp = new int[NL][];          // = Stamp on the current net's own belt centre lines
        public int[] CorrStamp = [], CorrOwner = [];      // = Stamp: the approach to one of this net's openings (owner: the opening's cell)
        public int AllowA = -1, AllowB = -1;              // the openings the current search may approach
        public int Stamp;
        // A* buffers, reused between searches (generation-stamped instead of cleared)
        public float[] Dist = []; public int[] Prev = []; public int[] Gen = []; public int Generation;
        public long Deadline;                             // Stopwatch ticks
        public System.Runtime.CompilerServices.StrongBox<long>? Cancel; // portfolio: a shared stop (Stopwatch timestamp)
        public bool Over => Clock.ElapsedTicks > Deadline || (Cancel is { } c && System.Threading.Volatile.Read(ref c.Value) is long v && v != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > v);
        public System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public int Idx(int x, int y) => y * W + x;
        public bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
        public bool Usable(int x, int y, int id) => In(x, y) && !Obst[Idx(x, y)] && (Res[Idx(x, y)] == 0 || Res[Idx(x, y)] == id);
        /// <summary>Extra cost of a belt centre at (x, y) on level l: history, plus the price of every other net there.</summary>
        public float Pen(int x, int y, int l)
        {
            int i = Idx(x, y), others = Area[l][i] - (OwnStamp[l][i] == Stamp ? 1 : 0);
            return Hist[l][i] + (others > 0 ? Pres * others : 0);
        }
    }
    sealed class TreeCell { public int X, Y, Level, Dir; public bool Downstream; public bool Joinable; }

    static Layout? Route(List<Cell> cells, List<(Lane lane, List<(Cell c, Term t)> terms)> nets, Settings s, double tile, Plan plan, List<Lane> failedOut, List<Layout.Corridor>? stack = null)
    {
        BendJoinR = int.TryParse(Environment.GetEnvironmentVariable("PNR_BENDJOIN"), out var bj) ? bj : s.HandPlaceAcrossTiles ? 0 : 2;
        double maxX = cells.Max(c => Box(c, c.BX0, c.BY0, c.BX1, c.BY1).x1), maxY = cells.Max(c => Box(c, c.BX0, c.BY0, c.BX1, c.BY1).y1);
        var g = new Grid { W = (int)Math.Ceiling(maxX) + 16, H = (int)Math.Ceiling(maxY) + 16, Cancel = CancelAt };
        int N = g.W * g.H;
        if (stack != null)
        {
            // the corridors to stack (and 1 m around them)
            g.Stack = new bool[N];
            foreach (var cr in stack)
                for (int x = (int)Math.Floor(cr.X0) - 1; x <= (int)Math.Ceiling(cr.X1) + 1; x++)
                    for (int y = (int)Math.Floor(cr.Y0) - 1; y <= (int)Math.Ceiling(cr.Y1) + 1; y++)
                        if (g.In(x, y)) g.Stack[g.Idx(x, y)] = true;
        }
        g.Obst = new bool[N]; g.Res = new int[N]; g.LiftSpot = new bool[N];
        for (int l = 0; l < NL; l++) { g.Area[l] = new short[N]; g.Hist[l] = new float[N]; g.OwnStamp[l] = new int[N]; g.CenStamp[l] = new int[N]; }
        g.CorrStamp = new int[N]; g.CorrOwner = new int[N];
        // belts / pipes stay inside the factory: not on its west / south edge line (half of them would stick out —
        // a tile of their own in the blueprints)
        for (int x = 0; x < g.W; x++) g.Obst[g.Idx(x, 0)] = true;
        for (int y = 0; y < g.H; y++) g.Obst[g.Idx(0, y)] = true;
        g.Dist = new float[N * NL * 4]; g.Prev = new int[N * NL * 4]; g.Gen = new int[N * NL * 4];
        foreach (var c in cells)
        {
            var b = Box(c, c.BX0, c.BY0, c.BX1, c.BY1);
            for (int x = (int)Math.Floor(b.x0); x < b.x1; x++)
                for (int y = (int)Math.Floor(b.y0); y < b.y1; y++)
                    if (g.In(x, y)) g.Obst[g.Idx(x, y)] = true;
            // the lifts of its upper lanes (at the lane's connection end, and down to each machine)
            for (int side = 0; side < 2 && c.Pad == null && c.Void == null && !c.Direct; side++)
            {
                var list = side == 0 ? c.Ins : c.Outs;
                for (int j = 0; j < list.Count; j++)
                {
                    if (!c.Up(side, j)) continue;
                    double ty = side == 0 ? c.InY(j) : c.OutY(j), faceY = side == 0 ? 0 : c.D, sg = side == 0 ? 1 : -1;
                    var spots = new List<(double x, double y)> { World(c, c.East[side == 0 ? j : c.Ins.Count + j] ? c.Right + 2 : -2, ty) };
                    for (int m = 0; m < c.K; m++) spots.Add(World(c, c.PortX(m, side == 0 ? c.InOff[j] : c.OutOff[j]), faceY - sg * 1));
                    foreach (var (lx, ly) in spots) // (a routed lift looks 2 m around itself for these)
                        if (g.In((int)Math.Round(lx), (int)Math.Round(ly))) g.LiftSpot[g.Idx((int)Math.Round(lx), (int)Math.Round(ly))] = true;
                }
            }
        }
        // within 4 m of a border between tiles a belt / pipe only crosses straight at ground level: it is cut 1–2 m short
        // of the border there (bridged by hand) and still needs straight length before its next bend
        // (no zones when machines may cross tiles: belts / pipes bending near a border are finished by hand there)
        bool NearBorder(double v) => tile > 0 && !s.HandPlaceAcrossTiles && Math.Round(v / tile) >= 1 && Math.Abs(v - Math.Round(v / tile) * tile) < 2.5; // pipes end 2 m short of a border in the export (they over-extend), belts 1 m

        // terminal cells (outside their cell, pointing out)
        (int x, int y, int dir) TermCell(Cell c, Term t)
        {
            var lt = c.Local(t);
            var (wx, wy) = World(c, lt.lx, lt.ly);
            int dir = (lt.dir + c.Rot) & 3;
            return ((int)Math.Round(wx), (int)Math.Round(wy), dir);
        }
        // lines that can be one direct curve go first each round, so the space for it is still free
        bool CanCurve((Lane lane, List<(Cell c, Term t)> terms) n)
        {
            if (n.terms.Count != 2 || n.terms.Count(q => q.t.Source) != 1) return false;
            var a = TermCell(n.terms.First(q => q.t.Source).c, n.terms.First(q => q.t.Source).t);
            var b = TermCell(n.terms.First(q => !q.t.Source).c, n.terms.First(q => !q.t.Source).t);
            if (a.dir != ((b.dir + 2) & 3)) return false;
            double run = (b.x - a.x) * DX[a.dir] + (b.y - a.y) * DY[a.dir], side = Math.Abs((b.x - a.x) * DY[a.dir] - (b.y - a.y) * DX[a.dir]);
            return run >= 3 && run <= 16 && (side < 0.01 || run * run >= 12 * side);
        }
        // line classes: global = touches a box or a floor crossing, or spans more than 40 m (raw inputs, products, long
        // lines between stages); local = short, between neighbouring groups. Local lines are routed first, flat and
        // direct; global ones after, free to lift over them onto stacked levels (buses).
        bool Global((Lane lane, List<(Cell c, Term t)> terms) n)
        {
            if (n.terms.Any(q => q.c.Pad != null)) return true;
            var pts = n.terms.Select(q => { var lt = q.c.Local(q.t); return World(q.c, lt.lx, lt.ly); }).ToList();
            return pts.Max(q => q.x) - pts.Min(q => q.x) + pts.Max(q => q.y) - pts.Min(q => q.y) > 40;
        }
        var global = nets.Where(Global).Select(n => n.lane).ToHashSet();
        bool twoStage = TwoStage;
        var order = nets.OrderByDescending(CanCurve).ThenBy(n => twoStage && global.Contains(n.lane)).ThenByDescending(n => n.terms.Count).ThenBy(n => n.lane.Id).ToList();
        var netId = new Dictionary<Lane, int>();
        for (int ni = 0; ni < order.Count; ni++) netId[order[ni].lane] = ni + 1;
        // every terminal (and 2 m outwards) belongs to its own net
        foreach (var net in order)
            foreach (var (c, t) in net.terms)
            {
                var (tx, ty, td) = TermCell(c, t);
                for (int i = 0; i <= 2; i++)
                {
                    int rx = tx + DX[td] * i, ry = ty + DY[td] * i;
                    if (g.In(rx, ry) && !g.Obst[g.Idx(rx, ry)] && g.Res[g.Idx(rx, ry)] == 0) g.Res[g.Idx(rx, ry)] = netId[net.lane];
                }
            }

        // negotiated routing (PathFinder): every round all nets are routed, sharing space at a price; where nets fought,
        // the history cost rises and sharing gets dearer; done when no belt runs inside another's clearance
        long ms = (long)Math.Min(10000 * TimeX, StopAt == DateTime.MinValue ? 10000 * TimeX : Math.Max(500, (StopAt - DateTime.UtcNow).TotalMilliseconds));
        g.Deadline = g.Clock.ElapsedTicks + System.Diagnostics.Stopwatch.Frequency * ms / 1000; // safety cutoff (10 s, or the floor's deadline)
        var routes = new Dictionary<Lane, List<List<(int x, int y, int l)>>>();
        var marks = new Dictionary<Lane, (HashSet<int> area, HashSet<int> centre)>();
        bool dbg = Environment.GetEnvironmentVariable("PNR_DEBUG") == "1";
        int lastBad = int.MaxValue, stale = 0;
        HashSet<Lane>? reroute = null; bool incremental = Environment.GetEnvironmentVariable("PNR_INCREMENTAL") == "1"; // (measured: worse)
        for (int round = 0; round < 40; round++)
        {
            foreach (var net in order)
            {
                // after the first round only the lines in a conflict are routed again; the rest keep their paths
                if (reroute != null && !reroute.Contains(net.lane) && routes.ContainsKey(net.lane)) continue;
                if (marks.TryGetValue(net.lane, out var old)) { foreach (var k in old.area) g.Area[k / N][k % N]--; marks.Remove(net.lane); }
                g.CurFluid = net.lane.Fluid;
                g.CurLocal = twoStage && !global.Contains(net.lane);
                var tl = net.terms.Select(q => (q.t.Source, TermCell(q.c, q.t))).ToList();
                // the distributor's trunk to the nearest consumer first, or to the farthest (the others then branch off it
                // on the way): try both when there are several consumers, keep the shorter tree
                List<List<(int x, int y, int l)>>? r = null; HashSet<int> centre = new();
                void Undo() { foreach (var k in g.Own) g.Area[k / N][k % N]--; }
                double Len(List<List<(int x, int y, int l)>>? t) => t == null ? double.MaxValue : t.Sum(p => p.Count + 30 * Enumerable.Range(1, Math.Max(0, p.Count - 2)).Count(i => DirOf(p[i - 1], p[i]) != DirOf(p[i], p[i + 1])));
                double best = double.MaxValue; bool bestFar = false;
                var modes = tl.Count(q => !q.Source) > 1 ? new[] { false, true } : new[] { false };
                foreach (bool far in modes)
                {
                    g.Own = new HashSet<int>(); g.Stamp++;
                    var tr = RouteNet(g, tl, netId[net.lane], NearBorder, out var cen, far);
                    double len = Len(tr);
                    if (modes.Length == 1 || (far && len >= best)) { if (modes.Length == 1 || tr == null) { r = tr; centre = cen; } if (modes.Length > 1) Undo(); break; }
                    if (!far) { best = len; Undo(); continue; }
                    r = tr; centre = cen; bestFar = true; // the far order won: keep it as committed
                }
                if (modes.Length > 1 && !bestFar)
                {
                    g.Own = new HashSet<int>(); g.Stamp++;
                    r = RouteNet(g, tl, netId[net.lane], NearBorder, out centre, false);
                }
                if (r == null)
                {
                    foreach (var k in g.Own) g.Area[k / N][k % N]--;
                    failedOut.Clear(); failedOut.Add(net.lane);
                    if (dbg) Console.WriteLine($"  round {round}: {net.lane.Id} unroutable");
                    FailWhy = $"unroutable#{NetWhy} r{round} {(net.lane.Fluid ? "pipe" : "belt")} ends {net.terms.Count}"; return null;
                }
                routes[net.lane] = r;
                marks[net.lane] = (g.Own, centre);
            }
            // conflicts: a belt centre inside another net's belt / clearance
            var bad = new HashSet<int>(); var badNets = new HashSet<Lane>();
            foreach (var (lane, (area, centre)) in marks)
                foreach (var k in centre)
                    if (g.Area[k / N][k % N] > 1) { bad.Add(k); badNets.Add(lane); }
            if (dbg) Console.WriteLine($"  round {round}: {bad.Count} contested cells, {badNets.Count} nets, {g.Clock.ElapsedMilliseconds} ms" + (bad.Count < 8 ? " " + string.Join(",", badNets.Select(b => b.Id)) + " @ " + string.Join(" ", bad.Select(k => $"{k % N % g.W},{k % N / g.W},{k / N}")) : ""));
            // a path that crosses itself (at the same height, not just a repeated point), or runs through the strip between
            // a group and one of its openings (where the group's lane comes out), can't be built: that line failed —
            // more room around its groups on the next placement
            if (bad.Count == 0)
            {
                var gap = new HashSet<int>();
                foreach (var net in order)
                    foreach (var (c, tm) in net.terms)
                    {
                        var (tx, ty, td) = TermCell(c, tm);
                        for (int i = 1; i <= 2; i++)
                        {
                            int sx = tx - DX[td] * i, sy = ty - DY[td] * i;
                            if (!g.In(sx, sy) || g.Obst[g.Idx(sx, sy)]) break;
                            gap.Add(g.Idx(sx, sy));
                        }
                    }
                foreach (var (ln, rt) in routes)
                    foreach (var pth in rt)
                    {
                        var seen = new HashSet<(int, int, int)>();
                        for (int i = 0; i < pth.Count; i++)
                        {
                            if ((i == 0 || pth[i] != pth[i - 1]) && !seen.Add(pth[i])) { badNets.Add(ln); break; }
                            if (pth[i].l == 0 && g.In(pth[i].x, pth[i].y) && gap.Contains(g.Idx(pth[i].x, pth[i].y))) { badNets.Add(ln); break; }
                        }
                    }
            }
            if (bad.Count == 0 && badNets.Count > 0)
            {
                if (dbg) Console.WriteLine($"  round {round}: self-crossing: {string.Join(", ", badNets.Select(b => b.Id))}");
                failedOut.Clear(); failedOut.AddRange(badNets);
                FailWhy = $"selfcross r{round} nets {badNets.Count}"; return null;
            }
            if (bad.Count == 0)
            {
                if (Environment.GetEnvironmentVariable("PNR_PATHS") == "1")
                    foreach (var (ln, rt) in routes) foreach (var pth in rt)
                        Console.WriteLine($"FINAL {ln.Id} {(ln.Fluid ? "pipe" : "belt")}: " + string.Join(" ", pth.Where((q, i) => i == 0 || i == pth.Count - 1 || q.l != pth[i - 1].l || DirOf(pth[i - 1], q) != DirOf(q, pth[i + 1])).Select(q => $"{q.x},{q.y},{q.l}")));
                return Emit(cells, routes, s, plan, g);
            }
            // stuck (the same few cells fought over round after round): give up now, so the placement retry gets the time
            if (bad.Count < lastBad) { lastBad = bad.Count; stale = 0; } else if (++stale >= 5) { failedOut.Clear(); failedOut.AddRange(badNets); FailWhy = $"stuck r{round} cells {bad.Count} nets {badNets.Count}"; return null; }
            foreach (var k in bad) g.Hist[k / N][k % N] += 1;
            // next round: the lines whose belt is in a conflict, and the lines whose room they run into
            if (incremental)
            {
                reroute = new HashSet<Lane>(badNets);
                foreach (var (lane, (area, _)) in marks) if (area.Overlaps(bad)) reroute.Add(lane);
            }
            g.Pres *= 1.6f;
            failedOut.Clear(); failedOut.AddRange(badNets);
            if (g.Over) { FailWhy = $"deadline r{round} cells {bad.Count} nets {badNets.Count}"; return null; }
            // curves, then local lines, then contested lines first
            order = order.OrderByDescending(CanCurve).ThenBy(n => twoStage && global.Contains(n.lane)).ThenByDescending(n => badNets.Contains(n.lane)).ToList();
        }
        FailWhy = $"rounds cells {lastBad}"; return null;
    }
    [ThreadStatic] static string? FailWhy, NetWhy;

    /// <summary>Route one net as a tree; null if some terminal can't be reached.</summary>
    static List<List<(int x, int y, int l)>>? RouteNet(Grid g, List<(bool source, (int x, int y, int dir) cell)> terms, int id, Func<double, bool> nearBorder, out HashSet<int> centre, bool far = false, Func<double, bool>? nearJoin = null)
    {
        var cen = new HashSet<int>(); centre = cen;
        static void Why(string w) { NetWhy = w; if (Environment.GetEnvironmentVariable("PNR_DEBUG") == "1") Console.WriteLine("    routenet fail #" + w); }
        int NN = g.W * g.H;
        var sources = terms.Where(t => t.source).Select(t => t.cell).ToList();
        var sinks = terms.Where(t => !t.source).Select(t => t.cell).ToList();
        var s0 = sources[0];
        // one producer, one consumer, close and facing each other: a single smooth curve (like a belt dragged in game)
        if (sources.Count == 1 && sinks.Count == 1)
        {
            var direct = Direct(g, id, s0, sinks[0], nearBorder);
            if (direct != null)
            {
                foreach (var (x, y, l) in direct)
                    for (int ax = -1; ax <= 1; ax++)
                        for (int ay = -1; ay <= 1; ay++)
                            if (g.In(x + ax, y + ay) && g.Own.Add(g.Idx(x + ax, y + ay))) { g.Area[0][g.Idx(x + ax, y + ay)]++; g.OwnStamp[0][g.Idx(x + ax, y + ay)] = g.Stamp; }
                foreach (var (x, y, l) in direct) cen.Add(g.Idx(x, y));
                g.Smooth.Add(direct);
                return [direct];
            }
        }
        sinks = sinks.OrderBy(t => Math.Abs(t.x - s0.x) + Math.Abs(t.y - s0.y)).ToList();
        // the 2 m in front of each opening (and the opening) is kept for the one path that starts or ends there: the
        // line's other paths may not run across it (they'd leave no straight run into the port)
        if (Corr)
        foreach (var (_, (tx, ty, td)) in terms)
            for (int i = 0; i <= 2; i++)
            {
                int sx = tx + DX[td] * i, sy = ty + DY[td] * i;
                if (!g.In(sx, sy)) continue;
                g.CorrStamp[g.Idx(sx, sy)] = g.Stamp; g.CorrOwner[g.Idx(sx, sy)] = g.Idx(tx, ty);
            }
        // every opening of this line (and the stub behind it) is off-limits to its other paths: only the path aimed at
        // an opening may enter it (the goal / start cells are exempt in A*)
        foreach (var (_, (tx, ty, td)) in terms)
            for (int i = 0; i <= 2 && g.CurFluid; i++) { int sx = tx - DX[td] * i, sy = ty - DY[td] * i; if (g.In(sx, sy)) g.CenStamp[0][g.Idx(sx, sy)] = g.Stamp; }
        var tree = new List<TreeCell>();
        var paths = new List<List<(int x, int y, int l)>>();
        var byCell = new Dictionary<(int, int), TreeCell>();

        void Commit(List<(int x, int y, int l)> path, bool downstream, bool fromTree)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var (x, y, l) = path[i];
                int dir = i + 1 < path.Count ? DirOf(path[i], path[i + 1]) : i > 0 ? DirOf(path[i - 1], path[i]) : 0;
                bool straight = i > 0 && i + 1 < path.Count && DirOf(path[i - 1], path[i]) == DirOf(path[i], path[i + 1]) && path[i - 1].l == l && path[i + 1].l == l;
                var tc = new TreeCell { X = x, Y = y, Level = l, Dir = dir, Downstream = downstream, Joinable = straight && i > 1 && i < path.Count - 2 && !(nearJoin ?? nearBorder)(x) && !(nearJoin ?? nearBorder)(y) }; // no junction by a tile border
                tree.Add(tc); byCell[(x, y)] = tc;
                // the belt with its clearance (counted once per net)
                for (int ax = -1; ax <= 1; ax++)
                    for (int ay = -1; ay <= 1; ay++)
                        if (g.In(x + ax, y + ay) && g.Own.Add(l * NN + g.Idx(x + ax, y + ay))) { g.Area[l][g.Idx(x + ax, y + ay)]++; g.OwnStamp[l][g.Idx(x + ax, y + ay)] = g.Stamp; }
                cen.Add(l * NN + g.Idx(x, y));
                g.CenStamp[l][g.Idx(x, y)] = g.Stamp;
            }
            // (end stubs only at real ports — not a hub or a join)
            // the stub between a path's end and the machine port it enters / leaves is this net's pipe too: nothing may cross it
            void Stub((int x, int y, int l) p, int d) { for (int i = 1; i <= 2; i++) { int sx = p.x + DX[d] * i, sy = p.y + DY[d] * i; if (g.In(sx, sy) && !g.Obst[g.Idx(sx, sy)]) g.CenStamp[p.l][g.Idx(sx, sy)] = g.Stamp; } }
            if (path.Count > 1) { if (downstream) Stub(path[^1], DirOf(path[^2], path[^1])); if (!fromTree) Stub(path[0], (DirOf(path[0], path[1]) + 2) & 3); }
            // no second junction within 4 m of a new one, and none right at the ends of a path
            if (fromTree && path.Count > 0) Unjoin(path[0].x, path[0].y);
            if (path.Count > 0) Unjoin(path[^1].x, path[^1].y);
            for (int i = 1; i < path.Count; i++) if (path[i].l != path[i - 1].l) Unjoin(path[i].x, path[i].y); // no splitter by a lift
            // nor by a bend: a T / splitter needs straight line on all its sides (else its through-line bends right away)
            // (belts too: a belt bends on a 2 m radius, so a splitter / merger 1 m from the corner sits inside the curve —
            // its straight output turns at once and no belt snaps into it; seen in game)
            if (g.CurFluid || BendJoinR > 0) for (int i = 1; i < path.Count - 1; i++) if (path[i].l == path[i - 1].l && path[i + 1].l == path[i].l && DirOf(path[i - 1], path[i]) != DirOf(path[i], path[i + 1])) Unjoin(path[i].x, path[i].y, g.CurFluid ? 3 : BendJoinR);
            paths.Add(path);
            if (Environment.GetEnvironmentVariable("PNR_PATHS") == "1") Console.WriteLine($"    commit id {id} stamp {g.Stamp} ds {downstream}: " + string.Join(" ", path.Where((q, i) => i == 0 || i == path.Count - 1 || DirOf(path[i - 1], q) != DirOf(q, path[i + 1])).Select(q => $"{q.x},{q.y},{q.l}")));
        }
        void Unjoin(int x, int y, int r = 3)
        {
            foreach (var tc in tree) if (Math.Abs(tc.X - x) <= r && Math.Abs(tc.Y - y) <= r) tc.Joinable = false; // splitters / mergers 4 m apart
        }

        // several producers: they merge into a collector that ends at a hub near their middle; the distributor starts
        // there. One producer: the distributor starts at its output.
        (int x, int y, int d) root = (s0.x, s0.y, s0.dir);
        if (sources.Count > 1)
        {
            // the hub at least 10 m from the first producer, so the collector has room for the others to merge in
            var hub = FindFree(g, (int)Math.Round(sources.Average(q => q.x)), (int)Math.Round(sources.Average(q => q.y)), nearBorder, (s0.x, s0.y, 10));
            if (hub == null) { Why("1"); return null; }
            (g.AllowA, g.AllowB) = (g.Idx(s0.x, s0.y), -1);
            var col = AStar(g, id, [(s0.x, s0.y, 0, s0.dir)], goal: (hub.Value.x, hub.Value.y, -1), treeGoal: null, nearBorder);
            if (col == null || col.Count < 2) { Why("2"); return null; }
            Commit(col, false, false);
            int arrive = DirOf(col[^2], col[^1]);
            foreach (var src in sources.Skip(1).OrderBy(q => Math.Abs(q.x - hub.Value.x) + Math.Abs(q.y - hub.Value.y)))
            {
                var box = (tree.Min(q => q.X), tree.Min(q => q.Y), tree.Max(q => q.X), tree.Max(q => q.Y));
                (g.AllowA, g.AllowB) = (g.Idx(src.x, src.y), -1);
                var p = AStar(g, id, [(src.x, src.y, 0, src.dir)], goal: null, treeGoal: tc => tc.Joinable && !tc.Downstream, nearBorder, byCell, box);
                // no spot on the collector: feed the hub itself from a side (the hub is then a merger)
                foreach (int side in new[] { (arrive + 1) & 3, (arrive + 3) & 3 })
                    if (p == null) p = AStar(g, id, [(src.x, src.y, 0, src.dir)], goal: (hub.Value.x, hub.Value.y, side), treeGoal: null, nearBorder);
                if (p == null) { Why("3"); return null; }
                Commit(p, false, false);
            }
            Unjoin(hub.Value.x, hub.Value.y);
            root = (hub.Value.x, hub.Value.y, arrive);
        }
        sinks = far ? sinks.OrderByDescending(q => Math.Abs(q.x - root.x) + Math.Abs(q.y - root.y)).ToList()
                    : sinks.OrderBy(q => Math.Abs(q.x - root.x) + Math.Abs(q.y - root.y)).ToList();
        // distributor: to the nearest consumer; the others branch off it (splitters)
        // (from a hub the belt may leave straight on or to either side: the collector then enters the merger from a side)
        List<(int x, int y, int l, int d)> rootStarts = sources.Count > 1
            ? [(root.x, root.y, 0, root.d), (root.x, root.y, 0, (root.d + 1) & 3), (root.x, root.y, 0, (root.d + 3) & 3)]
            : [(root.x, root.y, 0, root.d)];
        (g.AllowA, g.AllowB) = (sources.Count == 1 ? g.Idx(s0.x, s0.y) : -1, g.Idx(sinks[0].x, sinks[0].y));
        var trunk = AStar(g, id, rootStarts, goal: (sinks[0].x, sinks[0].y, (sinks[0].dir + 2) & 3), treeGoal: null, nearBorder);
        if (trunk == null) { Why("4"); return null; }
        Commit(trunk, true, sources.Count > 1);
        foreach (var snk in sinks.Skip(1))
        {
            var starts = tree.Where(tc => tc.Joinable && tc.Downstream).SelectMany(tc => new[] { (tc.X, tc.Y, tc.Level, (tc.Dir + 1) & 3), (tc.X, tc.Y, tc.Level, (tc.Dir + 3) & 3) }).ToList();
            if (starts.Count == 0)
            {
                // no spot 3 m clear of ends and lifts (a short run): as a last resort any straight ground cell 2 m clear
                foreach (var pth in paths)
                    for (int i = 2; i < pth.Count - 2; i++)
                    {
                        var (px, py, pl) = pth[i];
                        bool flat = true;
                        for (int k = i - 2; k <= i + 2; k++) flat &= pth[k].l == 0;
                        if (!flat || nearBorder(px) || nearBorder(py) || DirOf(pth[i - 1], pth[i]) != DirOf(pth[i], pth[i + 1]) || !byCell.TryGetValue((px, py), out var tc) || !tc.Downstream) continue;
                        starts.Add((px, py, 0, (tc.Dir + 1) & 3)); starts.Add((px, py, 0, (tc.Dir + 3) & 3));
                    }
            }
            if (starts.Count == 0) { Why($"5 (tree {tree.Count} cells, trunk {trunk.Count})"); return null; }
            (g.AllowA, g.AllowB) = (g.Idx(snk.x, snk.y), -1);
            var p = AStar(g, id, starts, goal: (snk.x, snk.y, (snk.dir + 2) & 3), treeGoal: null, nearBorder);
            if (p == null) { Why("6"); return null; }
            Commit(p, true, true);
        }
        return paths;
    }
    /// <summary>
    /// A smooth S-curve from producer end a to consumer end b when they face each other on one axis, 3–16 m apart,
    /// the sideways offset is gentle (the curve's tightest radius ≥ 2 m: run² ≥ 12 · offset) and every cell under it is
    /// free. Returns the cells under it (start and end first / last), or null.
    /// </summary>
    static List<(int x, int y, int l)>? Direct(Grid g, int id, (int x, int y, int dir) a, (int x, int y, int dir) b, Func<double, bool> nearBorder)
    {
        bool dbg = Environment.GetEnvironmentVariable("PNR_DEBUG") == "1";
        int arrive = (b.dir + 2) & 3;
        double run = (b.x - a.x) * DX[a.dir] + (b.y - a.y) * DY[a.dir];
        double side = Math.Abs((b.x - a.x) * DY[a.dir] - (b.y - a.y) * DX[a.dir]);
        if (dbg) Console.WriteLine($"      direct? {a} -> {b}: facing {a.dir == arrive}, run {run}, side {side}");
        if (a.dir != arrive) return null;
        if (run < 3 || run > 16 || (side > 0.01 && run * run < 12 * side)) return null;
        var cells = new List<(int x, int y, int l)>();
        for (int i = 0; i <= 64; i++)
        {
            double t = i / 64.0, h = 3 * t * t - 2 * t * t * t; // Hermite blend: the offset eases in and out
            double x = a.x + DX[a.dir] * run * t + (b.x - a.x - DX[a.dir] * run) * h;
            double y = a.y + DY[a.dir] * run * t + (b.y - a.y - DY[a.dir] * run) * h;
            var c = ((int)Math.Round(x), (int)Math.Round(y), 0);
            if (cells.Count > 0 && cells[^1] == c) continue;
            if (!g.Usable(c.Item1, c.Item2, id) || nearBorder(c.Item1) || nearBorder(c.Item2)) { if (dbg) Console.WriteLine($"      direct blocked at {c}"); return null; }
            // (other nets' belts from the last round may still be here: they give way in the next rounds)
            cells.Add(c);
        }
        return cells;
    }

    /// <summary>The free ground-level cell (with room for a belt around it) nearest to (x, y).</summary>
    static (int x, int y)? FindFree(Grid g, int x, int y, Func<double, bool> nearBorder, (int x, int y, int min)? awayFrom = null)
    {
        for (int r = 0; r < 60; r++)
            for (int dx = -r; dx <= r; dx++)
                foreach (int dy in r == 0 ? new[] { 0 } : new[] { r - Math.Abs(dx), -(r - Math.Abs(dx)) })
                {
                    int cx = x + dx, cy = y + dy;
                    if (nearBorder(cx) || nearBorder(cy)) continue;
                    if (awayFrom is { } af && Math.Abs(cx - af.x) + Math.Abs(cy - af.y) < af.min) continue;
                    bool ok = true;
                    for (int ax = -2; ax <= 2 && ok; ax++)
                        for (int ay = -2; ay <= 2 && ok; ay++)
                            ok = g.In(cx + ax, cy + ay) && !g.Obst[g.Idx(cx + ax, cy + ay)] && g.Res[g.Idx(cx + ax, cy + ay)] == 0 && g.Area[0][g.Idx(cx + ax, cy + ay)] == 0;
                    // (and a way out: 6 m straight clear on at least one side — else nothing can leave it)
                    if (ok && (FixMask & 4) == 0) return (cx, cy);
                    if (ok) for (int d = 0; d < 4; d++)
                    {
                        bool run = true;
                        for (int i = 3; i <= 6 && run; i++) { int px = cx + DX[d] * i, py = cy + DY[d] * i; run = g.In(px, py) && !g.Obst[g.Idx(px, py)] && g.Res[g.Idx(px, py)] == 0 && g.Area[0][g.Idx(px, py)] == 0; }
                        if (run) return (cx, cy);
                    }
                }
        return null;
    }
    static int DirOf((int x, int y, int l) a, (int x, int y, int l) b) => b.x > a.x ? 0 : b.y > a.y ? 1 : b.x < a.x ? 2 : 3;

    /// <summary>A* on (x, y, level, dir). Straight moves cost 1 (upper level 1.3), a turn 3, a ramp (3 m, changes level)
    /// 8. A cell is usable if free or already this net's; belts keep one belt-width apart. Near a tile border a belt may
    /// only cross straight at ground level.</summary>
    static List<(int x, int y, int l)>? AStar(Grid g, int id, List<(int x, int y, int l, int d)> starts, (int x, int y, int d)? goal,
        Func<TreeCell, bool>? treeGoal, Func<double, bool> nearBorder, Dictionary<(int, int), TreeCell>? tree = null, (int x0, int y0, int x1, int y1)? box = null)
    {
        g.Generation++;
        int gen = g.Generation;
        var distA = g.Dist; var prevA = g.Prev; var genA = g.Gen;
        float Dist(int k) => genA[k] == gen ? distA[k] : float.MaxValue;
        void Set(int k, float v, int p) { genA[k] = gen; distA[k] = v; prevA[k] = p; }
        int Key(int x, int y, int l, int d) => ((y * g.W + x) * NL + l) * 4 + d;
        // a metre of belt: dearer up high; in a corridor being stacked, dear on the ground and cheap up high
        bool twoStageGlobal = TwoStage && !g.CurLocal && !g.CurFluid;
        float StepCost(int x, int y, int l) => g.Stack != null && g.Stack[g.Idx(x, y)] ? (l == 0 ? 2.5f : 0.7f)
            : l == 0 ? 1f : twoStageGlobal ? 1.05f + 0.05f * l : 1.2f + 0.1f * l; // (a global line takes the stack readily)
        static int LevelOf(int k) => (k >> 2) % NL;
        int top = Math.Min(NL - 1, g.CurFluid ? (int)(4 / LevelH) : NL - 1); // pipes up to 4 m (a higher rise needs a pump)
        // other nets' belts are allowed here, at a price (Pen); this net's own belt is not (a branch touches it only
        // where it joins — running along or across it would make loops and junctions no splitter can build)
        bool Free(int x, int y, int l) => g.Usable(x, y, id) && g.CenStamp[l][g.Idx(x, y)] != g.Stamp
            && (l != 0 || g.CorrStamp[g.Idx(x, y)] != g.Stamp || g.CorrOwner[g.Idx(x, y)] == g.AllowA || g.CorrOwner[g.Idx(x, y)] == g.AllowB);
        bool BorderOk(int x, int y, int l, int d, bool turning)
        {
            bool nx = nearBorder(x), ny = nearBorder(y);
            if (!nx && !ny) return true;
            if (l != 0 || turning) return false;
            if (nx && (d == 1 || d == 3)) return false; // running along a vertical border
            if (ny && (d == 0 || d == 2)) return false;
            return true;
        }
        // a lift (≈2×2 m) needs room: none of this net's belts / splitters within 2 m, no other belt within 1 m (either
        // level), and no other lift within 3 m before it on this route
        bool LiftRoom(int x, int y, int k)
        {
            for (int ax = -2; ax <= 2; ax++)
                for (int ay = -2; ay <= 2; ay++)
                {
                    int px = x + ax, py = y + ay;
                    if (!g.In(px, py)) return false;
                    int i = g.Idx(px, py);
                    for (int ll = 0; ll < NL; ll++) if (g.CenStamp[ll][i] == g.Stamp) return false;
                    if (g.LiftSpot[i]) return false; // 2 m clear of a machine group's own lane lifts
                    if (Math.Abs(ax) <= 1 && Math.Abs(ay) <= 1)
                        for (int ll = 0; ll < NL; ll++) if (g.Area[ll][i] > (g.OwnStamp[ll][i] == g.Stamp ? 1 : 0)) return false;
                }
            int lv = LevelOf(k);
            for (int back = 0, kk = k; back < 4 && prevA[kk] >= 0 && genA[prevA[kk]] == gen; back++)
            {
                int pk = prevA[kk];
                if (LevelOf(pk) != lv) return false; // a lift just before
                kk = pk;
            }
            return true;
        }
        double H(int x, int y) => goal is { } gl ? Math.Abs(gl.x - x) + Math.Abs(gl.y - y)
            : box is { } bx ? Math.Max(0, Math.Max(bx.x0 - x, x - bx.x1)) + Math.Max(0, Math.Max(bx.y0 - y, y - bx.y1)) : 0;
        var pq = new PriorityQueue<int, double>();
        foreach (var (x, y, l, d) in starts)
        {
            int k = Key(x, y, l, d);
            Set(k, 0, -1); pq.Enqueue(k, H(x, y));
        }
        int found = -1, pops = 0;
        int budget = (FixMask & 8) != 0 ? Math.Max(300000, g.W * g.H * 6 * NL) : Math.Max(150000, g.W * g.H * 3 * NL); // search budget grows with the factory
        while (pq.Count > 0 && pops++ < budget)
        {
            if ((pops & 4095) == 0 && g.Over) return null; // out of time
            int k = pq.Dequeue();
            int d = k & 3, l = LevelOf(k), c = (k >> 2) / NL, x = c % g.W, y = c / g.W;
            float dk = Dist(k);
            if (goal is { } gl0 && x == gl0.x && y == gl0.y && (gl0.d < 0 || d == gl0.d) && l == 0 && dk > 0) { found = k; break; }
            if (treeGoal != null && tree != null && dk > 0 && tree.TryGetValue((x, y), out var tc) && tc.Level == l && treeGoal(tc) && (d & 1) != (tc.Dir & 1))
            { found = k; break; }
            // on this line's own belt without joining it here: a dead end, never pass through
            if (dk > 0 && tree != null && tree.ContainsKey((x, y))) continue;
            void Relax(int nx, int ny, int nl, int nd, float cost)
            {
                int nk = Key(nx, ny, nl, nd);
                float nd2 = dk + cost;
                if (nd2 < Dist(nk)) { Set(nk, nd2, k); pq.Enqueue(nk, nd2 + H(nx, ny)); }
            }
            // straight
            {
                int nx = x + DX[d], ny = y + DY[d];
                bool isGoalCell = (goal is { } gg && nx == gg.x && ny == gg.y) || (tree != null && tree.ContainsKey((nx, ny)));
                if (g.In(nx, ny) && (Free(nx, ny, l) || isGoalCell) && BorderOk(nx, ny, l, d, false))
                    Relax(nx, ny, l, d, StepCost(nx, ny, l) + g.Pen(nx, ny, l));
            }
            // a bend: 3 m on, the corner, 3 m in the new direction — so every curve gets the game's full 2 m radius, two
            // bends are ≥ 6 m apart and none sits right at a splitter / port. It costs like 30 m of belt.
            foreach (int nd in new[] { (d + 1) & 3, (d + 3) & 3 })
            {
                bool ok = true; float pen = 0;
                int cx = x + DX[d] * 3, cy = y + DY[d] * 3;
                for (int i = 1; i <= 3 && ok; i++)
                {
                    int ax = x + DX[d] * i, ay = y + DY[d] * i;
                    ok = Free(ax, ay, l) && BorderOk(ax, ay, l, d, i == 3);
                    if (ok) pen += g.Pen(ax, ay, l);
                }
                for (int j = 1; j <= 3 && ok; j++)
                {
                    int bx = cx + DX[nd] * j, by = cy + DY[nd] * j;
                    bool last = j == 3;
                    bool goalCell = last && ((goal is { } gq && bx == gq.x && by == gq.y) || (tree != null && tree.ContainsKey((bx, by))));
                    ok = (Free(bx, by, l) || goalCell) && BorderOk(bx, by, l, nd, false);
                    if (ok) pen += g.Pen(bx, by, l);
                }
                if (ok) Relax(cx + DX[nd] * 3, cy + DY[nd] * 3, l, nd, 36f + pen);
            }
            // a conveyor lift: straight up / down 4 m right here (it stands on both levels); its top may face any way.
            // (a pipe may use the upper level too: it's 4 m above the pipe's own ports — under the 5 m a pipe can safely
            // rise without a pump; the export checks every pipe's rise)
            // (to any other level: every level it passes must be free here)
            // (in the stacking pass also right at the opening a path starts / ends at: an output lifts straight up to its
            // corridor's level, an input comes straight down into its machine)
            if (!g.CurLocal && (dk > 0 || g.Stack != null) && !nearBorder(x) && !nearBorder(y) && LiftRoom(x, y, k))
                for (int nl = 0; nl <= top; nl++)
                {
                    if (nl == l || (g.CurFluid && nl != 0 && nl != top)) continue; // pipes: the ground or 4 m up
                    bool clear = true; float pen = 0;
                    for (int ll = Math.Min(l, nl); ll <= Math.Max(l, nl) && clear; ll++) { if (ll != l) clear = Free(x, y, ll); pen = Math.Max(pen, g.Pen(x, y, ll)); }
                    if (!clear) continue;
                    // the top faces on or to a side; back only going up (down and back would return under itself)
                    foreach (int nd in nl > l ? new[] { d, (d + 1) & 3, (d + 3) & 3, (d + 2) & 3 } : new[] { d, (d + 1) & 3, (d + 3) & 3 })
                        Relax(x, y, nl, nd, (twoStageGlobal ? 24f : 40f) + pen); // a lift is worth 40 m of belt (a bus line: 24 m)
                }
            // a ramp to the level above / below: 3 m straight rising (falling) 2 m, as onto a stack of conveyor poles.
            // It passes over / under the cells on both levels.
            foreach (int nl in new[] { l + 1, l - 1 })
            {
                if (nl < 0 || nl > top || dk <= 0 || g.CurFluid || g.CurLocal) continue; // (pipes: straight up / down only, as before)
                bool ok = true; float pen = 0;
                for (int i = 1; i <= 3 && ok; i++)
                {
                    int rx = x + DX[d] * i, ry = y + DY[d] * i;
                    ok = Free(rx, ry, l) && Free(rx, ry, nl) && !nearBorder(rx) && !nearBorder(ry);
                    if (ok) pen += Math.Max(g.Pen(rx, ry, l), g.Pen(rx, ry, nl));
                }
                if (ok) Relax(x + DX[d] * 3, y + DY[d] * 3, nl, d, 16f + pen);
            }
        }
        if (found < 0)
        {
            if (Environment.GetEnvironmentVariable("PNR_DEBUG") == "1")
                Console.WriteLine($"      A* failed: pops {pops}, budget {Math.Max(150000, g.W * g.H * 6)}, queue {pq.Count}, starts {starts.Count} first {starts[0]}, goal {goal}, {g.Clock.ElapsedMilliseconds} ms, deadline {(g.Clock.ElapsedTicks > g.Deadline ? "passed" : "ok")}, start usable {g.Usable(starts[0].x + DX[starts[0].d], starts[0].y + DY[starts[0].d], id)}");
                if (pops >= 10 && goal is { } gm && Environment.GetEnvironmentVariable("PNR_MAPGOAL") == "1")
                {
                    // a map around the unreachable goal: # machine/box, R other net's opening, o this net, B border zone, g goal
                    for (int yy = gm.y + 8; yy >= gm.y - 8; yy--)
                    {
                        var row = new System.Text.StringBuilder("        ");
                        for (int xx = gm.x - 12; xx <= gm.x + 12; xx++)
                        {
                            if (!g.In(xx, yy)) { row.Append(' '); continue; }
                            int ii = g.Idx(xx, yy);
                            row.Append(xx == gm.x && yy == gm.y ? 'g' : g.Obst[ii] ? '#' : g.Res[ii] != 0 && g.Res[ii] != id ? 'R' : g.CenStamp[0][ii] == g.Stamp ? 'o' : nearBorder(xx) || nearBorder(yy) ? 'B' : '.');
                        }
                        Console.WriteLine(row);
                    }
                    Console.WriteLine($"        goal dir {gm.d} (0 E, 1 N, 2 W, 3 S: the way the belt must be heading)");
                }
                if (pops < 10 && Environment.GetEnvironmentVariable("PNR_DEBUG") == "1")
                {
                    // a map around the stuck start: # machine/box, R other net's opening, o this net, B border zone, . free (north up)
                    var (sx0, sy0, _, _) = starts[0];
                    for (int yy = sy0 + 6; yy >= sy0 - 6; yy--)
                    {
                        var row = new System.Text.StringBuilder("        ");
                        for (int xx = sx0 - 8; xx <= sx0 + 8; xx++)
                        {
                            if (!g.In(xx, yy)) { row.Append(' '); continue; }
                            int ii = g.Idx(xx, yy);
                            row.Append(xx == sx0 && yy == sy0 ? '@' : g.Obst[ii] ? '#' : g.Res[ii] != 0 && g.Res[ii] != id ? 'R' : g.CenStamp[0][ii] == g.Stamp ? 'o' : nearBorder(xx) || nearBorder(yy) ? 'B' : '.');
                        }
                        Console.WriteLine(row);
                    }
                }
            return null;
        }
        // unwind: states from start to goal; fill the cells a ramp (3 m straight) or a bend (3 m, corner, 3 m) passes
        var ks = new List<int>();
        for (int k = found; k >= 0; k = prevA[k]) ks.Add(k);
        ks.Reverse();
        (int x, int y, int l, int d) St(int k) { int c = (k >> 2) / NL; return (c % g.W, c / g.W, LevelOf(k), k & 3); }
        var path = new List<(int x, int y, int l)>();
        var s0 = St(ks[0]); path.Add((s0.x, s0.y, s0.l));
        for (int i = 1; i < ks.Count; i++)
        {
            var a = St(ks[i - 1]); var b = St(ks[i]);
            if (a.x == b.x && a.y == b.y) { if (a.l != b.l) path.Add((b.x, b.y, b.l)); continue; } // a lift
            if (a.x != b.x && a.y != b.y)
            {
                // a bend: along a's direction to the corner, then along b's
                int cx = a.x + DX[a.d] * 3, cy = a.y + DY[a.d] * 3;
                for (int j = 1; j <= 3; j++) path.Add((a.x + DX[a.d] * j, a.y + DY[a.d] * j, b.l));
                for (int j = 1; j <= 3; j++) path.Add((cx + DX[b.d] * j, cy + DY[b.d] * j, b.l));
                continue;
            }
            int dx = Math.Sign(b.x - a.x), dy = Math.Sign(b.y - a.y), steps = Math.Abs(b.x - a.x) + Math.Abs(b.y - a.y);
            for (int j = 1; j <= steps; j++) path.Add((a.x + dx * j, a.y + dy * j, b.l)); // ramp cells take the new level (Emit blends)
        }
        return path;
    }

    // ================= output =================
    static Layout Emit(List<Cell> cells, Dictionary<Lane, List<List<(int x, int y, int l)>>> routes, Settings s, Plan plan, Grid g)
    {
        var L = new Layout { HasLevels = true, RowLength = cells.Where(c => c.Pad == null).Select(c => c.K).DefaultIfEmpty(0).Max() };
        void Belt(double x1, double y1, double z1, double x2, double y2, double z2, Lane lane)
        {
            if (Math.Abs(x1 - x2) + Math.Abs(y1 - y2) < 0.01) return;
            L.Belts.Add(new Segment(x1, y1, x2, y2, lane.Fluid, lane.Item, z1 > 0 || z2 > 0, 0, lane.Id, z1, z2));
        }
        string LineText(Lane lane) => cells.SelectMany(c => c.Ins.Select(i => i.lane).Concat(c.Outs.Select(o => o.lane))).Distinct().Count(o => o.Item == lane.Item) > 1
            ? $" #{lane.Id[(lane.Id.LastIndexOf('#') + 1)..]}" : "";

        // ---- cells: machines, manifolds, pads ----
        foreach (var c in cells)
        {
            if (c.Void != null)
            {
                var vb = Box(c, 0, 0, c.W, c.D);
                if (c.Void == "hole") L.Buildings.Add(new Placed("hole", "", c.Note ?? "", vb.x0, vb.y0, vb.x1 - vb.x0, vb.y1 - vb.y0));
                else L.Markers.Add(new Marker("floorhole", (vb.x0 + vb.x1) / 2, (vb.y0 + vb.y1) / 2, c.Note ?? ""));
                continue;
            }
            if (c.Pad != null && c.Building == "riser")
            {
                L.RiserSpots[c.CrossKey ?? c.Pad.Lane.Id] = (c.X, c.Y, c.Rot);
                // a crossing: a floor hole with a lift / pipe, and the short run out of it
                var rb = Box(c, 0, 0, c.W, c.D);
                L.Markers.Add(new Marker("floorhole", (rb.x0 + rb.x1) / 2, (rb.y0 + rb.y1) / 2, c.Note ?? ""));
                var rf = World(c, c.Terms[0].LX, c.D); var rt = World(c, c.Terms[0].LX, c.D + 2);
                if (c.Pad.Input) Belt(rf.x, rf.y, 0, rt.x, rt.y, 0, c.Pad.Lane); else Belt(rt.x, rt.y, 0, rf.x, rf.y, 0, c.Pad.Lane);
                continue;
            }
            if (c.Pad != null)
            {
                var sl = c.Pad;
                var r = Box(c, 0, 0, c.W, c.D);
                L.Buildings.Add(new Placed(sl.Input ? "input" : c.Spare ? "surplus" : "output", sl.Building,
                    $"{(sl.Input ? "IN" : "OUT")} {GameData.Item(sl.Lane.Item).Name}{LineText(sl.Lane)} · {sl.Rate:0.#}/min", r.x0, r.y0, r.x1 - r.x0, r.y1 - r.y0, sl.Lane.Item));
                var face = World(c, c.Terms[0].LX, c.D); var term = World(c, c.Terms[0].LX, c.D + 2);
                if (sl.Input) Belt(face.x, face.y, 0, term.x, term.y, 0, sl.Lane); else Belt(term.x, term.y, 0, face.x, face.y, 0, sl.Lane);
                L.Wall.Add((face.x, face.y, sl.Lane.Item, face.y, face.x));
                continue;
            }
            string label = $"{GameData.Item(c.Node.Item).Name}{(c.Node.Key.Contains('#') ? $" #{c.Node.Key[(c.Node.Key.LastIndexOf('#') + 1)..]}" : "")}" +
                           (c.Subs > 1 ? $" ({c.Sub + 1}/{c.Subs})" : "") + $" · {c.K}× {GameData.Buildings.GetValueOrDefault(c.Building)?.Name}";
            for (int m = 0; m < c.K; m++)
            {
                var r = Box(c, m * c.P, 0, m * c.P + c.W, c.D);
                L.Buildings.Add(new Placed("machine", c.Building, m == 0 ? label + "\n" + $"@@SW|{c.P}|{c.X}|{c.Y}" : "", r.x0, r.y0, r.x1 - r.x0, r.y1 - r.y0, c.Node.Item, Recipe: c.Node.Recipe?.ClassName) { Rot = c.Rot });
            }
            L.Markers.Add(new Marker("anchor", c.X, c.Y, ""));
            if (c.Direct)
            {
                for (int j = 0; j < c.Ins.Count; j++)
                {
                    var a = World(c, c.PortX(0, c.InOff[j]), -2); var b = World(c, c.PortX(0, c.InOff[j]), 0);
                    Belt(a.x, a.y, 0, b.x, b.y, 0, c.Ins[j].lane);
                }
                for (int j = 0; j < c.Outs.Count; j++)
                {
                    var a = World(c, c.PortX(0, c.OutOff[j]), c.D); var b = World(c, c.PortX(0, c.OutOff[j]), c.D + 2);
                    Belt(a.x, a.y, 0, b.x, b.y, 0, c.Outs[j].lane);
                }
            }
            // manifolds: a track per input / output, a splitter (merger) at each port, a drop to the port; drops from
            // the outer tracks rise 2 m over the inner ones
            for (int side = 0; side < 2 && !c.Direct; side++)
            {
                var list = side == 0 ? c.Ins : c.Outs;
                for (int j = 0; j < list.Count; j++)
                {
                    var lane = list[j].lane;
                    double ty = side == 0 ? c.InY(j) : c.OutY(j), faceY = side == 0 ? 0 : c.D;
                    double[] xs = Enumerable.Range(0, c.K).Select(m => c.PortX(m, side == 0 ? c.InOff[j] : c.OutOff[j])).ToArray();
                    bool east = c.East[side == 0 ? j : c.Ins.Count + j];
                    bool up = c.Up(side, j);
                    double h = up ? LiftH : 0;
                    var endT = World(c, east ? c.Right + 4 : -4, ty); var endM = World(c, east ? xs.Min() : xs.Max(), ty);
                    var liftE = World(c, east ? c.Right + 2 : -2, ty); // an upper lane's lift at its connection end
                    void Lift(double x, double y, double z1, double z2)
                    {
                        L.Belts.Add(new Segment(x, y, x, y, lane.Fluid, lane.Item, true, 0, lane.Id, z1, z2));
                        L.Markers.Add(new Marker("lift", x, y, z2 > z1 ? "↑" : "↓"));
                    }
                    // the lane from its connection end to the far port (inputs flow in, outputs flow out)
                    if (side == 0)
                    {
                        if (up) { Belt(endT.x, endT.y, 0, liftE.x, liftE.y, 0, lane); Lift(liftE.x, liftE.y, 0, h); Belt(liftE.x, liftE.y, h, endM.x, endM.y, h, lane); }
                        else Belt(endT.x, endT.y, 0, endM.x, endM.y, 0, lane);
                    }
                    else
                    {
                        if (up) { Belt(endM.x, endM.y, h, liftE.x, liftE.y, h, lane); Lift(liftE.x, liftE.y, h, 0); Belt(liftE.x, liftE.y, 0, endT.x, endT.y, 0, lane); }
                        else Belt(endM.x, endM.y, 0, endT.x, endT.y, 0, lane);
                    }
                    double sg = side == 0 ? 1 : -1; // towards the machine in local y
                    for (int m = 0; m < xs.Length; m++)
                    {
                        var pt = World(c, xs[m], ty);
                        bool far = east ? m == 0 : m == xs.Length - 1;
                        if (!far || (side == 0 && s.EndSplitter))
                            L.Markers.Add(new Marker(lane.Fluid ? "junction" : side == 0 ? "splitter" : "merger", pt.x, pt.y, ""));
                        // drop between the lane and the machine: an upper lane comes down in a lift 2 m from the machine
                        var face = World(c, xs[m], faceY);
                        if (up)
                        {
                            var lp = World(c, xs[m], faceY - sg * 1); // a lift right at the machine (1 m out, as built in game)
                            if (side == 0) { Belt(pt.x, pt.y, h, lp.x, lp.y, h, lane); Lift(lp.x, lp.y, h, 0); Belt(lp.x, lp.y, 0, face.x, face.y, 0, lane); }
                            else { Belt(face.x, face.y, 0, lp.x, lp.y, 0, lane); Lift(lp.x, lp.y, 0, h); Belt(lp.x, lp.y, h, pt.x, pt.y, h, lane); }
                        }
                        else if (side == 0) Belt(pt.x, pt.y, 0, face.x, face.y, 0, lane);
                        else Belt(face.x, face.y, 0, pt.x, pt.y, 0, lane);
                    }
                    if (side == 0) L.Markers.Add(new Marker("rate", World(c, -3, ty).x, World(c, -3, ty).y, $"{list[j].rate * c.Share:0.#}"));
                }
            }
            var guard = plan.Guards.FirstOrDefault(gd => gd.Row.Recipe == c.Node.Recipe);
            if (guard != null && c.Outs.Count > 0)
            {
                var gp = World(c, c.Right + 2, c.OutY(c.Outs.FindIndex(o => o.lane.Item == guard.Item) is var gi && gi >= 0 ? gi : 0));
                L.Markers.Add(new Marker(guard.Fluid ? "valve" : "guard", gp.x, gp.y, guard.AllAway ? "→ " + Loc.T("guard.badge") : "⛨"));
            }
        }

        // ---- routed belts: cell centres; straight runs merged; a ramp spans its 3 m ----
        foreach (var (lane, paths) in routes)
        {
            foreach (var path in paths)
            {
                if (path.Count < 2) continue;
                if (g.Smooth.Contains(path))
                {
                    // one smooth curve: a single segment the export turns into an S-shaped belt
                    L.Belts.Add(new Segment(path[0].x, path[0].y, path[^1].x, path[^1].y, lane.Fluid, lane.Item, false, 0, lane.Id) { Smooth = true });
                    continue;
                }
                // flat runs between bends and lifts; a lift is a vertical segment (same spot, 4 m apart)
                // (a ramp: the level changes between two neighbouring cells; its run is the 3 cells from its foot)
                var rampIn = new HashSet<int>();
                for (int i = 0; i + 3 < path.Count; i++)
                    if (path[i + 1].l != path[i].l && (path[i + 1].x != path[i].x || path[i + 1].y != path[i].y)) { rampIn.Add(i + 1); rampIn.Add(i + 2); }
                var brk = new List<int> { 0 };
                for (int i = 1; i < path.Count - 1; i++)
                {
                    if (rampIn.Contains(i)) continue;
                    bool lift = path[i].l != path[i - 1].l || path[i + 1].l != path[i].l;
                    bool bend = path[i + 1].x - path[i].x != path[i].x - path[i - 1].x || path[i + 1].y - path[i].y != path[i].y - path[i - 1].y;
                    if (lift || bend || rampIn.Contains(i - 1) || rampIn.Contains(i + 1)) brk.Add(i);
                }
                brk.Add(path.Count - 1);
                for (int i = 1; i < brk.Count; i++)
                {
                    var pa = path[brk[i - 1]]; var pb = path[brk[i]];
                    if (pa.x == pb.x && pa.y == pb.y && pa.l == pb.l) continue;
                    if (pa.x == pb.x && pa.y == pb.y)
                    {
                        L.Belts.Add(new Segment(pa.x, pa.y, pb.x, pb.y, lane.Fluid, lane.Item, true, 0, lane.Id, pa.l * LevelH, pb.l * LevelH));
                        L.Markers.Add(new Marker("lift", pa.x, pa.y, pb.l > pa.l ? "↑" : "↓"));
                    }
                    else Belt(pa.x, pa.y, pa.l * LevelH, pb.x, pb.y, pb.l * LevelH, lane);
                }
                // a branch leaving the tree (first point on an existing belt) is a splitter; a producer joining (last
                // point on the tree) a merger — the view draws them; the export finds them from the belt graph anyway
            }
            // junction markers: points where 3+ path ends / interiors meet
            var ends = paths.SelectMany(p => new[] { p[0], p[^1] }).GroupBy(p => (p.x, p.y)).Where(gp => gp.Count() >= 1).Select(gp => gp.Key);
            foreach (var (x, y) in ends)
            {
                int onPaths = paths.Count(p => p.Any(q => q.x == x && q.y == y));
                if (onPaths >= 2)
                {
                    bool merge = paths.Any(p => p[^1].x == x && p[^1].y == y && paths.IndexOf(p) > 0 && p[0] != paths[0][0] && !paths.Any(q => q[0].x == x && q[0].y == y));
                    L.Markers.Add(new Marker(lane.Fluid ? "junction" : merge ? "merger" : "splitter", x, y, ""));
                }
            }
            var top = paths.SelectMany(p => p).OrderByDescending(p => p.y).First();
            L.Markers.Add(new Marker("lanelabel", top.x, top.y + 1, GameData.Item(lane.Item).Name + LineText(lane)));
        }
        L.Lanes = routes.Count;
        L.Rows = cells.Count(c => c.Pad == null && c.Void == null);
        L.Width = SnapTo(Math.Max(L.Buildings.Select(b => b.X + b.W).DefaultIfEmpty(0).Max(), L.Belts.Select(b => Math.Max(b.X1, b.X2)).DefaultIfEmpty(0).Max()) + 2, Foundation);
        L.Height = SnapTo(Math.Max(L.Buildings.Select(b => b.Y + b.H).DefaultIfEmpty(0).Max(), L.Belts.Select(b => Math.Max(b.Y1, b.Y2)).DefaultIfEmpty(0).Max()) + 2, Foundation);
        for (int i = 0; i < L.Buildings.Count; i++)
        {
            var b = L.Buildings[i];
            int at = b.Label.IndexOf("@@", StringComparison.Ordinal);
            if (at < 0) continue;
            var parts = b.Label[(at + 2)..].Split('|');
            double ax = double.Parse(parts[2], CultureInfo.InvariantCulture), ay = double.Parse(parts[3], CultureInfo.InvariantCulture);
            L.Buildings[i] = b with { Label = b.Label[..at] + Loc.T("layout.anchor2", parts[0], ax / Foundation, ay / Foundation, double.Parse(parts[1], CultureInfo.InvariantCulture)) };
        }
        return L;
    }
}
