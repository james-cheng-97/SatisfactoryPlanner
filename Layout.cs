using System.Globalization;
using System.Text;

namespace SatisfactoryPlanner;

/// <summary>A placed building / part in the layout (metres; x east, y north; the station wall is at y = 0).</summary>
public record Placed(string Kind, string Building, string Label, double X, double Y, double W, double H, string? Item = null, int Floor = 0, string? Recipe = null)
{
    /// <summary>Machine turned 180° (inputs north, outputs south).</summary>
    public bool Flipped { get; init; }
    /// <summary>Quarter turns counter-clockwise from the standard orientation (inputs south) — place &amp; route layouts.</summary>
    public int Rot { get; init; }
    /// <summary>Crosses a blueprint tile border: left out of the blueprints, placed by hand.</summary>
    public bool ByHand { get; init; }
}

/// <summary>A straight belt or pipe segment. Elevated = lifted over other belts (drawn dashed, lift at each end).</summary>
public record Segment(double X1, double Y1, double X2, double Y2, bool Fluid, string Item, bool Elevated = false, int Floor = 0, string? Line = null, double Z1 = 0, double Z2 = 0)
{
    /// <summary>Not a straight run: one smooth S-curve between its ends (tangents along the main axis).</summary>
    public bool Smooth { get; init; }
    public double Length => Math.Abs(X2 - X1) + Math.Abs(Y2 - Y1);
}

public record Marker(string Kind, double X, double Y, string Text, int Floor = 0);

/// <summary>
/// Single-floor layout built from the plan's flow graph.
///  • Rows ("bands") hold machine groups side by side; each row has input belts south of its machines and output
///    belts north of them. Belt strips are shared wherever their belts don't overlap.
///  • Every item line has one vertical trunk placed where its row belts come out shortest — no buses and no
///    left/right rule: producers feed the trunk, consumers branch off it. Trunks pass between machines or are
///    lifted over them, never through a port column.
///  • Station ("pins"): input and output boxes sit together at the free end of one row, which becomes the south
///    border; the rows are then reordered so the trunks are short (raw and final-product rows end up near the
///    boxes). Spare boxes form their own group in any row's free end. Boxes connect like machines.
///  • Every row has one corner anchor on a foundation corner; the other machines follow at a fixed spacing.
///  • Ports are at the real in-game positions (measured from a save), so multi-input machines are fed correctly.
/// Building sizes from satisfactory-calculator.com; belt 2 m, splitter/merger 4×4 m, valve 2×2 m (wiki).
/// </summary>
public partial class Layout
{
    public List<Placed> Buildings = new();
    public List<Segment> Belts = new();
    public List<Marker> Markers = new();
    /// <summary>Suggested power wiring for machines placed by hand: from the machine to the middle of the tile whose wall
    /// outlets keep a slot for it (dotted in the view).</summary>
    public List<(double x1, double y1, double x2, double y2, int floor)> PowerHints = new();
    /// <summary>Stretches of belt / pipe built by hand: a bend right by a blueprint tile border (machines may cross tiles).</summary>
    public List<(double x1, double y1, double x2, double y2, int floor, bool fluid)> HandRuns = new();
    /// <summary>Congested corridors: several belts running side by side (the area they cover, lines in it, and whether
    /// the stacking pass moved them onto stacked levels).</summary>
    public List<Corridor> Corridors = new();
    public record Corridor(double X0, double Y0, double X1, double Y1, int Floor, int Lines, bool Stacked);

    /// <summary>Progress for the UI while a layout is built (the step it's on); set per build thread.</summary>
    [ThreadStatic] public static Action<string>? Status;
    internal static void Say(string key, params object[] args) => Status?.Invoke(Loc.T(key, args));

    /// <summary>
    /// Congested corridors in a routed layout: ground-level belts of different lines running parallel within 4.5 m of
    /// each other, side by side for at least 8 m, grouped into bundles (pipes aren't worth stacking).
    /// </summary>
    public static List<Corridor> FindCorridors(Layout L)
    {
        var segs = L.Belts.Where(q => !q.Fluid && !q.Smooth && Math.Abs(q.Z1) < 0.1 && Math.Abs(q.Z2) < 0.1
                                      && (Math.Abs(q.X1 - q.X2) < 0.01 || Math.Abs(q.Y1 - q.Y2) < 0.01) && Math.Abs(q.X1 - q.X2) + Math.Abs(q.Y1 - q.Y2) >= 4).ToList();
        int n = segs.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var span = new Dictionary<int, (double a0, double a1)>(); // overlap along the axis, per pair root later
        var pairs = new List<(int i, int j, double lo, double hi)>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var a = segs[i]; var b = segs[j];
                if (a.Floor != b.Floor || a.Line == b.Line) continue;
                bool ah = Math.Abs(a.Y1 - a.Y2) < 0.01, bh = Math.Abs(b.Y1 - b.Y2) < 0.01;
                if (ah != bh) continue;
                double lat = ah ? Math.Abs(a.Y1 - b.Y1) : Math.Abs(a.X1 - b.X1);
                if (lat < 0.5 || lat > 4.5) continue;
                double a0 = ah ? Math.Min(a.X1, a.X2) : Math.Min(a.Y1, a.Y2), a1 = ah ? Math.Max(a.X1, a.X2) : Math.Max(a.Y1, a.Y2);
                double b0 = ah ? Math.Min(b.X1, b.X2) : Math.Min(b.Y1, b.Y2), b1 = ah ? Math.Max(b.X1, b.X2) : Math.Max(b.Y1, b.Y2);
                double lo = Math.Max(a0, b0), hi = Math.Min(a1, b1);
                if (hi - lo < 8) continue;
                pairs.Add((i, j, lo, hi));
                parent[Find(i)] = Find(j);
            }
        var result = new List<Corridor>();
        foreach (var grp in pairs.GroupBy(p => Find(p.i)))
        {
            var members = grp.SelectMany(p => new[] { p.i, p.j }).Distinct().Select(i => segs[i]).ToList();
            bool h = Math.Abs(members[0].Y1 - members[0].Y2) < 0.01;
            double lo = grp.Min(p => p.lo), hi = grp.Max(p => p.hi);
            double c0 = members.Min(q => h ? q.Y1 : q.X1), c1 = members.Max(q => h ? q.Y1 : q.X1);
            int lines = members.Select(q => q.Line).Distinct().Count();
            result.Add(h ? new Corridor(lo, c0, hi, c1, members[0].Floor, lines, false) : new Corridor(c0, lo, c1, hi, members[0].Floor, lines, false));
        }
        return result;
    }

    /// <summary>
    /// The blueprint tile a machine placed by hand takes its power from: of the tiles it touches, the one it covers most.
    /// </summary>
    public static (int tx, int ty) PowerTile(Placed b, double tile)
    {
        (int, int) best = (0, 0); double bestA = -1;
        for (int tx = (int)Math.Floor(b.X / tile); tx <= (int)Math.Floor((b.X + b.W - 0.01) / tile); tx++)
            for (int ty = (int)Math.Floor(b.Y / tile); ty <= (int)Math.Floor((b.Y + b.H - 0.01) / tile); ty++)
            {
                double a = Math.Max(0, Math.Min(b.X + b.W, (tx + 1) * tile) - Math.Max(b.X, tx * tile)) * Math.Max(0, Math.Min(b.Y + b.H, (ty + 1) * tile) - Math.Max(b.Y, ty * tile));
                if (a > bestA) { bestA = a; best = (tx, ty); }
            }
        return best;
    }
    public static bool CrossesTile(Placed b, double tile) =>
        tile > 0 && (Math.Floor((b.X + 0.01) / tile) != Math.Floor((b.X + b.W - 0.01) / tile) || Math.Floor((b.Y + 0.01) / tile) != Math.Floor((b.Y + b.H - 0.01) / tile));
    public double Width, Height;
    public int Lanes, Rows, RowLength;
    public double BeltLength => Belts.Sum(b => b.Length) + LiftLength;
    /// <summary>Floors used, the elevation of each floor (m) and the vertical belt length in lifts between floors.</summary>
    public int Floors = 1;
    public List<double> FloorElevation = [0];
    /// <summary>Belt heights come from the segments (Z1/Z2, metres above port height) — place &amp; route layouts.</summary>
    public bool HasLevels;
    /// <summary>Place this layout this many times (a module for 1/Copies of the targets).</summary>
    public int Copies = 1;
    public double LiftLength;
    /// <summary>Multi-floor: vertical runs between floors (line id, where, lowest and highest floor, pipe?).</summary>
    public List<(string line, double x, double y, int lo, int hi, bool fluid)> Crossings = new();
    /// <summary>Multi-floor planner: where each crossing (lane id) sits on this floor (x, y, quarter turns).</summary>
    internal Dictionary<string, (double x, double y, int rot)> RiserSpots = new();

    public const double Foundation = 8, LanePitch = 4, TrackPitch = 4;
    /// <summary>In-game sizes (m): belt width 2, splitter/merger 4×4, valve 2×2 (wiki); pipe ≈1, junction ≈2.</summary>
    public const double BeltWidth = 2, PipeWidth = 1, SplitterSize = 4, ValveSize = 2;
    /// <summary>Station wall connections (outlet x/y, item, jog y, lane x) — used to check nothing is left unconnected.</summary>
    public List<(double x, double y, string item, double jogY, double laneX)> Wall = new();

    /// <summary>(width along the row, length north–south) in metres, from satisfactory-calculator.com.</summary>
    public static (double w, double d) Footprint(string building) => building switch
    {
        "Desc_SmelterMk1_C" => (6, 9),
        "Desc_ConstructorMk1_C" => (8, 10),
        "Desc_AssemblerMk1_C" => (10, 15),
        "Desc_FoundryMk1_C" => (8, 9),
        "Desc_ManufacturerMk1_C" => (20, 22),
        "Desc_OilRefinery_C" => (10, 20),
        "Desc_Packager_C" => (8, 8),
        "Desc_Blender_C" => (19, 16),
        "Desc_HadronCollider_C" => (38, 24),
        "Desc_Converter_C" => (16, 16),
        "Desc_QuantumEncoder_C" => (22, 48),
        "Desc_StorageContainerMk2_C" => (5, 10), // Industrial Storage Container
        "Desc_PipeStorageTank_C" => (4, 4),      // Fluid Buffer
        "Desc_IndustrialTank_C" => (12, 12),     // Industrial Fluid Buffer (its connections 6 m out either side, measured in a save)
        _ => (10, 10),
    };

    /// <summary>Building height in metres (satisfactory.wiki.gg infoboxes).</summary>
    public static double HeightOf(string building) => building switch
    {
        "Desc_SmelterMk1_C" => 8.5,
        "Desc_ConstructorMk1_C" => 8,
        "Desc_AssemblerMk1_C" => 11,
        "Desc_FoundryMk1_C" => 9,
        "Desc_ManufacturerMk1_C" => 12,
        "Desc_OilRefinery_C" => 30,
        "Desc_Packager_C" => 12,
        "Desc_Blender_C" => 15,
        "Desc_HadronCollider_C" => 32,
        "Desc_Converter_C" => 18,
        "Desc_QuantumEncoder_C" => 18,
        "Desc_StorageContainerMk2_C" => 8,
        "Desc_PipeStorageTank_C" => 8,
        "Desc_IndustrialTank_C" => 12, // (not verified in game)
        _ => 12,
    };
    /// <summary>Belts may be lifted over machines up to this height; taller ones must be routed around.</summary>
    public const double MaxLiftOver = 12;

    /// <summary>
    /// Port x offsets from the machine's centre, as placed here (inputs facing south). Measured from belt/pipe ends in a
    /// real save (Constructor, Assembler, Foundry, Refinery, Packager exact; Manufacturer partly inferred at 4 m spacing);
    /// the Refinery is turned 180° so its inputs face the manifold. Others: evenly spaced estimate.
    /// </summary>
    static (double[] itemIn, double[] pipeIn, double[] itemOut, double[] pipeOut) Ports(string building, double width, int items, int pipes, int itemOuts, int pipeOuts)
    {
        double[] Even(int n) => Enumerable.Range(0, n).Select(i => width * (i + 1) / (n + 1) - width / 2).ToArray();
        return building switch
        {
            "Desc_ConstructorMk1_C" or "Desc_SmelterMk1_C" => ([0], [], [0], []),
            "Desc_AssemblerMk1_C" => ([2, -2], [], [0], []),
            "Desc_FoundryMk1_C" => ([2, -2], [], [2], []),
            "Desc_ManufacturerMk1_C" => ([6, 2, -2, -6], [], [0], []),
            "Desc_OilRefinery_C" => ([2], [-2], [2], [-2]),     // item ports east, pipe ports west (as built in game, not turned)
            "Desc_Packager_C" => ([0], [0], [0], [0]),
            _ => (Even(items + pipes).Take(items).ToArray(), Even(items + pipes).Skip(items).ToArray(),
                  Even(itemOuts + pipeOuts).Take(itemOuts).ToArray(), Even(itemOuts + pipeOuts).Skip(itemOuts).ToArray()),
        };
    }

    static double SnapTo(double v, double grid) => Math.Ceiling(v / grid - 1e-9) * grid;

    class Lane
    {
        public required string Id;
        public required string Item;
        public bool Fluid;
        public double X;
        public double MinY = double.MaxValue, MaxY = double.MinValue;
        public int Order;
        public void Cover(double y) { MinY = Math.Min(MinY, y); MaxY = Math.Max(MaxY, y); }
    }

    /// <summary>Blueprints a layout takes: the tiles with something in them when machines may cross tiles, else its
    /// bounding box in tiles.</summary>
    internal static double BlueprintCount(Layout x, Settings s)
    {
        if (s.BlueprintTile <= 0) return 0;
        double tl = s.BlueprintTile * Foundation;
        if (!s.HandPlaceAcrossTiles) return Math.Ceiling(x.Width / tl - 1e-9) * Math.Ceiling(x.Height / tl - 1e-9);
        var occ = new HashSet<(int, int)>();
        foreach (var b in x.Buildings)
            for (int tx = (int)Math.Floor(b.X / tl); tx <= (int)Math.Floor((b.X + b.W - 0.01) / tl); tx++)
                for (int ty = (int)Math.Floor(b.Y / tl); ty <= (int)Math.Floor((b.Y + b.H - 0.01) / tl); ty++) occ.Add((tx, ty));
        foreach (var q in x.Belts) occ.Add(((int)Math.Floor(q.X1 / tl), (int)Math.Floor(q.Y1 / tl)));
        return occ.Count;
    }

    /// <summary>
    /// A byproduct that is also one of the products is just extra output: it goes into that product's output box / tank
    /// (edges to the surplus node go to the target node instead; the surplus node goes).
    /// </summary>
    static Plan SurplusIntoOutputs(Plan plan)
    {
        var targets = plan.Nodes.Where(n => n.Kind == NodeKind.Target).GroupBy(n => n.Item).ToDictionary(g => g.Key, g => g.First());
        var merge = plan.Nodes.Where(n => n.Kind == NodeKind.Surplus && targets.ContainsKey(n.Item)).ToDictionary(n => n.Key, n => targets[n.Item].Key);
        if (merge.Count == 0) return plan;
        var edges = plan.Edges.Select(e => merge.TryGetValue(e.To, out var t) ? e with { To = t } : e).ToList();
        return plan.With(plan.Nodes.Where(n => !merge.ContainsKey(n.Key)).ToList(), edges);
    }

    /// <summary>Try several row lengths and row widths; keep the most square factory (ties: shortest belts).</summary>
    public static Layout Build(Plan plan, Settings s)
    {
        var L = BuildLayout(SurplusIntoOutputs(plan), s);
        // machines across a blueprint tile border, when allowed: marked for placing by hand, with a power hint
        if (s.BlueprintTile > 0 && s.HandPlaceAcrossTiles)
        {
            double tile = s.BlueprintTile * Foundation;
            for (int i = 0; i < L.Buildings.Count; i++)
            {
                var b = L.Buildings[i];
                if (b.Kind != "machine" || !CrossesTile(b, tile)) continue;
                L.Buildings[i] = b with { ByHand = true };
                var (tx, ty) = PowerTile(b, tile);
                L.PowerHints.Add((b.X + b.W / 2, b.Y + b.H / 2, (tx + 0.5) * tile, (ty + 0.5) * tile, b.Floor));
            }
            // bends within 3 m of a tile border that one of their legs crosses: the export leaves the last 3 m on each
            // side out (built by hand) — the same stretches, for the view
            bool Crosses(Segment q) => Math.Floor(Math.Min(q.X1, q.X2) / tile + 1e-9) != Math.Floor(Math.Max(q.X1, q.X2) / tile - 1e-9)
                                    || Math.Floor(Math.Min(q.Y1, q.Y2) / tile + 1e-9) != Math.Floor(Math.Max(q.Y1, q.Y2) / tile - 1e-9);
            double ToBorder(double v) => Math.Abs(v - Math.Round(v / tile) * tile);
            var flat = L.Belts.Where(q => Math.Abs(q.X1 - q.X2) + Math.Abs(q.Y1 - q.Y2) > 0.5 && !q.Smooth).ToList();
            foreach (var a in flat)
                foreach (var b2 in flat)
                {
                    if (a == b2 || a.Floor != b2.Floor || a.Line != b2.Line || Math.Abs(a.Z2 - b2.Z1) > 0.1) continue;
                    if (Math.Abs(a.X2 - b2.X1) > 0.01 || Math.Abs(a.Y2 - b2.Y1) > 0.01) continue; // a ends where b starts
                    bool aH = Math.Abs(a.Y1 - a.Y2) < 0.01, bH = Math.Abs(b2.Y1 - b2.Y2) < 0.01;
                    if (aH == bH) continue; // not a bend
                    double cx = a.X2, cy = a.Y2;
                    if (!(Crosses(a) || Crosses(b2)) || Math.Min(ToBorder(cx), ToBorder(cy)) >= 3) continue;
                    (double, double) Back(Segment q, bool fromEnd, double d)
                    {
                        double sx = fromEnd ? q.X1 : q.X2, sy = fromEnd ? q.Y1 : q.Y2, len = Math.Abs(sx - cx) + Math.Abs(sy - cy);
                        double k = Math.Min(d, len) / Math.Max(len, 1e-9);
                        return (cx + (sx - cx) * k, cy + (sy - cy) * k);
                    }
                    var (ax, ay) = Back(a, true, 3); var (bx, by) = Back(b2, false, 3);
                    L.HandRuns.Add((ax, ay, cx, cy, a.Floor, a.Fluid));
                    L.HandRuns.Add((cx, cy, bx, by, a.Floor, a.Fluid));
                }
        }
        return L;
    }

    static Layout BuildLayout(Plan plan, Settings s)
    {
        if (s.PlaceAndRoute)
        {
            // a flow too big for one belt: design a module for 1/N of the targets and place it N times, rather than
            // routing one huge factory (N = the most parallel belt lines any item needs)
            int copies = plan.Edges.Where(e => e.From.Contains('#'))
                .GroupBy(e => e.Item).Select(gp => gp.Select(e => e.From[(e.From.LastIndexOf('#') + 1)..]).Distinct().Count())
                .DefaultIfEmpty(1).Max();
            var ms = s; var mplan = plan;
            if (copies > 1)
            {
                ms = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(s))!;
                foreach (var t in ms.Targets) t.Rate /= copies;
                mplan = Plan.Build(ms);
            }
            if (s.Floors > 1)
            {
                RateDiv = 60; RawMul = 2; OneFloor = false;
                var mfClock = System.Diagnostics.Stopwatch.StartNew();
                var mf = BuildPnrFloors(mplan, ms);
                // step-aligned floors too (bigger plans), when the usual split left time: keep fewer blueprints, then less
                // belt (it helps some plans a lot and fails others — measured)
                if (mf != null && Environment.GetEnvironmentVariable("PNR_STEPS") == "1" && ms.BlueprintTile > 0 && mfClock.ElapsedMilliseconds < 60000 * TimeScale / 2
                    && mplan.Nodes.Where(n => n.Kind == NodeKind.Machine).Sum(n => Math.Max(1, n.Machines)) >= 20)
                {
                    Say("status.steps");
                    StepFloors = true; RateDiv = 60; RawMul = 2;
                    try
                    {
                        var st = BuildPnrFloors(mplan, ms);
                        if (st != null && (BlueprintCount(st, ms) < BlueprintCount(mf, ms)
                            || (BlueprintCount(st, ms) == BlueprintCount(mf, ms) && st.BeltLength < mf.BeltLength))) mf = st;
                    }
                    finally { StepFloors = false; }
                }
                if (mf != null) { mf.Copies = copies; return mf; }
                if (!OneFloor) goto rows; // floors the planner couldn't split: the rows layout (it handles floors)
            }
            // two weightings of busy lines — strong and soft — keep the better: fewer tiles, then less belt
            Layout? pnr = null;
            var pnrClock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (div, raw) in new[] { (60.0, 2.0), (120.0, 1.5) })
            {
                if (pnrClock.ElapsedMilliseconds > 30000 * TimeScale) break; // (≤ 60 s in all) a hard plan: no second try (the rows layout is the fallback)
                RateDiv = div; RawMul = raw;
                var l = BuildPnr(mplan, ms);
                if (l == null) continue;
                double TileCount(Layout x) => BlueprintCount(x, s);
                if (pnr == null || TileCount(l) < TileCount(pnr) || (TileCount(l) == TileCount(pnr) && l.BeltLength < pnr.BeltLength)) pnr = l;
            }
            // a big empty block inside (long groups of big machines leave holes): try those groups in twos
            if (pnr != null && Environment.GetEnvironmentVariable("PNR_HOLE") == "1") Console.WriteLine($"HOLE {LargestHole(pnr):0.00}");
            if (pnr != null && LargestHole(pnr) > 0.12 && pnrClock.ElapsedMilliseconds < 20000 * TimeScale)
            {
                BigPer = 2;
                try
                {
                    foreach (var (div, raw) in new[] { (60.0, 2.0), (120.0, 1.5) })
                    {
                        if (pnrClock.ElapsedMilliseconds > 45000 * TimeScale) break;
                        RateDiv = div; RawMul = raw;
                        var l = BuildPnr(mplan, ms);
                        if (Environment.GetEnvironmentVariable("PNR_HOLE") == "1") Console.WriteLine(l == null ? "PAIRS failed" : $"PAIRS {l.Width / Foundation:0}x{l.Height / Foundation:0} belt {l.BeltLength:0} hole {LargestHole(l):0.00}");
                        if (l == null) continue;
                        double TileCount(Layout x) => s.BlueprintTile > 0 ? BlueprintCount(x, s) : x.Width * x.Height;
                        if (TileCount(l) < TileCount(pnr) || (TileCount(l) == TileCount(pnr) && l.BeltLength < pnr.BeltLength)) pnr = l;
                    }
                }
                finally { BigPer = 0; }
            }
            // the narrow lane spacing too (smaller groups): with time left, keep whichever takes fewer blueprints (then
            // less belt)
            if (s.BlueprintTile > 0 && pnrClock.ElapsedMilliseconds < 20000 * TimeScale)
            {
                Say("status.narrow");
                NarrowLanes = true;
                try
                {
                    foreach (var (div, raw) in new[] { (60.0, 2.0), (120.0, 1.5) })
                    {
                        if (pnrClock.ElapsedMilliseconds > 35000 * TimeScale) break;
                        RateDiv = div; RawMul = raw;
                        var ln = BuildPnr(mplan, ms);
                        if (ln != null && (pnr == null || BlueprintCount(ln, s) < BlueprintCount(pnr, s)
                            || (BlueprintCount(ln, s) == BlueprintCount(pnr, s) && ln.BeltLength < pnr.BeltLength))) pnr = ln;
                    }
                }
                finally { NarrowLanes = false; }
            }
            // without clusters too (a cluster's shape can cost a blueprint): keep fewer blueprints, then less belt
            if (s.BlueprintTile > 0 && pnrClock.ElapsedMilliseconds < 25000 * TimeScale)
            {
                NoClusters = true;
                try
                {
                    foreach (var (narrow, div, raw) in new[] { (false, 60.0, 2.0), (false, 120.0, 1.5), (true, 60.0, 2.0), (true, 120.0, 1.5) })
                    {
                        if (pnrClock.ElapsedMilliseconds > 40000 * TimeScale) break;
                        NarrowLanes = narrow; RateDiv = div; RawMul = raw;
                        var lc = BuildPnr(mplan, ms);
                        if (lc != null && (pnr == null || BlueprintCount(lc, s) < BlueprintCount(pnr, s)
                            || (BlueprintCount(lc, s) == BlueprintCount(pnr, s) && lc.BeltLength < pnr.BeltLength))) pnr = lc;
                    }
                }
                finally { NoClusters = false; NarrowLanes = false; }
            }
            // machines may cross tiles: that's a freedom, not a must — with time left, also plan with every machine
            // inside a tile and keep whichever needs fewer blueprints
            if (s.HandPlaceAcrossTiles && s.BlueprintTile > 0 && pnrClock.ElapsedMilliseconds < 20000 * TimeScale)
            {
                var sIn = ms.Clone(); sIn.HandPlaceAcrossTiles = false;
                try
                {
                    foreach (var (nc, div, raw) in new[] { (false, 60.0, 2.0), (false, 120.0, 1.5), (true, 60.0, 2.0), (true, 120.0, 1.5) })
                    {
                        if (pnrClock.ElapsedMilliseconds > 40000 * TimeScale) break;
                        NoClusters = nc; RateDiv = div; RawMul = raw;
                        var lIn = BuildPnr(mplan, sIn);
                        if (lIn != null && (pnr == null || BlueprintCount(lIn, sIn) < BlueprintCount(pnr, s))) pnr = lIn;
                    }
                }
                finally { NoClusters = false; }
            }
            // one-stage routing failed: two-stage (local lines flat first, then global lines stacked) as a last try
            if (pnr == null && pnrClock.ElapsedMilliseconds < 45000 * TimeScale)
            {
                Say("status.twostage");
                RateDiv = 60; RawMul = 2; TwoStage = true;
                try { pnr = BuildPnr(mplan, ms); } finally { TwoStage = false; }
            }
            if (pnr != null) { pnr.Copies = copies; return pnr; }
        }
        rows:
        Say("status.rows");
        Layout? best = null;
        double bestScore = double.MaxValue;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // middle settings first, so a big plan that runs out of time still gets a sensible layout
        foreach (var perRow in new[] { 8, 12, 6, 16, 4, 10 })
            foreach (var bandWidth in new[] { 240.0, 160, 320, 480, 96 })
            {
                if (best != null && clock.ElapsedMilliseconds > 2500) break;
                var l = BuildWith(plan, s, perRow, bandWidth);
                double score = Math.Max(l.Width, l.Height) + 0.05 * l.BeltLength; // 1 km of belt ≈ 50 m more factory side
                if (s.BlueprintTile > 0)
                {
                    // blueprints: every tile is one more blueprint to place and join — fewest tiles first
                    double tile = s.BlueprintTile * Foundation;
                    score += 1000 * Math.Ceiling(l.Width / tile - 1e-9) * Math.Ceiling(l.Height / tile - 1e-9);
                }
                if (score < bestScore - 1e-6) { best = l; bestScore = score; }
            }
        return best ?? new Layout();
    }

    /// <summary>One block = one row segment of identical machines (a machine group, or part of a big one).</summary>
    class Block
    {
        public required GraphNode Node;
        public required string Building;
        public double W, D, Pitch;
        public int Count, Sub, Subs, Total = 1;
        public double Share;
        public List<(Lane lane, double rate)> Ins = new(), Outs = new();
        public double[] InOff = [], OutOff = [];
        public double X0; // west edge of the first (west-most) machine
        public double[]? Shift;     // extra offset per machine (pushed past blueprint tile borders)
        public double Width => (Count - 1) * Pitch + W + (Shift?[^1] ?? 0);
        public double Mx(int m) => X0 + m * Pitch + (Shift?[m] ?? 0);   // west edge of machine m
        public double Cx(int m) => Mx(m) + W / 2;
        public int Band;
        public List<Slot>? Slots;   // a station group: one box per slot instead of identical machines
        public double ExtraGap;     // extra space before this block in its row
    }

    /// <summary>One station box: supplies (Input) or receives one item line.</summary>
    record Slot(GraphNode Node, Lane Lane, double Rate, bool Input, string Building, double W, double D, double Off);

    /// <summary>A block's connection to one item line: its ports along the row's input (IsIn) or output belts.</summary>
    class Conn(Block b, Lane lane, bool isIn, double[] xs, double rate, int index, Slot? slot)
    {
        public Block B = b; public Lane Lane = lane; public bool IsIn = isIn; public double[] Xs = xs;
        public double Rate = rate; public int Index = index; public Slot? Slot = slot;
    }

    class Band
    {
        public List<Block> Blocks = new();
        public double Width;
        public int Floor;
        public bool Flipped; // turned 180°: inputs on the north channel, outputs on the south one
        public double Y, MachineY, OutBase, Top;
        public List<double> InTracks = new(), OutTracks = new();
        public List<List<(double a, double b)>> InStrips = new(), OutStrips = new();
    }

    /// <summary>Lowest strip whose belts don't overlap [a, b] along the row (strips are reused where they're free).</summary>
    static int Strip(List<List<(double a, double b)>> strips, double a, double b)
    {
        if (a > b) (a, b) = (b, a);
        for (int i = 0; i < strips.Count; i++)
            if (strips[i].All(iv => b + 6 < iv.a || a - 6 > iv.b)) { strips[i].Add((a, b)); return i; }
        strips.Add([(a, b)]);
        return strips.Count - 1;
    }

    static Layout BuildWith(Plan plan, Settings s, int maxPerRow, double maxBandWidth)
    {
        var L = new Layout { RowLength = maxPerRow };
        var nodes = plan.Nodes.GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.First());
        if (nodes.Count == 0) return L;

        static int LineOf(string key) => key.Contains('#') && int.TryParse(key[(key.LastIndexOf('#') + 1)..], out var k) ? k : 1;
        string LaneId(string producerKey, string item) => $"{item}#{LineOf(producerKey)}";

        // ---- lanes (one per item per belt line) and who uses them ----
        var lanes = new Dictionary<string, Lane>();
        Lane LaneFor(string producerKey, string item)
        {
            var id = LaneId(producerKey, item);
            if (!lanes.TryGetValue(id, out var l)) lanes[id] = l = new Lane { Id = id, Item = item, Fluid = GameData.Item(item).IsFluid };
            return l;
        }
        var rowIn = new Dictionary<string, List<(Lane lane, double rate)>>();
        var rowOut = new Dictionary<string, List<(Lane lane, double rate)>>();
        var wallIn = new List<(GraphNode node, Lane lane, double rate)>();
        var wallOut = new List<(GraphNode node, Lane lane, double rate)>();
        var laneProducers = new Dictionary<Lane, HashSet<string>>();
        var laneConsumers = new Dictionary<Lane, HashSet<string>>();
        foreach (var e in plan.Edges)
        {
            if (!nodes.TryGetValue(e.From, out var from) || !nodes.TryGetValue(e.To, out var to)) continue;
            var lane = LaneFor(e.From, e.Item);
            (laneProducers.TryGetValue(lane, out var ps) ? ps : laneProducers[lane] = new()).Add(e.From);
            (laneConsumers.TryGetValue(lane, out var cs) ? cs : laneConsumers[lane] = new()).Add(e.To);
            if (from.Kind == NodeKind.Machine) Add(rowOut, e.From, lane, e.Rate);
            else if (!wallIn.Any(w => w.node == from && w.lane == lane)) wallIn.Add((from, lane, from.Rate));
            if (to.Kind == NodeKind.Machine) Add(rowIn, e.To, lane, e.Rate);
            else
            {
                var i = wallOut.FindIndex(w => w.node == to && w.lane == lane);
                if (i < 0) wallOut.Add((to, lane, e.Rate)); else wallOut[i] = (to, lane, wallOut[i].rate + e.Rate);
            }
        }
        static void Add(Dictionary<string, List<(Lane, double)>> d, string key, Lane lane, double rate)
        {
            if (!d.TryGetValue(key, out var l)) d[key] = l = new();
            var i = l.FindIndex(x => x.Item1 == lane);
            if (i < 0) l.Add((lane, rate)); else l[i] = (lane, l[i].Item2 + rate);
        }
        bool IsRaw(Lane l) => laneProducers.GetValueOrDefault(l)?.Any(p => nodes[p].Kind is NodeKind.Raw or NodeKind.Import) == true;

        // ---- production order (longest path to an output): earlier steps first = further west / south ----
        var outs = plan.Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().ToList());
        var depth = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        int Depth(string k)
        {
            if (depth.TryGetValue(k, out var d)) return d;
            onStack.Add(k);
            int best = 0;
            foreach (var t in outs.GetValueOrDefault(k) ?? []) if (!onStack.Contains(t)) best = Math.Max(best, Depth(t) + 1);
            onStack.Remove(k);
            return depth[k] = best;
        }
        var machineNodes = nodes.Values.Where(n => n.Kind == NodeKind.Machine && n.Recipe != null)
            .OrderByDescending(n => Depth(n.Key)).ThenBy(n => n.Item).ThenBy(n => LineOf(n.Key)).ToList();

        // ---- blocks: machine groups split into row segments ----
        var blocks = new List<Block>();
        foreach (var n in machineNodes)
        {
            var building = n.Recipe!.Building;
            var (fw, fd) = Footprint(building);
            var ins = rowIn.GetValueOrDefault(n.Key) ?? [];
            var outsL = rowOut.GetValueOrDefault(n.Key) ?? [];
            var p = Ports(building, fw, ins.Count(x => !x.lane.Fluid), ins.Count(x => x.lane.Fluid), outsL.Count(x => !x.lane.Fluid), outsL.Count(x => x.lane.Fluid));
            double[] Offsets(List<(Lane lane, double rate)> list, double[] itemPorts, double[] pipePorts)
            {
                int a = 0, b = 0;
                return list.Select(x => x.lane.Fluid
                    ? (pipePorts.Length > 0 ? pipePorts[Math.Min(b++, pipePorts.Length - 1)] : 0)
                    : (itemPorts.Length > 0 ? itemPorts[Math.Min(a++, itemPorts.Length - 1)] : 0)).ToArray();
            }
            int machines = Math.Max(1, n.Machines);
            int subs = (int)Math.Ceiling(machines / (double)maxPerRow);
            for (int sr = 0; sr < subs; sr++)
            {
                int count = Math.Min(maxPerRow, machines - sr * maxPerRow);
                blocks.Add(new Block
                {
                    Node = n, Building = building, W = fw, D = fd, Pitch = fw + 2, Count = count, Sub = sr, Subs = subs, Total = machines,
                    Share = (double)count / machines, Ins = ins, Outs = outsL,
                    InOff = Offsets(ins, p.itemIn, p.pipeIn), OutOff = Offsets(outsL, p.itemOut, p.pipeOut)
                });
            }
        }

        // ---- pack blocks into bands (rows) in production order ----
        // a block goes into the row where its inputs are made, else the row after it, else the first row with room
        const double Gap = Foundation; // between blocks in a band: room for trunk belts to pass
        var bands = new List<Band>();
        bool Fits(Band bd, double w, double extra = 0) => bd.Width + Gap + extra + w <= maxBandWidth;
        void Put(Band bd, Block b)
        {
            bd.Width = SnapTo(bd.Width + (bd.Blocks.Count > 0 ? Gap + b.ExtraGap : 0), Foundation) + b.Width;
            bd.Blocks.Add(b);
        }
        foreach (var b in blocks)
        {
            var producerKeys = b.Ins.Where(x => !IsRaw(x.lane)).SelectMany(x => laneProducers.GetValueOrDefault(x.lane) ?? []).ToHashSet();
            int p = bands.FindLastIndex(bd => bd.Blocks.Any(x => producerKeys.Contains(x.Node.Key)));
            Band? target = null;
            if (p >= 0 && Fits(bands[p], b.Width)) target = bands[p];
            else if (p >= 0 && p + 1 < bands.Count && Fits(bands[p + 1], b.Width)) target = bands[p + 1];
            else if (p >= 0 && p + 1 == bands.Count) { target = new Band(); bands.Add(target); }
            target ??= bands.FirstOrDefault(bd => bd.Blocks.Count > 0 && Fits(bd, b.Width));
            if (target == null) { target = new Band(); bands.Add(target); }
            Put(target, b);
        }

        // ---- station ("pins"): the boxes are blocks too — one group for inputs + real outputs, one for the spare boxes.
        //      Input boxes feed the row's output belts (north face), output boxes take from its input belts (south face),
        //      so they are wired exactly like machines: straight to the trunk of their item, nothing runs around outside.
        string Box(Lane l) => l.Fluid ? (s.IndustrialFluidBox ? "Desc_IndustrialTank_C" : "Desc_PipeStorageTank_C") : "Desc_StorageContainerMk2_C";
        Block? Station(IEnumerable<(GraphNode node, Lane lane, double rate)> ins, IEnumerable<(GraphNode node, Lane lane, double rate)> outs)
        {
            var slots = new List<Slot>();
            double x = 0;
            foreach (var w in ins.OrderBy(w => w.lane.Id))
            {
                var (cw, cd) = Footprint(Box(w.lane));
                slots.Add(new Slot(w.node, w.lane, w.rate, true, Box(w.lane), cw, cd, x));
                x += cw + 2;
            }
            if (slots.Count > 0 && outs.Any()) x += Foundation - 2; // inputs and outputs a foundation apart
            foreach (var w in outs.OrderBy(w => w.lane.Id))
            {
                var (cw, cd) = Footprint(Box(w.lane));
                slots.Add(new Slot(w.node, w.lane, w.rate, false, Box(w.lane), cw, cd, x));
                x += cw + 2;
            }
            if (slots.Count == 0) return null;
            return new Block { Node = slots[0].Node, Building = "", W = x - 2, D = slots.Max(sl => sl.D), Count = 1, Slots = slots };
        }
        var io = Station(wallIn, wallOut.Where(w => w.node.Kind != NodeKind.Surplus));
        var spare = Station([], wallOut.Where(w => w.node.Kind == NodeKind.Surplus));

        const double areaXPlace = Foundation;
        // ---- placement, standard-cell style: machine groups are cells in rows, every item line is a net, the station
        //      boxes are I/O pads. The pad cell sits in row 0 (the south border); simulated annealing then moves cells
        //      between rows, reorders them within rows and swaps whole rows to minimise the total half-perimeter wire
        //      length (HPWL) — so intermediates end up right next to the machines that use them, and raw / final rows
        //      gather round the pads. ----
        var allBlocks = blocks.Concat(new[] { io, spare }.OfType<Block>()).ToList();
        IEnumerable<Lane> LanesOf(Block b) => b.Slots != null ? b.Slots.Select(sl => sl.Lane) : b.Ins.Select(x => x.lane).Concat(b.Outs.Select(x => x.lane));
        if (io != null)
        {
            var host = bands.Where(bd => Fits(bd, io.Width) && SnapTo(bd.Blocks.Max(b => b.D), Foundation) >= io.D).OrderBy(bd => bd.Width).FirstOrDefault();
            if (host == null) host = new Band(); else bands.Remove(host);
            bands.Insert(0, host);
            Put(host, io);
        }
        if (spare != null)
        {
            spare.ExtraGap = Foundation; // a foundation more than usual from whatever sits west of it
            var host = bands.Where(bd => Fits(bd, spare.Width, Foundation) && (bd.Blocks.Count == 0 || SnapTo(bd.Blocks.Max(b => b.D), Foundation) >= spare.D))
                .OrderBy(bd => bd.Width).FirstOrDefault();
            if (host == null) { host = new Band(); bands.Add(host); }
            Put(host, spare);
        }
        // nets by lane index; every block knows which nets it touches
        var laneIdx = lanes.Values.Select((l, i) => (l, i)).ToDictionary(v => v.l, v => v.i);
        // pins per block: (net, is it on the input side) — rows can be flipped, which swaps the sides' channels
        var netsOf = new Dictionary<Block, (int n, bool input)[]>();
        foreach (var b in allBlocks)
            netsOf[b] = b.Slots != null ? b.Slots.Select(sl => (laneIdx[sl.Lane], false)).ToArray()
                : b.Ins.Select(x => (laneIdx[x.lane], true)).Concat(b.Outs.Select(x => (laneIdx[x.lane], false))).Distinct().ToArray();
        int nl = laneIdx.Count;
        var nlo = new double[nl]; var nhi = new double[nl]; var nylo = new double[nl]; var nyhi = new double[nl];
        var nflo = new int[nl]; var nfhi = new int[nl]; var ncount = new int[nl];
        const double RowPitch = 40; // rough height of a row with its belt channels
        const double LiftCost = 24; // a floor change: the lift plus the belt to reach it
        int maxFloors = Math.Clamp(s.Floors, 1, 4);
        // buildings too tall for any storey (> 15 m clear under a 16 m floor) only go on the ground floor (open above)
        bool IsTall(Block b) => b.Slots == null && HeightOf(b.Building) > 15;
        var rowTall = new Dictionary<List<Block>, bool>();
        var perFloorRows = new int[maxFloors];
        // row state: floor * 2 + 1 if the row is flipped (turned 180°: inputs on the north channel, outputs south)
        double Hpwl(List<List<Block>> rows, List<int> floors)
        {
            Array.Fill(nlo, double.MaxValue); Array.Fill(nhi, double.MinValue); Array.Fill(nylo, double.MaxValue); Array.Fill(nyhi, double.MinValue);
            Array.Fill(nflo, int.MaxValue); Array.Fill(nfhi, int.MinValue); Array.Clear(ncount); Array.Clear(perFloorRows);
            double width = 0; int fluidUp = 0;
            int topFloor = 0;
            for (int r = 0; r < rows.Count; r++) if (rows[r].Count > 0) topFloor = Math.Max(topFloor, floors[r] >> 1);
            for (int r = 0; r < rows.Count; r++)
            {
                if (rows[r].Count == 0) continue;
                int f = floors[r] >> 1;
                bool flip = (floors[r] & 1) == 1;
                double ry = perFloorRows[f]++ * RowPitch;
                double x = areaXPlace, w = 0;
                for (int i = 0; i < rows[r].Count; i++)
                {
                    var b = rows[r][i];
                    if (f > 0 && IsTall(b)) return double.MaxValue; // too tall for a storey: the ground floor, open above
                    if (f > 0 && (b.Ins.Any(q => q.lane.Fluid) || b.Outs.Any(q => q.lane.Fluid))) fluidUp += f; // fluids low: no pumps
                    double x0 = SnapTo(x + (i > 0 ? b.ExtraGap : 0), Foundation);
                    double cx = x0 + b.Width / 2;
                    foreach (var (n, input) in netsOf[b])
                    {
                        double py = ry + (input != flip ? -RowPitch * 0.3 : RowPitch * 0.3); // south / north channel
                        nlo[n] = Math.Min(nlo[n], cx); nhi[n] = Math.Max(nhi[n], cx);
                        nylo[n] = Math.Min(nylo[n], py); nyhi[n] = Math.Max(nyhi[n], py);
                        nflo[n] = Math.Min(nflo[n], f); nfhi[n] = Math.Max(nfhi[n], f); ncount[n]++;
                    }
                    x = x0 + b.Width + Gap; w = x0 + b.Width - areaXPlace;
                }
                if (w > maxBandWidth && rows[r].Count > 1) return double.MaxValue;
                width = Math.Max(width, w);
            }
            double sum = 150 * fluidUp;
            for (int n = 0; n < nl; n++) if (ncount[n] > 1) sum += nhi[n] - nlo[n] + nyhi[n] - nylo[n] + (nfhi[n] - nflo[n]) * LiftCost;
            // compact: the footprint (widest floor, tallest stack of rows on any floor) costs too
            double height = perFloorRows.Max() * RowPitch;
            double cost = sum + 0.5 * Math.Max(width, height) + 0.02 * width * height / Foundation;
            if (s.BlueprintTile > 0)
            {
                // blueprints: a machine row can't straddle a tile border, so each row (with its belt channels) takes
                // about one tile row — every tile is one more blueprint to place and join by hand
                double tl = s.BlueprintTile * Foundation;
                cost += 150 * Math.Ceiling(width / tl - 1e-9) * perFloorRows.Max();
            }
            return cost;
        }
        var rowFloor = new List<int>();
        {
            var rows = bands.Select(bd => bd.Blocks.ToList()).ToList();
            var floors = rows.Select(_ => 0).ToList();
            var rng = new Random(12345);
            double cur = Hpwl(rows, floors), bestCost = cur;
            var best = rows.Select(r => r.ToList()).ToList();
            var bestFloors = floors.ToList();
            int iters = Math.Min(20000, 1500 + 300 * allBlocks.Count) * (maxFloors > 1 ? 2 : 1);
            double t0 = Math.Max(1, cur * 0.02), t1 = t0 * 0.001;
            for (int it = 0; it < iters && rows.Count > 0; it++)
            {
                double temp = t0 * Math.Pow(t1 / t0, it / (double)iters);
                var trial = rows.Select(r => r.ToList()).ToList();
                var tfl = floors.ToList();
                int move = rng.Next(5);
                if (move == 3 && maxFloors == 1) move = 2;
                if (move == 4) move = 2; // row flipping: off (made belts longer, not shorter)
                if (move == 0 && trial.Count > 2)
                {
                    // swap two rows (row 0, the pad row, stays); each takes the other's place and floor
                    int i = 1 + rng.Next(trial.Count - 1), j = 1 + rng.Next(trial.Count - 1);
                    if (i == j) continue;
                    (trial[i], trial[j]) = (trial[j], trial[i]);
                }
                else if (move == 1 && trial.Count > 1)
                {
                    // move one cell to another row (not the pad cell)
                    int from = rng.Next(trial.Count);
                    if (trial[from].Count == 0) continue;
                    int k = rng.Next(trial[from].Count);
                    var b = trial[from][k];
                    if (b == io) continue;
                    int to = rng.Next(trial.Count);
                    if (to == from) continue;
                    trial[from].RemoveAt(k);
                    trial[to].Insert(rng.Next(trial[to].Count + 1), b);
                }
                else if (move == 3 && trial.Count > 1)
                {
                    // put a whole row on another floor (the pad row stays on the ground floor)
                    int r = 1 + rng.Next(trial.Count - 1);
                    int f = rng.Next(maxFloors);
                    if (f == tfl[r] >> 1) continue;
                    tfl[r] = f * 2 + (tfl[r] & 1);
                }
                else if (move == 4 && trial.Count > 1)
                {
                    // flip a row (the pad row keeps its outer side clear, so it never flips)
                    int r = 1 + rng.Next(trial.Count - 1);
                    tfl[r] ^= 1;
                }
                else
                {
                    // swap two cells within a row
                    int r = rng.Next(trial.Count);
                    if (trial[r].Count < 2) continue;
                    int i = rng.Next(trial[r].Count), j = rng.Next(trial[r].Count);
                    if (i == j) continue;
                    (trial[r][i], trial[r][j]) = (trial[r][j], trial[r][i]);
                }
                // the pad cell stays at the east end of row 0, so no row belt has to pass under it
                if (io != null && trial[0].Remove(io)) trial[0].Add(io);
                double c = Hpwl(trial, tfl);
                if (c == double.MaxValue) continue;
                if (c < cur || rng.NextDouble() < Math.Exp((cur - c) / temp))
                {
                    rows = trial; floors = tfl; cur = c;
                    if (c < bestCost) { bestCost = c; best = rows.Select(r => r.ToList()).ToList(); bestFloors = floors.ToList(); }
                }
            }
            // floors used, renumbered from 0 without gaps; rows ordered floor by floor
            var used = bestFloors.Where((f, i) => best[i].Count > 0).Select(f => f >> 1).Distinct().OrderBy(f => f).ToList();
            bands = new List<Band>();
            foreach (var f in used)
                for (int i = 0; i < best.Count; i++)
                {
                    if (best[i].Count == 0 || bestFloors[i] >> 1 != f) continue;
                    var bd = new Band { Floor = used.IndexOf(f), Flipped = (bestFloors[i] & 1) == 1 && !best[i].Any(b => b.Slots != null) };
                    foreach (var b in best[i]) Put(bd, b);
                    bands.Add(bd);
                }
            L.Floors = used.Count;
        }
        var bandOf = new Dictionary<Block, Band>();
        foreach (var bd in bands) foreach (var b in bd.Blocks) bandOf[b] = bd;
        for (int i = 0; i < bands.Count; i++) foreach (var b in bands[i].Blocks) b.Band = i;

        // ---- block positions (west edge of each row on a foundation line) ----
        // blueprint tiles: a machine / box that would straddle a tile border is pushed to the next tile (on the
        // foundation grid, so the corner anchor stays valid; the spacing after it grows by that much)
        double tile = s.BlueprintTile * Foundation;
        bool Straddles(double a, double z) => tile > 0 && Math.Floor((a + 0.01) / tile) != Math.Floor((z - 0.01) / tile);
        double NextBorder(double a) => Math.Ceiling((a + 0.01) / tile) * tile;
        double areaX = Foundation;
        foreach (var band in bands)
        {
            double x = areaX;
            for (int i = 0; i < band.Blocks.Count; i++)
            {
                var b = band.Blocks[i];
                if (i > 0) x += b.ExtraGap;
                b.X0 = SnapTo(x, Foundation);
                b.Shift = null;
                if (tile > 0 && b.Slots != null)
                {
                    // station boxes one by one
                    double push = 0;
                    for (int k = 0; k < b.Slots.Count; k++)
                    {
                        var sl = b.Slots[k];
                        double a = b.X0 + sl.Off + push;
                        if (Straddles(a, a + sl.W)) push += NextBorder(a) - a;
                        b.Slots[k] = sl with { Off = sl.Off + push };
                    }
                    b.W = b.Slots.Max(sl => sl.Off + sl.W);
                }
                else if (tile > 0)
                {
                    double push = 0;
                    var sh = new double[b.Count];
                    for (int m = 0; m < b.Count; m++)
                    {
                        double a = b.X0 + m * b.Pitch + push;
                        if (b.W < tile && Straddles(a, a + b.W)) push += SnapTo(NextBorder(a) - a, 1);
                        sh[m] = push;
                    }
                    if (push > 0) b.Shift = sh;
                }
                x = b.X0 + b.Width + Gap;
            }
            band.Width = x - Gap - areaX;
        }
        double areaRight = areaX + bands.Select(bd => bd.Width).DefaultIfEmpty(0).Max();

        // ---- connections: every port of every block, as (row, which side, x positions) ----
        //      level of a connection: 3·row (input belts, south of the machines), 3·row+2 (output belts, north)
        var conns = new List<Conn>();
        double Fs(Block b) => bands[b.Band].Flipped ? -1 : 1; // a flipped machine's ports mirror east–west
        bool South(Conn c) => c.IsIn != bands[c.B.Band].Flipped;  // which channel: south of the machines, or north
        foreach (var b in allBlocks.Where(b => bandOf.ContainsKey(b)))
        {
            if (b.Slots != null)
                foreach (var sl in b.Slots)
                    conns.Add(new Conn(b, sl.Lane, false, [b.X0 + sl.Off + sl.W / 2], sl.Rate, -1, sl)); // boxes: north (inner) face only
            else
            {
                for (int i = 0; i < b.Ins.Count; i++)
                    conns.Add(new Conn(b, b.Ins[i].lane, true, Enumerable.Range(0, b.Count).Select(m => b.Cx(m) + Fs(b) * b.InOff[i]).ToArray(), b.Ins[i].rate * b.Share, i, null));
                for (int i = 0; i < b.Outs.Count; i++)
                    conns.Add(new Conn(b, b.Outs[i].lane, false, Enumerable.Range(0, b.Count).Select(m => b.Cx(m) + Fs(b) * b.OutOff[i]).ToArray(), b.Outs[i].rate * b.Share, i, null));
            }
        }
        int Level(Conn c) => 3 * c.B.Band + (South(c) ? 0 : 2);
        var inPorts = bands.Select((_, k) => conns.Where(c => c.B.Band == k && South(c)).SelectMany(c => c.Xs).ToList()).ToList();   // south channel
        var outPorts = bands.Select((_, k) => conns.Where(c => c.B.Band == k && !South(c)).SelectMany(c => c.Xs).ToList()).ToList(); // north channel
        // storey height per floor: 12 m if everything on it is at most 11 m tall (Assembler), else 16 m; the top floor
        // is open. A belt may be lifted over a machine only with ≥ 3 m to spare below the floor above.
        int topFl = L.Floors - 1;
        var pitch = Enumerable.Range(0, L.Floors).Select(f =>
        {
            // (machines over 15 m reach through the open floor above: they don't set the storey height)
            double h = bands.Where(bd => bd.Floor == f).SelectMany(bd => bd.Blocks).Select(b => b.Slots != null ? 8 : HeightOf(b.Building)).Where(v => v <= 15).DefaultIfEmpty(8).Max();
            return Math.Max(12, Math.Ceiling((h + 4) / 4) * 4); // (~4 m headroom above the tallest machine)
        }).ToList();
        L.FloorElevation = [0];
        for (int f = 1; f < L.Floors; f++) L.FloorElevation.Add(L.FloorElevation[^1] + pitch[f - 1]);
        // for blueprints nothing is lifted over machines: two belt levels (ports, crossings) are then always enough
        double LiftLimit(int f) => s.BlueprintTile > 0 ? 0 : f == topFl ? MaxLiftOver : Math.Min(MaxLiftOver, pitch[f] - 1 - 3);
        var rowInFloor = new int[bands.Count];
        for (int k = 0, prevF = -1, r = 0; k < bands.Count; k++) { if (bands[k].Floor != prevF) { prevF = bands[k].Floor; r = 0; } rowInFloor[k] = r++; }
        var bodies = bands.Select(bd => bd.Blocks.SelectMany(b => Enumerable.Range(0, b.Count).Select(m =>
            (a: b.Mx(m), z: b.Mx(m) + b.W, tall: b.Slots == null && HeightOf(b.Building) > LiftLimit(bd.Floor)))).ToList()).ToList();
        // lookup per level and whole metre: 0 free, 1 over a machine body, 2 blocked (a port column)
        int gridW = (int)Math.Ceiling(areaRight) + 64;
        var grid = new byte[3 * bands.Count][];
        for (int k = 0; k < bands.Count; k++)
        {
            var gi = grid[3 * k] = new byte[gridW]; var gb = grid[3 * k + 1] = new byte[gridW]; var go = grid[3 * k + 2] = new byte[gridW];
            void Block(byte[] g, double p) { for (int x = (int)Math.Floor(p - 2) + 1; x < p + 2; x++) if (x >= 0 && x < gridW) g[x] = 2; }
            foreach (var p in inPorts[k]) Block(gi, p);
            foreach (var p in outPorts[k]) Block(go, p);
            foreach (var f in bodies[k]) for (int x = (int)Math.Floor(f.a - 0.5) + 1; x < f.z + 0.5; x++) if (x >= 0 && x < gridW) gb[x] = Math.Max(gb[x], f.tall ? (byte)2 : (byte)1);
        }
        // blueprint tiles: no vertical belt within 2 m of a tile border (splitters are 4 m wide)
        if (tile > 0)
            for (double bx = tile; bx < gridW; bx += tile)
                for (int x = Math.Max(0, (int)Math.Floor(bx - 3)); x <= bx + 3 && x < gridW; x++)
                    foreach (var g in grid) g[x] = 2;
        // station boxes are pins: their outer (south) side stays free — no trunk through their row's input channel or
        // machine row across them, and no row belt south of them
        var padSpans = bands.Select(bd => bd.Blocks.Where(b => b.Slots != null).Select(b => (a: b.X0 - 2, z: b.X0 + b.Width + 2)).ToList()).ToList();
        for (int k = 0; k < bands.Count; k++)
            foreach (var (pa, pz) in padSpans[k])
                for (int x = Math.Max(0, (int)Math.Floor(pa)); x < pz && x < gridW; x++) { grid[3 * k][x] = 2; grid[3 * k + 1][x] = 2; }

        // ---- trunks: one vertical belt per item line, placed where its belts along the rows come out shortest.
        //      No buses and no left/right rule: raw inputs, intermediates and products all go straight from the
        //      producing belts to the consuming ones. A trunk may pass between machines or over them (lifted), but
        //      never through another row's port column, and trunks keep 4 m apart (splitters are 4 m wide). ----
        var gapCols = bands.Select(bd => bd.Blocks.SelectMany(b =>
            new[] { b.X0 - Gap / 2 - 2, b.X0 - Gap / 2 + 2, b.X0 + b.Width + Gap / 2 - 2, b.X0 + b.Width + Gap / 2 + 2 }
                .Concat(Enumerable.Range(1, Math.Max(0, b.Count - 1)).Select(m => b.Mx(m) - 1))).Select(v => Math.Round(v)).ToList()).ToList();
        var trunkCols = new List<(double x, int lo, int hi)>();
        var laneConns = conns.GroupBy(c => c.Lane).ToDictionary(g => g.Key, g => g.ToList());
        int FloorOf(Conn c) => bands[c.B.Band].Floor;
        // a trunk has one segment per floor it serves; floors meet at a lift in the anchor floor's row nearest the
        // middle of the item's rows (the other floors' segments reach the same spot)
        var segs = new Dictionary<Lane, List<(int f, int lo, int hi)>>();
        var liftBand = new Dictionary<Lane, int>();
        foreach (var (lane, cs) in laneConns)
        {
            var byF = cs.GroupBy(FloorOf).ToDictionary(g => g.Key, g => g.ToList());
            var list = new List<(int f, int lo, int hi)>();
            if (byF.Count > 1)
            {
                int anchor = byF.OrderByDescending(g => g.Value.Count).ThenBy(g => g.Key).First().Key;
                var rs = byF[anchor].Select(c => rowInFloor[c.B.Band]).OrderBy(v => v).ToList();
                int med = rs[rs.Count / 2];
                liftBand[lane] = Enumerable.Range(0, bands.Count).First(k => bands[k].Floor == anchor && rowInFloor[k] == med);
                foreach (var (f, fc) in byF)
                {
                    var fb = Enumerable.Range(0, bands.Count).Where(k => bands[k].Floor == f).ToList();
                    int lb = fb[Math.Min(med, fb.Count - 1)];
                    list.Add((f, Math.Min(fc.Min(Level), 3 * lb + 2), Math.Max(fc.Max(Level), 3 * lb + 2)));
                }
            }
            else list.Add((byF.Keys.First(), cs.Min(Level), cs.Max(Level)));
            segs[lane] = list;
        }
        var trunkLanes = laneConns.Keys.OrderByDescending(l => segs[l].Sum(sg => sg.hi - sg.lo)).ThenBy(l => l.Id).ToList();
        double eastSpill = SnapTo(areaRight, Foundation) + 2;
        foreach (var lane in trunkLanes)
        {
            var cs = laneConns[lane];
            var sg = segs[lane];
            double xmin = cs.Min(c => c.Xs.Min()), xmax = cs.Max(c => c.Xs.Max());
            double? best = null; double bestCost = double.MaxValue;
            // candidates come from the machine groups themselves: the gaps before / after each group and between its
            // machines in every row the trunk touches, and right beside each of its own ports
            var cand = new SortedSet<double>();
            foreach (var (_, lo, hi) in sg) for (int k = lo / 3; k <= hi / 3; k++) foreach (var x in gapCols[k]) cand.Add(x);
            foreach (var c in cs) foreach (var px in c.Xs) { cand.Add(Math.Round(px - 3)); cand.Add(Math.Round(px + 3)); }
            void Try(double x)
            {
                bool bad = false; int over = 0; int xi = (int)x;
                if (xi >= gridW) return;
                foreach (var (_, lo, hi) in sg)
                    for (int lv = lo; lv <= hi && !bad; lv++)
                    {
                        byte g = grid[lv][xi];
                        if (g == 2) bad = true; else if (g == 1) over++;
                    }
                if (!bad) bad = cs.Any(c => South(c) && padSpans[c.B.Band].Any(sp => Math.Min(c.Xs.Min(), x) < sp.z && Math.Max(c.Xs.Max(), x) > sp.a));
                if (bad) return;
                double cost = cs.Sum(c => Math.Max(0, c.Xs.Min() - x) + Math.Max(0, x - c.Xs.Max())) + 6 * over;
                if (cost < bestCost - 1e-9) { bestCost = cost; best = x; }
            }
            foreach (var x in cand) if (x >= 2 && x >= xmin - 40 && x <= xmax + 40) Try(x);
            if (best == null) for (double x = 2; x < gridW; x++) Try(x); // anywhere at all
            if (best == null)
            {
                // nothing free near its machines: run it along the edge of the rows — the west edge when it feeds a
                // row with pads west of them (the east side would pass under the pads), else the east edge
                bool west = cs.Any(c => South(c) && padSpans[c.B.Band].Count > 0);
                double x = west ? -2 : eastSpill, step = west ? -4 : 4;
                while (trunkCols.Any(t => Math.Abs(t.x - x) < 4 && sg.Any(q => t.hi >= q.lo && t.lo <= q.hi))) x += step;
                best = x;
            }
            lane.X = best.Value;
            foreach (var (_, lo, hi) in sg)
            {
                trunkCols.Add((lane.X, lo, hi));
                // later trunks keep 4 m away over the same levels
                for (int lv = lo; lv <= hi; lv++)
                    for (int xi = (int)Math.Floor(lane.X - 4) + 1; xi < lane.X + 4; xi++) if (xi >= 0 && xi < gridW) grid[lv][xi] = 2;
            }
        }
        L.Lanes = trunkLanes.Count;

        // ---- belt strips along each row (strips shared where belts don't overlap) ----
        var strips = new Dictionary<Conn, int>();
        // left-edge channel routing: intervals sorted by their left end, each on the first track it fits
        foreach (var c in conns.OrderBy(c => c.B.Band).ThenBy(c => c.IsIn ? 0 : 1).ThenBy(c => Math.Min(c.Xs.Min(), c.Lane.X)))
        {
            var band = bands[c.B.Band];
            double a = Math.Min(c.Xs.Min(), c.Lane.X), z = Math.Max(c.Xs.Max(), c.Lane.X);
            strips[c] = Strip(c.IsIn ? band.InStrips : band.OutStrips, a, z);
        }

        // ---- vertical positions ----
        double y = 0, maxY = 0;
        // ground-floor rows holding a machine too tall for a storey: no floor above them (open to the sky), so the
        // upper floors' rows skip that stretch
        var tallRows = new List<(double y0, double y1)>();
        bool redo = false;
        for (int k = 0; k < bands.Count; k++)
        {
            var band = bands[k];
            if (k > 0 && band.Floor != bands[k - 1].Floor && !redo) y = 0; // every floor starts at the south edge
            redo = false;
            band.Y = y;
            // belt tracks every half foundation, strip 0 nearest the machines; with blueprint tiles no machine row and
            // no track (4 m splitters) may sit on a tile border — rows move past it, tracks skip it
            // (4.5 m: a belt crossing this track must be back down 1.5 m before the border, where the tiles are
            // bridged by hand, and needs 2 m to ramp over the 2 m-wide belt)
            bool OnBorder(double ty) => tile > 0 && Math.Abs(ty - Math.Round(ty / tile) * tile) < 4.5;
            double machineD = SnapTo(band.Blocks.Max(b => b.D), Foundation);
            int lowN = band.Flipped ? band.OutStrips.Count : band.InStrips.Count, highN = band.Flipped ? band.InStrips.Count : band.OutStrips.Count;
            var low = new List<double>();
            for (double my = y + SnapTo(Math.Max(1, lowN) * TrackPitch + 2, Foundation); ; my += Foundation)
            {
                if (Straddles(my, my + machineD)) continue;
                var tracks = new List<double>();
                for (double ty = my - 4; tracks.Count < lowN && ty >= y + 2; ty -= TrackPitch) if (!OnBorder(ty)) tracks.Add(ty);
                if (tracks.Count < lowN) continue;
                band.MachineY = my; low = tracks;
                break;
            }
            band.OutBase = band.MachineY + machineD;
            var high = new List<double>();
            for (double oy = band.OutBase + 4; high.Count < highN; oy += TrackPitch) if (!OnBorder(oy)) high.Add(oy);
            band.InTracks = band.Flipped ? high : low;
            band.OutTracks = band.Flipped ? low : high;
            band.Top = SnapTo(Math.Max(band.OutBase + 4, (high.Count > 0 ? high[^1] : band.OutBase) + 4), Foundation);
            if (band.Floor > 0 && tallRows.FirstOrDefault(r => band.Y < r.y1 && band.Top > r.y0) is var hit && hit != default)
            { y = hit.y1; redo = true; k--; continue; } // over a tall machine: start past it
            if (band.Floor == 0 && band.Blocks.Any(b => b.Slots == null && HeightOf(b.Building) > 15)) tallRows.Add((band.Y, band.Top));
            y = band.Top; maxY = Math.Max(maxY, y);
            L.Rows += band.Blocks.Count(b => b.Slots == null);
        }
        // belt tracks every half foundation (a 2 m belt with room for 4 m splitters), strip 0 nearest the machines
        double InY(Band band, int strip) => band.InTracks[strip];
        double OutY(Band band, int strip) => band.OutTracks[strip];
        double StripY(Conn c) => c.IsIn ? InY(bands[c.B.Band], strips[c]) : OutY(bands[c.B.Band], strips[c]);
        double maxX = areaRight + Foundation;

        // everything drawn below goes on the floor in `fl`
        int fl = 0;
        void AddB(Placed v) => L.Buildings.Add(v with { Floor = fl });
        void AddS(Segment v) => L.Belts.Add(v with { Floor = fl });
        void AddM(Marker v) => L.Markers.Add(v with { Floor = fl });
        string FloorTag(int f) => L.Floors > 1 ? $"F{f} · " : "";
        // which way items flow: machine inputs and output / spare boxes take items (boxes sit on the north channel,
        // like machine outputs, but they are sinks)
        static bool Takes(Conn c) => c.IsIn || c.Slot is { Input: false };

        // ---- draw machines and station boxes ----
        foreach (var band in bands)
            foreach (var b in band.Blocks)
            {
                fl = band.Floor;
                double my = band.MachineY;
                if (b.Slots != null)
                {
                    foreach (var sl in b.Slots)
                        AddB(new Placed(sl.Input ? "input" : sl.Node.Kind == NodeKind.Surplus ? "surplus" : "output", sl.Building,
                            $"{(sl.Input ? "IN" : "OUT")} {GameData.Item(sl.Lane.Item).Name}{LineText(sl.Lane)} · {sl.Rate:0.#}/min", b.X0 + sl.Off, my, sl.W, sl.D, sl.Lane.Item));
                    continue;
                }
                string label = FloorTag(band.Floor) + $"{GameData.Item(b.Node.Item).Name}{(b.Node.Key.Contains('#') ? $" #{LineOf(b.Node.Key)}" : "")}" +
                               (b.Subs > 1 ? $" ({b.Sub + 1}/{b.Subs})" : "") + $" · {b.Count}× {GameData.Buildings.GetValueOrDefault(b.Building)?.Name}";
                for (int m = 0; m < b.Count; m++)
                    AddB(new Placed("machine", b.Building, m == 0 ? label + "\n" + $"@@SW|{b.Pitch}|{b.X0}|{my}" : "", b.Mx(m), my, b.W, b.D, b.Node.Item, Recipe: b.Node.Recipe?.ClassName) { Flipped = band.Flipped });
                AddM(new Marker("anchor", b.X0, my, ""));
            }

        var spans = conns.GroupBy(o => (o.B.Band, o.IsIn)).ToDictionary(g => g.Key,
            g => g.Select(o => (strip: strips[o], a: Math.Min(o.Xs.Min(), o.Lane.X), z: Math.Max(o.Xs.Max(), o.Lane.X))).ToList());
        // ---- draw row belts: manifolds (splitter per input port) and collectors (merger per output port) ----
        foreach (var c in conns)
        {
            var band = bands[c.B.Band];
            fl = band.Floor;
            var lane = c.Lane;
            double by = StripY(c);
            int strip = strips[c];
            double a = Math.Min(c.Xs.Min(), lane.X), z = Math.Max(c.Xs.Max(), lane.X);
            // segments point the way items flow: out from the trunk along a manifold, towards it along a collector
            foreach (var end in new[] { a, z })
                if (Math.Abs(end - lane.X) > 0.01)
                    AddS((Takes(c) ? new Segment(lane.X, by, end, by, lane.Fluid, lane.Item) : new Segment(end, by, lane.X, by, lane.Fluid, lane.Item)) with { Line = lane.Id });
            // the port farthest from the trunk is the end of the manifold
            var far = c.Xs.OrderByDescending(px => Math.Abs(px - lane.X)).First();
            foreach (var px in c.Xs)
            {
                double face = c.Slot != null ? band.MachineY + c.Slot.D : (South(c) ? band.MachineY : band.MachineY + c.B.D);
                bool lifted = spans[(c.B.Band, c.IsIn)].Any(o => o.strip < strip && o.a <= px && px <= o.z);
                AddS((Takes(c) ? new Segment(px, by, px, face, lane.Fluid, lane.Item, lifted) : new Segment(px, face, px, by, lane.Fluid, lane.Item, lifted)) with { Line = lane.Id });
                if (c.IsIn && (px != far || s.EndSplitter) && c.Slot == null) AddM(new Marker(lane.Fluid ? "junction" : "splitter", px, by, ""));
                if (!c.IsIn && px != far && c.Slot == null) AddM(new Marker(lane.Fluid ? "junction" : "merger", px, by, ""));
                if (c.Slot != null) L.Wall.Add((px, face, lane.Item, by, px));
            }
            if (c.IsIn && c.Slot == null) AddM(new Marker("rate", c.B.X0 - 7, by, $"{c.Rate:0.#}"));
            if (!c.IsIn && c.Slot == null)
            {
                var g = plan.Guards.FirstOrDefault(g => g.Row.Recipe == c.B.Node.Recipe && g.Item == lane.Item);
                if (g != null) AddM(new Marker(g.Fluid ? "valve" : "guard", c.B.Cx(c.B.Count - 1) + 6, by, g.AllAway ? "→ " + Loc.T("guard.badge") : "⛨"));
            }
        }

        // ---- draw trunks: splitters where a consuming row branches off mid-trunk, mergers where a producer joins;
        //      on several floors, each floor's piece reaches the lift, which stands in for the other floors ----
        foreach (var lane in trunkLanes)
        {
            var cs = laneConns[lane];
            var floorsUsed = cs.Select(FloorOf).Distinct().OrderBy(f => f).ToList();
            double? liftY = liftBand.TryGetValue(lane, out var lbk) ? bands[lbk].Top - 2 : null;
            if (liftY != null)
            {
                // the lift / floor hole (4×4 m) must be clear of buildings on every floor it stands on or passes through:
                // pick the clear channel edge (top of a row's output channel, never on a belt track) that needs the
                // least extra trunk on the floors it serves
                int f0 = floorsUsed[0], f1 = floorsUsed[^1];
                bool Clear(double yy) => !L.Buildings.Any(b => b.Floor >= f0 && b.Floor <= f1 &&
                    b.X < lane.X + 2 && b.X + b.W > lane.X - 2 && b.Y < yy + 2 && b.Y + b.H > yy - 2);
                double Extra(double yy) => floorsUsed.Sum(f =>
                {
                    var fy = cs.Where(c => FloorOf(c) == f).Select(StripY).ToList();
                    return Math.Max(0, fy.Min() - yy) + Math.Max(0, yy - fy.Max());
                });
                var pick = bands.Where(bd => bd.Floor >= f0 && bd.Floor <= f1).Select(bd => bd.Top - 2)
                    .Where(Clear).OrderBy(Extra).Cast<double?>().FirstOrDefault();
                if (pick != null) liftY = pick;
            }
            if (liftY != null) L.LiftLength += L.FloorElevation[floorsUsed[^1]] - L.FloorElevation[floorsUsed[0]];
            foreach (var f in floorsUsed)
            {
                fl = f;
                var ys = cs.Where(c => FloorOf(c) == f).Select(c => (y: StripY(c), IsIn: Takes(c))).ToList();
                if (liftY != null)
                {
                    // from this floor the lift looks like a producer if another floor makes the item, else a consumer
                    bool othersMake = cs.Any(c => FloorOf(c) != f && !Takes(c));
                    ys.Add((liftY.Value, !othersMake));
                    // the lift starts on the lowest floor (conveyor lift / vertical pipe) and passes each floor above
                    // through a floor hole; both carry the same foundation position so they line up in game
                    var others = floorsUsed.Where(g => g != f).Select(g => "F" + g);
                    string at = $"({(lane.X) / Foundation:0.##}, {liftY.Value / Foundation:0.##})";
                    string kind = f == floorsUsed[0] ? "liftbase" : "floorhole";
                    string arrow = f == floorsUsed[0] ? "↑" : f == floorsUsed[^1] ? "↓" : "⇅";
                    AddM(new Marker(kind, lane.X, liftY.Value, $"{arrow} {string.Join("/", others)} {at}"));
                }
                double lo = ys.Min(v => v.y), hi = ys.Max(v => v.y);
                if (hi - lo < 0.01) continue;
                bool over = Enumerable.Range(0, bands.Count).Any(k => bands[k].Floor == f &&
                    bands[k].MachineY > lo && bands[k].MachineY < hi && bodies[k].Any(q => lane.X > q.a - 0.5 && lane.X < q.z + 0.5));
                // trunk piece by piece between its branch levels, each pointing from the producers towards the consumers
                var lv = ys.Select(v => v.y).Distinct().OrderBy(v => v).ToList();
                for (int i = 0; i + 1 < lv.Count; i++)
                {
                    bool down = ys.Any(v => !v.IsIn && v.y >= lv[i + 1] - 0.01) && ys.Any(v => v.IsIn && v.y <= lv[i] + 0.01);
                    AddS((down ? new Segment(lane.X, lv[i + 1], lane.X, lv[i], lane.Fluid, lane.Item, over)
                               : new Segment(lane.X, lv[i], lane.X, lv[i + 1], lane.Fluid, lane.Item, over)) with { Line = lane.Id });
                }
                foreach (var (vy, isIn) in ys.Distinct())
                    if (vy > lo + 0.01 && vy < hi - 0.01)
                        AddM(new Marker(lane.Fluid ? "junction" : isIn ? "splitter" : "merger", lane.X, vy, ""));
                AddM(new Marker("lanelabel", lane.X, hi + 1, GameData.Item(lane.Item).Name + LineText(lane)));
            }
            maxX = Math.Max(maxX, lane.X + Foundation);
        }

        maxX = Math.Max(maxX, L.Buildings.Select(b => b.X + b.W).DefaultIfEmpty(0).Max() + Foundation);
        maxX = Math.Max(maxX, L.Markers.Where(m => m.Kind is "guard" or "valve").Select(m => m.X + Foundation).DefaultIfEmpty(0).Max());
        // trunks spilled west of the rows: shift everything east by whole foundations
        double shift = SnapTo(Math.Max(0, 2 - trunkCols.Select(tc => tc.x).DefaultIfEmpty(2).Min()), Foundation);
        if (shift > 0) { L.Shift(shift); maxX += shift; }
        // the open areas above tall machines, on every floor above
        foreach (var tb in L.Buildings.Where(b => b.Floor == 0 && b.Kind == "machine" && HeightOf(b.Building) > 15).ToList())
            for (int f = 1; f < L.Floors; f++)
                L.Buildings.Add(new Placed("hole", "", Loc.T("layout.openAbove", GameData.Buildings.GetValueOrDefault(tb.Building)?.Name ?? ""), tb.X - 1, tb.Y - 1, tb.W + 2, tb.H + 2, Floor: f));
        L.Width = SnapTo(maxX, Foundation);
        L.Height = SnapTo(maxY, Foundation);
        for (int i = 0; i < L.Buildings.Count; i++)
        {
            var b = L.Buildings[i];
            int at = b.Label.IndexOf("@@", StringComparison.Ordinal);
            if (at < 0) continue;
            var parts = b.Label[(at + 2)..].Split('|');
            double ax = double.Parse(parts[2], CultureInfo.InvariantCulture) + shift, ay = double.Parse(parts[3], CultureInfo.InvariantCulture);
            L.Buildings[i] = b with { Label = b.Label[..at] + Loc.T("layout.anchor2", parts[0], ax / Foundation, ay / Foundation, double.Parse(parts[1], CultureInfo.InvariantCulture)) };
        }
        return L;

        string LineText(Lane lane)
        {
            var n = lanes.Values.Count(o => o.Item == lane.Item);
            return n > 1 ? $" #{lane.Id[(lane.Id.LastIndexOf('#') + 1)..]}" : "";
        }
    }

    void Shift(double dx)
    {
        Buildings = Buildings.Select(b => b with { X = b.X + dx }).ToList();
        Belts = Belts.Select(b => b with { X1 = b.X1 + dx, X2 = b.X2 + dx }).ToList();
        Markers = Markers.Select(m => m with { X = m.X + dx }).ToList();
        Wall = Wall.Select(w => (w.x + dx, w.y, w.item, w.jogY, w.laneX + dx)).ToList();
    }

    /// <summary>CSV: one line per building/part with its position in metres and foundations.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder("kind,building,label,floor,x_m,y_m,width_m,depth_m,x_foundation,y_foundation\n");
        string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        string N(double v, string f = "0.##") => v.ToString(f, CultureInfo.InvariantCulture);
        foreach (var b in Buildings)
            sb.AppendLine(string.Join(",", b.Kind, Q(GameData.Buildings.GetValueOrDefault(b.Building)?.Name ?? b.Building), Q(b.Label), b.Floor,
                N(b.X), N(b.Y), N(b.W), N(b.H), N(b.X / Foundation), N(b.Y / Foundation)));
        foreach (var m in Markers.Where(m => m.Kind is "splitter" or "merger" or "junction" or "guard" or "valve" or "anchor" or "liftbase" or "floorhole"))
            sb.AppendLine(string.Join(",", m.Kind, Q(m.Kind), Q(m.Text), m.Floor, N(m.X, "0.#"), N(m.Y, "0.#"), "", "", N(m.X / Foundation), N(m.Y / Foundation)));
        return sb.ToString();
    }
}
