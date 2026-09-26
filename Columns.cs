namespace SatisfactoryPlanner;

/// <summary>
/// The columns layout (docs/LAYOUT_DESIGN.md §2.10f), modelled on the player's hand-built motor factory: one column per
/// production step (steps may share a column), machines packed at their own width, flowing west → east. Deterministic.
///  • Each column's machines take their inputs on the west face and give their outputs on the east face.
///  • The corridor between two columns holds the west column's COLLECT tracks (its outputs, mergers) and the east
///    column's DISTRIBUTE tracks (its inputs, splitters) — raised, one height each, so a track's stubs pass over the
///    nearer ones; a lift at each machine port joins stub and port (a ground track right by the machines needs none).
///    Pipes stay on the ground, outermost.
///  • North of the columns, HEADER rows (8 m up; pipes on the ground) carry each item from where it's made (or its
///    input at the west edge) to the corridors that use it, and products on to their box at the east edge. Rows that
///    don't overlap share a y.
///  • Two floors (Settings.Floors ≥ 2): tall machines and pipe users on the ground (open above), the rest upstairs;
///    header rows keep their y on both floors and an item changing floors rides a lift through the floor.
/// Belts are drawn as directed segments the export already understands: T-joins become splitters / mergers, a
/// zero-length segment with a height change is a lift, a loose end next to a port connects to it.
/// </summary>
public static class Columns
{
    const double TrackPitch = 2.5;   // between parallel tracks in a corridor (neighbours sit at other heights)
    const double LiftOut = 1;        // a lift stands 1 m past the machine face and plugs into the port
    const double TrackFromLift = 2;  // lift → first track
    const double RowPitch = 4;       // header rows (and the input bank): two lifts side by side need ~4 m
    const double HeaderZ = 8;        // header rows' height (belts, above the 2 / 4 / 6 m tracks); pipes stay on the ground
    const double ColumnGap = 2;      // between two machine groups stacked in one column
    const double ColumnPitch = 32;   // a column and its corridor, roughly (sizes the column height)
    static readonly double[] TrackZ = [2, 4, 6];
    /// <summary>Track heights on one side of a column, nearest first: a ground track right by the machines when that
    /// side has no pipes (its stubs go straight into the ports; the raised tracks' stubs pass over it).</summary>
    static double[] Heights(bool pipes) => pipes ? TrackZ : [0, .. TrackZ];

    sealed class Group
    {
        public required GraphNode Node;
        public int Depth;
        public List<Placed> Machines = new();
    }
    sealed class Col
    {
        public int Index, Floor;
        public List<Group> Groups = new();
        public double X0, X1;                 // west / east face
        public double Depth;                  // east-west size (rotated machine depth)
        public List<(string item, bool fluid)> Ins = new(), Outs = new();
        public bool Tall => Groups.Any(g => Layout.HeightOf(g.Node.Building!) > 15);
        public bool Fluids => Ins.Concat(Outs).Any(i => i.fluid);
    }

    /// <summary>A few column heights (all cheap, deterministic): the smallest factory wins (ties: less belt).</summary>
    public static Layout? Build(Plan plan, Settings s, List<string>? log = null)
    {
        Layout? best = null; List<string>? bestLog = null;
        foreach (double k in new[] { 0.6, 0.75, 0.9, 1.0, 1.15, 1.3, 1.5, 1.8 })
        {
            var l = new List<string>();
            var L = BuildOnce(plan, s, k, l);
            if (L == null) continue;
            if (best == null || L.Width * L.Height < best.Width * best.Height - 1 || Math.Abs(L.Width * L.Height - best.Width * best.Height) <= 1 && L.BeltLength < best.BeltLength)
                (best, bestLog) = (L, l);
        }
        log?.AddRange(bestLog ?? ["columns: no layout"]);
        return best;
    }

    static Layout? BuildOnce(Plan plan, Settings s, double capScale, List<string>? log)
    {
        var nodes = plan.Nodes.GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.First());
        var machineNodes = nodes.Values.Where(n => n.Kind == NodeKind.Machine && n.Recipe != null && n.Machines > 0).ToList();
        if (machineNodes.Count == 0) return null;
        int floors = s.Floors >= 2 ? 2 : 1;

        // ---- production depth (longest path from the inputs) → column order
        var into = plan.Edges.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.Select(e => e.From).Distinct().ToList());
        var depth = new Dictionary<string, int>(); var busy = new HashSet<string>();
        int Depth(string k)
        {
            if (depth.TryGetValue(k, out var d)) return d;
            if (!nodes.TryGetValue(k, out var n) || n.Kind != NodeKind.Machine) return 0;
            if (!busy.Add(k)) return 1; // (a loop: cut here)
            d = 1 + (into.GetValueOrDefault(k) ?? []).Select(Depth).DefaultIfEmpty(0).Max();
            busy.Remove(k);
            return depth[k] = d;
        }
        var groups = machineNodes.Select(n => new Group { Node = n, Depth = Depth(n.Key) }).ToList();
        bool Pipes(IEnumerable<Group> gs, bool inputs) => gs.Any(g => (inputs ? g.Node.Recipe!.In : g.Node.Recipe!.Out).Any(a => GameData.Item(a.Item).IsFluid));
        int Belts(IEnumerable<Group> gs, bool inputs) => gs.SelectMany(g => (inputs ? g.Node.Recipe!.In : g.Node.Recipe!.Out).Select(a => a.Item))
            .Distinct().Count(it => !GameData.Item(it).IsFluid);
        double Len(Group g) => g.Node.Machines * Layout.Footprint(g.Node.Building!).w + ColumnGap;

        // ---- columns: groups in flow order fill a column up to a height that makes the factory about square (per floor);
        //      different steps share a column like the player's refinery column; tall machines only with tall ones
        double maxLen = groups.Max(Len), sumLen = groups.Sum(Len);
        double capH = Math.Max(maxLen, Math.Sqrt(sumLen * ColumnPitch / floors) * capScale);
        bool IsTall(Group g) => Layout.HeightOf(g.Node.Building!) > 15;
        var cols = new List<Col>();
        // (with floors, tall machines — ground only — are packed among themselves, like the player's refinery column)
        var runs = floors > 1 ? [groups.Where(IsTall).ToList(), groups.Where(g => !IsTall(g)).ToList()] : new List<List<Group>> { groups };
        var packed = new List<(int depth, List<Group> gs)>();
        foreach (var run in runs.Where(r => r.Count > 0))
        {
            var cur = new List<Group>();
            foreach (var g in run.OrderBy(g => g.Depth).ThenByDescending(Len))
            {
                var trial = cur.Append(g).ToList();
                if (cur.Count > 0 && (Belts(trial, true) > Heights(Pipes(trial, true)).Length || Belts(trial, false) > Heights(Pipes(trial, false)).Length
                                      || trial.Sum(Len) > capH + 0.1))
                {
                    packed.Add((cur.Min(v => v.Depth), cur));
                    cur = new List<Group>();
                }
                cur.Add(g);
            }
            if (cur.Count > 0) packed.Add((cur.Min(v => v.Depth), cur));
        }
        // columns west → east in flow order (by their earliest step)
        foreach (var (_, gs) in packed.OrderBy(p => p.depth)) cols.Add(new Col { Index = cols.Count, Groups = gs });
        foreach (var c in cols)
        {
            c.Outs = c.Groups.SelectMany(g => g.Node.Recipe!.Out.Select(a => a.Item)).Distinct()
                .Where(it => plan.Edges.Any(e => e.Item == it && c.Groups.Any(g => g.Node.Key == e.From)))
                .Select(it => (it, GameData.Item(it).IsFluid)).ToList();
            c.Ins = c.Groups.SelectMany(g => g.Node.Recipe!.In.Select(a => a.Item)).Distinct().Select(it => (it, GameData.Item(it).IsFluid)).ToList();
        }

        // ---- machines: rotated so inputs face west
        Placed Machine(GraphNode n, double x, double y, int rot, int floor)
        {
            var (fw, fd) = Layout.Footprint(n.Building!);
            bool odd = rot % 2 == 1;
            return new Placed("machine", n.Building!, "", x, y, odd ? fd : fw, odd ? fw : fd, n.Item, floor, n.Recipe!.ClassName) { Rot = rot };
        }
        int RotFor(GraphNode n)
        {
            for (int r = 0; r < 4; r++)
            {
                var m = Machine(n, 0, 0, r, 0);
                var ps = BlueprintExport.PortsOf(m);
                if (ps.Count == 0) return 1;
                double cx = m.W / 2;
                var ins = ps.Where(p => p.input).ToList(); var outs = ps.Where(p => !p.input).ToList();
                if (ins.Count > 0 && ins.All(p => p.x < cx - 0.5) && outs.All(p => p.x > cx + 0.5)) return r;
            }
            return 1;
        }
        var rot = machineNodes.ToDictionary(n => n.Key, RotFor);
        foreach (var c in cols) c.Depth = c.Groups.Max(g => Machine(g.Node, 0, 0, rot[g.Node.Key], 0).W);

        // ---- corridors, per floor (x from the west): distribute tracks, the column, its collect tracks
        var trackX = new Dictionary<(int col, string item, bool collect), (double x, double z)>();
        const double inputBankX = 3;  // the input bank: belts start here, on the ground floor
        // (blocked: x ranges a column may not use — upstairs, the open space above tall machines)
        double SpanOf(Col c) => c.Ins.Count(d => d.fluid) * TrackPitch + c.Ins.Count(d => !d.fluid) * TrackPitch + TrackFromLift + LiftOut
                                + c.Depth + LiftOut + TrackFromLift + c.Outs.Count * TrackPitch;
        double LayoutFloor(List<Col> fc, double x, List<(double a, double b)>? blocked = null)
        {
            trackX.Keys.Where(k => fc.Any(c => c.Index == k.col)).ToList().ForEach(k => trackX.Remove(k));
            foreach (var c in fc)
            {
                if (blocked != null)
                    for (bool moved = true; moved;)
                    {
                        moved = false;
                        double w = SpanOf(c);
                        foreach (var (a, b) in blocked)
                            if (x < b && a < x + w) { x = b + 1; moved = true; }
                    }
                var distBelts = c.Ins.Where(d => !d.fluid).ToList(); var distPipes = c.Ins.Where(d => d.fluid).ToList();
                var hIn = Heights(distPipes.Count > 0);
                foreach (var p in distPipes) { trackX[(c.Index, p.item, false)] = (x, 0); x += TrackPitch; }
                for (int k = distBelts.Count - 1; k >= 0; k--) { trackX[(c.Index, distBelts[k].item, false)] = (x, hIn[k]); x += TrackPitch; }
                x += TrackFromLift - TrackPitch + LiftOut;
                c.X0 = x; c.X1 = c.X0 + c.Depth;
                x = c.X1 + LiftOut + TrackFromLift;
                var colBelts = c.Outs.Where(d => !d.fluid).ToList(); var colPipes = c.Outs.Where(d => d.fluid).ToList();
                var hOut = Heights(colPipes.Count > 0);
                for (int k = 0; k < colBelts.Count; k++) { trackX[(c.Index, colBelts[k].item, true)] = (x, hOut[k]); x += TrackPitch; }
                foreach (var p in colPipes) { trackX[(c.Index, p.item, true)] = (x, 0); x += TrackPitch; }
            }
            return x;
        }
        // floors: tall machines / pipe users on the ground; the rest upstairs, moved down (lowest steps first) while
        // that makes the factory narrower
        double eastX;
        if (floors == 1)
            eastX = LayoutFloor(cols, inputBankX + 6);
        else
        {
            foreach (var c in cols) c.Floor = c.Tall || c.Fluids ? 0 : 1;
            double Width(out double groundEnd)
            {
                var g0 = cols.Where(c => c.Floor == 0).ToList(); var g1 = cols.Where(c => c.Floor == 1).ToList();
                groundEnd = LayoutFloor(g0, inputBankX + 6);
                // upstairs over the whole width, except the open space above the tall columns
                var blocked = g0.Where(c => c.Tall).Select(c => (c.X0 - LiftOut - 1, c.X1 + LiftOut + 1)).ToList();
                double upEnd = g1.Count > 0 ? LayoutFloor(g1, inputBankX + 6, blocked) : 0;
                return Math.Max(groundEnd, upEnd);
            }
            // every way to put the free columns (no tall machines, no pipes) on either floor: the narrowest wins
            var free = cols.Where(c => !c.Tall && !c.Fluids).ToList();
            if (free.Count <= 12)
            {
                int bestMask = 0; double bestW = double.MaxValue;
                for (int mask = 0; mask < 1 << free.Count; mask++)
                {
                    for (int i = 0; i < free.Count; i++) free[i].Floor = (mask >> i & 1) == 1 ? 1 : 0;
                    double w = Width(out _);
                    if (w < bestW - 0.1) { bestW = w; bestMask = mask; }
                }
                for (int i = 0; i < free.Count; i++) free[i].Floor = (bestMask >> i & 1) == 1 ? 1 : 0;
            }
            eastX = Width(out _);
        }

        var L = new Layout { Floors = floors, HasLevels = true };
        // storeys: tallest machine below (≤ 15 m) + ~4 m, in 4 m steps, at least 12 (hard rule §3)
        double groundTop = cols.Where(c => c.Floor == 0 && !c.Tall).SelectMany(c => c.Groups).Select(g => Layout.HeightOf(g.Node.Building!)).DefaultIfEmpty(8).Max();
        double storey = Math.Max(12, Math.Ceiling((groundTop + 4) / 4) * 4);
        L.FloorElevation = floors == 1 ? [0] : [0, storey];

        // ---- place the machines (y up from 0), groups stacked in their column
        double colTop = 0;
        foreach (var c in cols)
        {
            double y = 0;
            foreach (var g in c.Groups)
            {
                for (int m = 0; m < g.Node.Machines; m++)
                {
                    // (flush with the column's west face: every input lift stands 1 m clear of it)
                    var pl = Machine(g.Node, c.X0, y, rot[g.Node.Key], c.Floor);
                    if (m == 0) pl = pl with { Label = g.Node.Title + "\n" + g.Node.Subtitle };
                    g.Machines.Add(pl); L.Buildings.Add(pl);
                    y += pl.H;
                }
                y += ColumnGap;
            }
            colTop = Math.Max(colTop, y);
            // open above tall machines on the floors above
            if (c.Tall && floors > 1)
                foreach (var m in c.Groups.SelectMany(g => g.Machines))
                    L.Buildings.Add(new Placed("hole", "", Loc.T("layout.openAbove", GameData.Buildings.GetValueOrDefault(m.Building)?.Name ?? ""), m.X - 1, m.Y - 1, m.W + 2, m.H + 2, Floor: 1));
        }

        void Seg(int f, double x1, double y1, double z1, double x2, double y2, double z2, string item, bool fluid)
            => L.Belts.Add(new Segment(x1, y1, x2, y2, fluid, item, z1 > 0 || z2 > 0, f, item + "#1", z1, z2));
        void Lift(int f, double x1, double y1, double z1, double z2, string item, bool fluid) => Seg(f, x1, y1, z1, x1, y1, z2, item, fluid);

        // ---- machine ports → tracks
        foreach (var c in cols)
            foreach (var g in c.Groups)
                foreach (var m in g.Machines)
                {
                    var ps = BlueprintExport.PortsOf(m);
                    var inItems = g.Node.Recipe!.In.Select(a => a.Item).Distinct().ToList();
                    var outItems = g.Node.Recipe!.Out.Select(a => a.Item).Distinct().Where(it => trackX.ContainsKey((c.Index, it, true))).ToList();
                    var beltIn = ps.Where(p => p.input && !p.pipe).OrderBy(p => p.y).ToList();
                    var pipeIn = ps.Where(p => p.input && p.pipe).OrderBy(p => p.y).ToList();
                    var inBelts = inItems.Where(it => !GameData.Item(it).IsFluid).ToList();
                    var inPipes = inItems.Where(it => GameData.Item(it).IsFluid).ToList();
                    for (int i = 0; i < inBelts.Count && i < beltIn.Count; i++)
                    {
                        var (tx, tz) = trackX[(c.Index, inBelts[i], false)];
                        var p = beltIn[i];
                        double lx = c.X0 - LiftOut;
                        Seg(c.Floor, tx, p.y, tz, lx, p.y, tz, inBelts[i], false);   // off the track (a splitter there)
                        if (tz > 0) Lift(c.Floor, lx, p.y, tz, 0, inBelts[i], false); // down beside the machine
                        Seg(c.Floor, lx, p.y, 0, p.x, p.y, 0, inBelts[i], false);     // into the port
                    }
                    for (int i = 0; i < inPipes.Count && i < pipeIn.Count; i++)
                    {
                        var (tx, _) = trackX[(c.Index, inPipes[i], false)];
                        var p = pipeIn[i];
                        Seg(c.Floor, tx, p.y, 0, p.x, p.y, 0, inPipes[i], true);
                    }
                    var beltOut = ps.Where(p => !p.input && !p.pipe).OrderBy(p => p.y).ToList();
                    var pipeOut = ps.Where(p => !p.input && p.pipe).OrderBy(p => p.y).ToList();
                    var oBelts = outItems.Where(it => !GameData.Item(it).IsFluid).ToList();
                    var oPipes = outItems.Where(it => GameData.Item(it).IsFluid).ToList();
                    for (int i = 0; i < oBelts.Count && i < beltOut.Count; i++)
                    {
                        var (tx, tz) = trackX[(c.Index, oBelts[i], true)];
                        var p = beltOut[i];
                        double lx = m.X + m.W + LiftOut;
                        Seg(c.Floor, p.x, p.y, 0, lx, p.y, 0, oBelts[i], false);
                        if (tz > 0) Lift(c.Floor, lx, p.y, 0, tz, oBelts[i], false);
                        Seg(c.Floor, lx, p.y, tz, tx, p.y, tz, oBelts[i], false);     // onto the track (a merger there)
                    }
                    for (int i = 0; i < oPipes.Count && i < pipeOut.Count; i++)
                    {
                        var (tx, _) = trackX[(c.Index, oPipes[i], true)];
                        var p = pipeOut[i];
                        Seg(c.Floor, p.x, p.y, 0, tx, p.y, 0, oPipes[i], true);
                    }
                }

        // ---- every item's sources and taps: (floor, x)
        var inputItems = nodes.Values.Where(n => n.Kind is NodeKind.Raw or NodeKind.Import).Select(n => n.Item.Split('#')[0]).ToHashSet();
        var boxItems = nodes.Values.Where(n => n.Kind is NodeKind.Target or NodeKind.Surplus).GroupBy(n => n.Item.Split('#')[0]).ToDictionary(g => g.Key, g => g.First().Kind);
        var items = cols.SelectMany(c => c.Outs.Concat(c.Ins)).Distinct().ToList();
        var sources = new Dictionary<string, List<(int f, double x)>>();
        var taps = new Dictionary<string, List<(int f, double x)>>();
        foreach (var ((ci, item, collect), (tx, _)) in trackX)
            (collect ? sources : taps).TryAdd(item, new());
        foreach (var ((ci, item, collect), (tx, _)) in trackX) (collect ? sources : taps)[item].Add((cols[ci].Floor, tx));
        foreach (var it in items)
        {
            if (inputItems.Contains(it.item)) (sources.TryGetValue(it.item, out var l) ? l : sources[it.item] = new()).Add((0, inputBankX + 2));
            if (boxItems.ContainsKey(it.item)) (taps.TryGetValue(it.item, out var l) ? l : taps[it.item] = new()).Add((0, eastX + 2));
        }

        // ---- header rows: one y per item on every floor; spans that don't overlap (on any floor) share a row
        var rowY = new Dictionary<string, double>();
        var rowSpans = new List<List<(int f, double a, double b)>>();
        foreach (var it in items.OrderBy(i => Span(i.item)).ThenBy(i => i.item))
        {
            var sp = SpansOf(it.item);
            if (sp.Count == 0) continue;
            int r = rowSpans.FindIndex(row => !row.Any(v => sp.Any(w => w.f == v.f && v.a < w.b + 2 && w.a < v.b + 2)));
            if (r < 0) { rowSpans.Add(new()); r = rowSpans.Count - 1; }
            rowSpans[r].AddRange(sp);
            rowY[it.item] = colTop + 4 + r * RowPitch;
        }
        double top = colTop + 4 + rowSpans.Count * RowPitch;
        double Span(string item) => SpansOf(item).Sum(v => v.b - v.a);
        List<(int f, double a, double b)> SpansOf(string item)
        {
            var src = sources.GetValueOrDefault(item) ?? new(); var tp = taps.GetValueOrDefault(item) ?? new();
            if (src.Count == 0 || tp.Count == 0) return new();
            var main = src[0];
            if (inputItems.Contains(item)) main = (main.f, main.x + 4);
            var res = new List<(int f, double a, double b)>();
            for (int f = 0; f < floors; f++)
            {
                var xs = src.Where(v => v.f == f).Select(v => v.x).Concat(tp.Where(v => v.f == f).Select(v => v.x)).ToList();
                if (xs.Count == 0) continue;
                if (f != main.f || tp.Any(v => v.f != main.f)) xs.Add(main.x); // (the riser)
                res.Add((f, xs.Min() - 2, xs.Max() + 2));
            }
            return res;
        }

        // ---- tracks: collect tracks run north from their first machine to their row; distribute tracks south from theirs
        foreach (var ((ci, item, collect), (tx, tz)) in trackX)
        {
            if (!rowY.TryGetValue(item, out var row)) continue;
            bool fluid = GameData.Item(item).IsFluid;
            int f = cols[ci].Floor;
            var ys = L.Belts.Where(b => b.Floor == f && b.Item == item && (collect ? b.X2 == tx : b.X1 == tx) && b.Y1 == b.Y2 && Math.Abs(b.Z1 - tz) < 0.01)
                .Select(b => b.Y1).Distinct().OrderBy(v => v).ToList();
            if (ys.Count == 0) continue;
            double hz = fluid ? 0 : HeaderZ;
            if (collect)
            {
                Seg(f, tx, ys[0], tz, tx, row - 2, tz, item, fluid);
                if (!fluid && tz != hz) Lift(f, tx, row - 2, tz, hz, item, false);
                Seg(f, tx, row - 2, hz, tx, row, hz, item, fluid);
            }
            else
            {
                Seg(f, tx, row, hz, tx, row - 2, hz, item, fluid);
                if (!fluid && tz != hz) Lift(f, tx, row - 2, hz, tz, item, false);
                Seg(f, tx, row - 2, tz, tx, ys[0], tz, item, fluid);
            }
        }

        // ---- rows, inputs (west edge, one bank), products (east edge, a box each), risers between floors
        foreach (var it in items)
        {
            if (!rowY.TryGetValue(it.item, out var row)) continue;
            bool fluid = it.fluid;
            double hz = fluid ? 0 : HeaderZ;
            var src = sources[it.item]; var tp = taps[it.item];
            var main = src[0];
            double riserX = inputItems.Contains(it.item) ? main.x + 4 : main.x; // (not on an input's lift: it has one exit)
            if (inputItems.Contains(it.item))
            {
                double sx = inputBankX;
                L.Buildings.Add(new Placed("input", fluid ? Layout.InputStubPipe : Layout.InputStub, GameData.Item(it.item).Name, sx - 1, row - 1, 2, 2, it.item + "#1"));
                Seg(0, sx, row, 0, sx + 2, row, 0, it.item, fluid);
                if (!fluid) Lift(0, sx + 2, row, 0, hz, it.item, false);
            }
            for (int f = 0; f < floors; f++)
            {
                var fsrc = src.Where(v => v.f == f).Select(v => v.x).ToList();
                var ftap = tp.Where(v => v.f == f).Select(v => v.x).ToList();
                bool riserHere = floors > 1 && (f != main.f && ftap.Count > 0 || f == main.f && tp.Any(v => v.f != f));
                if (riserHere && f != main.f) fsrc.Add(riserX);      // arrives here by the riser
                if (riserHere && f == main.f) ftap.Add(riserX + 0.001); // (leaves by it: the branch below)
                if (fsrc.Count == 0 || ftap.Count == 0) continue;
                double sx = fsrc.Min();
                var east = ftap.Where(t => t > sx + 0.01).OrderBy(t => t).ToList();
                var west = ftap.Where(t => t < sx - 0.01).OrderByDescending(t => t).ToList();
                if (east.Count > 0 && east[^1] - sx > 0.5) Seg(f, sx, row, hz, east[^1], row, hz, it.item, fluid);
                if (west.Count > 0) Seg(f, sx, row, hz, west[^1], row, hz, it.item, fluid);
            }
            // a riser: 2 m north of the row where it's made, a lift through the floor, 2 m back onto the other floor's row
            if (floors > 1 && !fluid && tp.Any(v => v.f != main.f))
            {
                int other = 1 - main.f;
                Seg(main.f, riserX, row, hz, riserX, row + 2, hz, it.item, false);
                double elev = L.FloorElevation[1];
                if (main.f == 0) Lift(0, riserX, row + 2, hz, hz + elev, it.item, false);
                else Lift(0, riserX, row + 2, hz + elev, hz, it.item, false);
                Seg(other, riserX, row + 2, hz, riserX, row, hz, it.item, false);
                L.Crossings.Add((it.item + "#1", riserX, row + 2, 0, 1, false));
            }
            if (boxItems.TryGetValue(it.item, out var kind))
            {
                double bx = eastX + 2;
                if (!fluid) Lift(0, bx, row, hz, 0, it.item, false);
                Seg(0, bx, row, 0, bx + 2, row, 0, it.item, fluid);
                var bld = fluid ? (s.IndustrialFluidBox ? "Desc_IndustrialTank_C" : "Desc_PipeStorageTank_C")
                        : kind == NodeKind.Surplus && s.Sinks(it.item) ? Layout.SinkBox : "Desc_StorageContainerMk2_C";
                L.Buildings.Add(PlaceBox(kind == NodeKind.Surplus ? "surplus" : "output", bld, it.item, bx + 2, row));
            }
        }

        // ---- size: snap to foundations
        double maxX = L.Buildings.Select(b => b.X + b.W).Concat(L.Belts.Select(b => Math.Max(b.X1, b.X2))).Max() + 2;
        double maxY = Math.Max(top, L.Buildings.Select(b => b.Y + b.H).Max()) + 2;
        L.Width = Math.Ceiling(maxX / Layout.Foundation) * Layout.Foundation;
        L.Height = Math.Ceiling(maxY / Layout.Foundation) * Layout.Foundation;
        log?.Add($"columns: {cols.Count} ({string.Join(" | ", cols.Select(c => (floors > 1 ? $"F{c.Floor + 1} " : "") + string.Join(", ", c.Groups.Select(g => $"{g.Node.Machines}× {GameData.Item(g.Node.Item).Name}"))))}), {L.Width:0} × {L.Height:0} m");
        return L;
    }

    /// <summary>A box whose input port lands at (px, py), facing west (the belt arrives from the west).</summary>
    static Placed PlaceBox(string kind, string building, string item, double px, double py)
    {
        var (w, d) = Layout.Footprint(building);
        for (int r = 0; r < 4; r++)
        {
            bool odd = r % 2 == 1;
            var b = new Placed(kind, building, GameData.Item(item).Name, 0, 0, odd ? d : w, odd ? w : d, item + "#1") { Rot = r };
            var inPort = BlueprintExport.PortsOf(b).FirstOrDefault(p => p.input);
            if (inPort.name == null) continue;
            if (inPort.x < b.W / 2 - 0.5) return b with { X = px - inPort.x, Y = py - inPort.y };
        }
        return new Placed(kind, building, GameData.Item(item).Name, px, py - d / 2, w, d, item + "#1");
    }
}
