using System.Text.Json;

namespace SatisfactoryPlanner;

/// <summary>
/// Turns a <see cref="Layout"/> into blueprint specs for the writer (bp/write.js): machines turned so their inputs face
/// the input belts, the belt/pipe network rebuilt from the drawn segments (splitters where a belt branches, mergers where
/// belts join), each belt run as one conveyor snapped onto the real port positions. Game frame: centimetres, X east,
/// Y south, origin at the blueprint centre, designer floor at z = 0. The ground floor stands on 4 m foundations.
/// </summary>
public static class BlueprintExport
{
    /// <summary>Port positions in each building's own frame (cm), measured from belts/pipes attached in a real save.</summary>
    static readonly Dictionary<string, (string name, double x, double y, double z, bool input, bool pipe)[]> Ports = new()
    {
        ["Build_SmelterMk1_C"] = [("Input0", 0, -300, 100, true, false), ("Output2", 0, 200, 100, false, false)],
        ["Build_ConstructorMk1_C"] = [("Input0", 0, -300, 100, true, false), ("Output0", 0, 300, 100, false, false)],
        ["Build_AssemblerMk1_C"] = [("Input0", 200, -600, 100, true, false), ("Input1", -200, -600, 100, true, false), ("Output0", 0, 500, 100, false, false)],
        ["Build_FoundryMk1_C"] = [("Input0", 200, -300, 100, true, false), ("Input1", -200, -300, 100, true, false), ("Output2", -200, 200, 100, false, false)],
        ["Build_ManufacturerMk1_C"] = [("Input0", 600, -875, 100, true, false), ("Input1", 200, -875, 100, true, false), ("Input2", -200, -875, 100, true, false),
                                       ("Input3", -600, -875, 100, true, false), ("Output0", 0, 875, 100, false, false)],
        ["Build_OilRefinery_C"] = [("Input0", 200, 900, 100, true, false), ("PipeInputFactory", -200, 900, 175, true, true),
                                   ("Output1", 200, -900, 100, false, false), ("PipeOutputFactory", -200, -900, 175, false, true)],
        ["Build_Packager_C"] = [("Input0", 0, -300, 100, true, false), ("PipeInputFactory", 0, -380, 375, true, true),
                                ("Output1", 0, 300, 100, false, false), ("PipeOutputFactory", 0, 380, 375, false, true)],
        ["Build_StorageContainerMk2_C"] = [("Input0", 0, -400, 100, true, false), ("Output1", 0, 400, 100, false, false)],
        ["Build_ResourceSink_C"] = [("Input0", 0, 500, 100, true, false)],
        ["Build_GeneratorFuel_C"] = [("FGPipeConnectionFactory", 0, -860, 175, true, true)],
        ["Build_GeneratorCoal_C"] = [("Input0", 200, 1100, 100, true, false), ("FGPipeConnectionFactory", -200, 1142, 175, true, true)],
        ["Build_PipeStorageTank_C"] = [("ConnectionAny0", 0, 200, 175, true, true), ("ConnectionAny1", 0, -200, 175, false, true)],
        ["Build_IndustrialTank_C"] = [("ConnectionAny0", 0, 600, 175, true, true), ("ConnectionAny1", 0, -600, 175, false, true)], // (measured: pipes on one in a save)
    };

    public record Entity(string id, string cls, double x, double y, double z, double yaw, string? recipe = null, double? topZ = null, double? topYaw = null, string? fill = null, string? overflow = null); // top: conveyor lift; fill: an input box's item (a test run can fill it); overflow: a smart splitter's output set to Overflow
    public record Pt(double x, double y, double z);
    public record End(string id, string port);
    public record Link(string cls, List<Pt> pts, End? from, End? to, List<int>? curve = null); // curve: indices of legs that are S-curves
    public record Wire(string a, string b);
    public record Spec(string name, int dim, string description, string baseBlueprint, List<Entity> entities, List<Link> links, List<Wire> wires, List<Direct>? direct = null);
    /// <summary>Two parts joined port to port with no belt between (a conveyor lift right at a machine).</summary>
    public record Direct(End a, End b);

    const double FloorTop = 400, BeltZ = FloorTop + 100, PipeZ = FloorTop + 175;
    static string Build(string desc) => desc.StartsWith("Desc_") ? "Build_" + desc[5..] : desc;

    /// <summary>Where a belt / pipe was cut at a tile border: join these two ends after placing both tiles.</summary>
    public record Joint(string item, string tileA, string tileB, double x, double y, double z = 0); // x, y in foundations; z in cm (blueprint frame)

    /// <summary>
    /// Blueprints for the ground floor of the layout, one per <paramref name="dim"/>×<paramref name="dim"/>-foundation tile
    /// (tiles numbered from the south-west corner: row r, column c). Belts crossing a tile border are cut there and
    /// listed in <paramref name="joints"/>; problems (loose ends, unsupported parts) go to <paramref name="warnings"/>.
    /// </summary>
    /// <param name="inputs">Filled with where each input's belt / pipe starts (no box there: the player brings it).</param>
    public static List<Spec> FromLayout(Layout L, Settings s, string name, int dim, string baseBlueprint, List<string> warnings, List<Joint> joints, List<string>? inputs = null)
    {
        double T = dim * Layout.Foundation;
        // layout metres (x east, y north, origin SW corner) → game centimetres (X east, Y south), per tile later
        static (double X, double Y) G(double x, double y) => (x * 100, -y * 100);
        (int tx, int ty) TileOf(double X, double Y) => ((int)Math.Floor(X / 100 / T), (int)Math.Floor(-Y / 100 / T));
        string TileName((int tx, int ty) t) => $"r{t.ty + 1}c{t.tx + 1}";
        // floors: each stands at its elevation above the ground floor's top (upper floors on 1 m foundations)
        double E(int f) => f < L.FloorElevation.Count ? L.FloorElevation[f] : 0;
        // the rows layout joins its floors its own way: only its ground floor goes out (place & route: every floor)
        int floors = L.HasLevels ? L.Floors : 1;
        if (!L.HasLevels && L.Floors > 1) warnings.Add("this layout's upper floors aren't exported (only the place & route planner's are)");
        if (L.Floors > 1)
        {
            double top = FloorTop / 100 + E(L.Floors - 1) + L.Buildings.Where(b => b.Floor == L.Floors - 1 && b.Kind == "machine").Select(b => Layout.HeightOf(b.Building)).DefaultIfEmpty(8).Max();
            if (top > dim * Layout.Foundation) warnings.Add($"the floors reach {top:0} m: more than this blueprint's {dim * Layout.Foundation:0} m height");
        }

        var ents = new List<Entity>();
        var byHand = new HashSet<string>(); var hand = new List<string>(); // machines across a tile border: placed by hand
        var handBelts = new List<string>(); // bends by a tile border (machines may cross tiles): built by hand
        var links = new List<Link>();

        // ---- buildings: machines and station boxes, turned so their input side faces south (game +Y) ----
        var surplusIds = new HashSet<string>(); // overflow (surplus) boxes
        bool loop = PolymerLoop.Applies(s) && PolymerLoop.IsOn(s);
        var overflowFed = new HashSet<string>(); // machines of an overflow chain: they only get what's left over
        // an overflow merged into its product's output: the layout ends it in a sink; the merge itself is finished by hand
        foreach (var (item, h) in s.ClogHandling.Where(kv => kv.Value == OverflowChain.Merge && L.Buildings.Any(b => b.Kind == "surplus" && b.Item?.Split('#')[0] == kv.Key)))
            warnings.Add($"merge by hand: {GameData.Item(item).Name} — on its way to the AWESOME Sink, turn its splitter's other output into a merger on the {GameData.Item(GameData.BaseItem(item)).Name} output belt (the sink keeps the Overflow)");
        var portsAt = new List<(string id, string port, double X, double Y, double Z, bool input, bool pipe)>();
        int n = 0;
        foreach (var b in L.Buildings.Where(b => b.Kind != "hole" && b.Floor < floors))
        {
            double zb = FloorTop + E(b.Floor) * 100;
            if (Layout.IsInputStub(b.Building)) continue; // (an input: its belt / pipe starts here, the player brings the supply)
            var cls = Build(b.Building);
            if (!Ports.TryGetValue(cls, out var ports)) { warnings.Add($"no blueprint data for {b.Building} — skipped"); continue; }
            // machines: inputs to the south (game +Y); our boxes connect on their north side only — input boxes need
            // their output end there, output / spare boxes their input end
            double yaw = b.Kind switch
            {
                "input" => ports.First(p => !p.input).y > 0 ? 180 : 0,
                "output" or "surplus" => ports.First(p => p.input).y < 0 ? 0 : 180,
                _ => (ports.Where(p => p.input).Average(p => p.y) < 0 ? 180 : 0) + (b.Flipped ? 180 : 0),
            };
            // place & route turns cells counter-clockwise (layout frame, north up) = clockwise in the game frame (Y south)
            yaw = ((yaw - 90 * b.Rot) % 360 + 360) % 360;
            var (X, Y) = G(b.X + b.W / 2, b.Y + b.H / 2);
            string id = $"b{n++}";
            if (TileOf(G(b.X + 0.01, 0).X, 0) != TileOf(G(b.X + b.W - 0.01, 0).X, 0) || TileOf(0, G(0, b.Y + 0.01).Y) != TileOf(0, G(0, b.Y + b.H - 0.01).Y))
            {
                if (!s.HandPlaceAcrossTiles) warnings.Add($"{b.Label.Split('\n')[0]} crosses a tile border at ({b.X / 8:0.#}, {b.Y / 8:0.#})");
                else
                {
                    // left out of the blueprints: the player places it (its belts end loose next to its ports)
                    byHand.Add(id);
                    hand.Add($"{GameData.Buildings.GetValueOrDefault(b.Building)?.Name} ({GameData.Item(b.Item ?? "").Name}){(b.Floor > 0 ? $" F{b.Floor}" : "")} at foundation ({b.X / Layout.Foundation:0.##}, {b.Y / Layout.Foundation:0.##}), {yaw:0}°");
                }
            }
            // (an overflow step's recipe is its game recipe; a generator has none)
            string? recipe = b.Recipe == null || b.Recipe.StartsWith(OverflowChain.BurnPrefix) ? null : b.Recipe.Split('|')[0];
            ents.Add(new Entity(id, cls, X, Y, zb, yaw, recipe, fill: b.Kind == "input" ? b.Item?.Split('#')[0] : null));
            if (b.Kind == "machine" && b.Recipe != null && (b.Recipe.Contains('|') || b.Recipe.StartsWith(OverflowChain.BurnPrefix))) overflowFed.Add(id);
            if (b.Kind == "surplus") surplusIds.Add(id);
            // plastic / rubber recycling loop: the other side's refineries first, only the surplus leaves (never runs dry)
            else if (b.Kind == "output" && loop && b.Item?.Split('#')[0] is PolymerLoop.Plastic or PolymerLoop.Rubber) surplusIds.Add(id);
            double c = Math.Cos(yaw * Math.PI / 180), sn = Math.Sin(yaw * Math.PI / 180);
            foreach (var p in ports)
                portsAt.Add((id, p.name, X + p.x * c - p.y * sn, Y + p.x * sn + p.y * c, zb + p.z, p.input, p.pipe));
        }

        // ---- belt / pipe network from the drawn segments (ground floor) ----
        // every point has a height (metres above port height): a conveyor lift joins the same spot on two heights.
        // Split every segment where another segment of the same line ends on it (T-junctions), then join into a graph.
        var segsAll = L.Belts.Where(b => b.Floor < floors).Select(b => (a: G(b.X1, b.Y1), z: G(b.X2, b.Y2), b.Fluid, Item: b.Line ?? b.Item, b.Smooth, za: b.Z1 + E(b.Floor), zz: b.Z2 + E(b.Floor))).ToList(); // one network per production line (heights from the ground floor)
        // belts change height in conveyor lifts; pipes simply run straight up / down (a piece of pipe of its own)
        var liftSegs = segsAll.Where(q => !q.Fluid && Math.Abs(q.z.X - q.a.X) + Math.Abs(q.z.Y - q.a.Y) < 1 && Math.Abs(q.zz - q.za) > 0.1).ToList();
        var segs = segsAll.Except(liftSegs).ToList();
        static (long, long, long) K((double X, double Y) p, double h) => ((long)Math.Round(p.X / 10), (long)Math.Round(p.Y / 10), (long)Math.Round(h * 10));
        var ends = segs.SelectMany(q => new[] { (q.a, q.Item, h: q.za), (q.z, q.Item, h: q.zz) }).ToList();
        var pieces = new List<((double X, double Y) a, (double X, double Y) z, bool fluid, string item, double h, double hz)>();
        var smoothPiece = new HashSet<int>();
        foreach (var q in segs)
        {
            double len = Math.Abs(q.z.X - q.a.X) + Math.Abs(q.z.Y - q.a.Y);
            if (len < 1 && Math.Abs(q.zz - q.za) > 0.1) { pieces.Add((q.a, q.z, q.Fluid, q.Item, q.za, q.zz)); continue; } // vertical pipe
            if (len < 1) continue;
            if (Math.Abs(q.zz - q.za) > 0.1) { pieces.Add((q.a, q.z, q.Fluid, q.Item, q.za, q.zz)); continue; } // a ramp between stacked levels
            if (q.Smooth) { smoothPiece.Add(pieces.Count); pieces.Add((q.a, q.z, q.Fluid, q.Item, q.za, q.za)); continue; } // a curve is never cut
            var cuts = ends.Where(e => e.Item == q.Item && Math.Abs(e.h - q.za) < 0.1 && OnInterior(q.a, q.z, e.Item1))
                .Select(e => e.Item1).Distinct().OrderBy(p => Math.Abs(p.X - q.a.X) + Math.Abs(p.Y - q.a.Y)).ToList();
            var prev = q.a;
            foreach (var cpt in cuts) { pieces.Add((prev, cpt, q.Fluid, q.Item, q.za, q.za)); prev = cpt; }
            pieces.Add((prev, q.z, q.Fluid, q.Item, q.za, q.za));
        }
        var outs = new Dictionary<((long, long, long), string), List<int>>();
        var ins = new Dictionary<((long, long, long), string), List<int>>();
        for (int i = 0; i < pieces.Count; i++)
        {
            (outs.TryGetValue((K(pieces[i].a, pieces[i].h), pieces[i].item), out var o) ? o : outs[(K(pieces[i].a, pieces[i].h), pieces[i].item)] = new()).Add(i);
            (ins.TryGetValue((K(pieces[i].z, pieces[i].hz), pieces[i].item), out var v) ? v : ins[(K(pieces[i].z, pieces[i].hz), pieces[i].item)] = new()).Add(i);
        }
        int Deg(Dictionary<((long, long, long), string), List<int>> d, ((long, long, long), string) k) => d.TryGetValue(k, out var l) ? l.Count : 0;
        bool Pass(((long, long, long), string) k) => Deg(ins, k) == 1 && Deg(outs, k) == 1;

        // conveyor lifts: the bottom (Any0) faces the belt coming in, the top (Any1) turns towards the belt going out
        // (measured on lifts in a real save: lift yaw = incoming direction + 180°, exit = lift yaw + top yaw)
        var liftIn = new Dictionary<((long, long, long), string), (string id, Pt at)>();
        var liftOut = new Dictionary<((long, long, long), string), (string id, Pt at)>();
        foreach (var q in liftSegs)
        {
            var kin = (K(q.a, q.za), q.Item); var kout = (K(q.z, q.zz), q.Item);
            var inPiece = ins.TryGetValue(kin, out var li) ? pieces[li[0]] : default;
            var outPiece = outs.TryGetValue(kout, out var lo) ? pieces[lo[0]] : default;
            double inDir = inPiece.item != null ? Math.Atan2(Dir(inPiece.a, inPiece.z).Y, Dir(inPiece.a, inPiece.z).X) * 180 / Math.PI : 0;
            double outDir = outPiece.item != null ? Math.Atan2(Dir(outPiece.a, outPiece.z).Y, Dir(outPiece.a, outPiece.z).X) * 180 / Math.PI : inDir;
            double yaw = inDir + 180;
            string id = $"l{n++}";
            string beltCls = Build(s.BeltFor(q.Item.Split('#')[0]).cls);
            string cls = beltCls.Replace("ConveyorBelt", "ConveyorLift");
            double z0 = (q.Fluid ? PipeZ : BeltZ) + q.za * 100;
            static double Norm(double a) => ((a % 360) + 540) % 360 - 180;
            ents.Add(new Entity(id, cls, q.a.X, q.a.Y, z0, Norm(yaw), null, (q.zz - q.za) * 100, Norm(outDir - yaw)));
            liftIn[kin] = (id, new Pt(q.a.X, q.a.Y, z0));
            liftOut[kout] = (id, new Pt(q.a.X, q.a.Y, (q.Fluid ? PipeZ : BeltZ) + q.zz * 100));
        }

        // attachments where belts branch or join
        var attach = new Dictionary<((long, long, long), string), (string id, double yaw, bool splitter, bool fluid, double X, double Y, double Z)>();
        var tee = new HashSet<string>(); // pipe junctions that are T junctions
        foreach (var k in outs.Keys.Union(ins.Keys))
        {
            int di = Deg(ins, k), dout = Deg(outs, k);
            if (di >= 2 && dout >= 2) warnings.Add($"junction that both merges and splits at ({(outs.ContainsKey(k) ? pieces[outs[k][0]].a.X : 0) / 100:0}, {(outs.ContainsKey(k) ? -pieces[outs[k][0]].a.Y : 0) / 100:0})");
            if (di >= 1 && dout >= 1 && !(di == 1 && dout == 1))
            {
                var any = di > 0 ? pieces[ins[k][0]] : pieces[outs[k][0]];
                var P = di > 0 ? any.z : any.a;
                bool splitter = dout > 1;
                // splitter: its straight-through axis follows the incoming belt; merger: follows the outgoing belt
                var dir = splitter ? Dir(pieces[ins[k][0]].a, pieces[ins[k][0]].z) : Dir(pieces[outs[k][0]].a, pieces[outs[k][0]].z);
                double yaw = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
                string id = $"a{n++}";
                bool fluid = any.fluid;
                string cls = fluid ? "Build_PipelineJunction_Cross_C" : splitter ? "Build_ConveyorAttachmentSplitter_C" : "Build_ConveyorAttachmentMerger_C";
                if (fluid && di + dout == 3)
                {
                    // three pipes: a T junction — the two in line on its ±x connections, the odd one on its branch (−y)
                    var away = (ins.GetValueOrDefault(k) ?? []).Select(i => Dir(pieces[i].z, pieces[i].a))
                        .Concat((outs.GetValueOrDefault(k) ?? []).Select(i => Dir(pieces[i].a, pieces[i].z))).ToList();
                    var branch = away.FirstOrDefault(v => !away.Any(u => u != v && Math.Abs(u.X + v.X) < 0.1 && Math.Abs(u.Y + v.Y) < 0.1));
                    yaw = Math.Atan2(branch.X, -branch.Y) * 180 / Math.PI; // turn local −y onto the branch
                    cls = "Build_PipelineJunction_T_C";
                    tee.Add(id);
                }
                double z = (fluid ? PipeZ : BeltZ) + (di > 0 ? any.hz : any.h) * 100;
                ents.Add(new Entity(id, cls, P.X, P.Y, z, yaw));
                attach[k] = (id, yaw, splitter, fluid, P.X, P.Y, z);
                if ((splitter && dout > 3) || (!splitter && di > 3)) warnings.Add($"{(splitter ? "splitter" : "merger")} with too many branches at ({P.X / 100:0}, {P.Y / 100:0})");
            }
        }

        // an attachment port for a belt leaving/entering in direction d (game frame)
        End AttachPort((string id, double yaw, bool splitter, bool fluid, double X, double Y, double Z) a, (double X, double Y) d, bool leaving, out Pt at)
        {
            double r = -a.yaw * Math.PI / 180;
            double lx = d.X * Math.Cos(r) - d.Y * Math.Sin(r), ly = d.X * Math.Sin(r) + d.Y * Math.Cos(r); // direction in local frame
            string port; (double x, double y) off;
            if (a.fluid)
            {
                // measured on junctions in a real save — cross: Connection0 −x, 1 +x, 2 +y, 3 −y; T: 0 −x, 1 +x, 2 −y (branch)
                var side = leaving ? (lx, ly) : (-lx, -ly);
                if (tee.Contains(a.id))
                    (port, off) = side.Item1 > 0.7 ? ("Connection1", (100.0, 0.0)) : side.Item1 < -0.7 ? ("Connection0", (-100.0, 0.0)) : ("Connection2", (0.0, -100.0));
                else
                    (port, off) = Math.Abs(side.Item1) > Math.Abs(side.Item2)
                        ? (side.Item1 > 0 ? ("Connection1", (100.0, 0.0)) : ("Connection0", (-100.0, 0.0)))
                        : (side.Item2 > 0 ? ("Connection2", (0.0, 100.0)) : ("Connection3", (0.0, -100.0)));
            }
            else if (a.splitter)
            {
                if (!leaving) (port, off) = ("Input1", (-100, 0));
                else if (lx > 0.7) (port, off) = ("Output1", (100, 0));
                else if (ly > 0.7) (port, off) = ("Output2", (0, 100));
                else (port, off) = ("Output3", (0, -100));
            }
            else
            {
                if (leaving) (port, off) = ("Output1", (100, 0));
                else if (lx > 0.7) (port, off) = ("Input1", (-100, 0));
                else if (ly < -0.7) (port, off) = ("Input2", (0, 100));  // arriving southward in local frame = from the +y side
                else (port, off) = ("Input3", (0, -100));
            }
            double ry = a.yaw * Math.PI / 180;
            at = new Pt(a.X + off.x * Math.Cos(ry) - off.y * Math.Sin(ry), a.Y + off.x * Math.Sin(ry) + off.y * Math.Cos(ry), a.Z);
            return new End(a.id, port);
        }
        // a machine / box port at a dangling end: the nearest port of the right kind within reach
        End? MachinePort((double X, double Y) p, double z, bool intoMachine, bool fluid, out Pt at)
        {
            // (on this floor: a belt cut short by a border once snapped onto a machine two floors up — 32 m of belt straight up)
            var best = portsAt.Where(q => q.input == intoMachine && q.pipe == fluid && Math.Abs(q.Z - z) < 600)
                .OrderBy(q => Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y)).FirstOrDefault();
            at = new Pt(p.X, p.Y, fluid ? PipeZ : BeltZ);
            double ddx = Math.Abs(best.X - p.X), ddy = Math.Abs(best.Y - p.Y);
            if (best.id == null || Math.Min(ddx, ddy) > 150 || Math.Max(ddx, ddy) > 700) return null; // any orientation
            at = new Pt(best.X, best.Y, best.Z);
            return new End(best.id, best.port);
        }

        // follow runs from every start (an attachment output or a dangling source) through pass-through points
        var used = new HashSet<int>();
        foreach (var start in Enumerable.Range(0, pieces.Count).Where(i => !Pass((K(pieces[i].a, pieces[i].h), pieces[i].item))))
        {
            if (!used.Add(start)) continue;
            var first = pieces[start];
            var pts = new List<(double X, double Y)> { first.a, first.z };
            var hts = new List<double> { first.h, first.hz };
            var curveLegs = new List<int>();
            if (smoothPiece.Contains(start)) curveLegs.Add(0);
            int cur = start;
            while (true)
            {
                var k = (K(pieces[cur].z, pieces[cur].hz), pieces[cur].item);
                if (!Pass(k)) break;
                int nx = outs[k][0];
                if (!used.Add(nx)) break;
                cur = nx;
                if (smoothPiece.Contains(cur)) curveLegs.Add(pts.Count - 1);
                pts.Add(pieces[cur].z); hts.Add(pieces[cur].hz);
            }
            var last = pieces[cur];
            bool fluid = first.fluid;
            var sk = (K(first.a, first.h), first.item);
            var ek = (K(last.z, last.hz), last.item);
            End? from, to; Pt pa, pz;
            if (liftOut.TryGetValue(sk, out var lso)) { from = new End(lso.id, "ConveyorAny1"); pa = lso.at; }
            else if (attach.TryGetValue(sk, out var sa)) from = AttachPort(sa, Dir(first.a, first.z), true, out pa);
            else
            {
                from = MachinePort(first.a, (fluid ? PipeZ : BeltZ) + first.h * 100, false, fluid, out pa);
                // an input's belt / pipe starts loose on purpose (no box: the player brings the supply there)
                bool stub = L.Buildings.Any(b => Layout.IsInputStub(b.Building) && Math.Abs((b.X + b.W / 2) * 100 - first.a.X) < 600 && Math.Abs(-(b.Y + b.H / 2) * 100 - first.a.Y) < 600);
                if (from == null && stub) inputs?.Add($"{GameData.Item(first.item.Split('#')[0]).Name}: ({first.a.X / 100 / Layout.Foundation:0.#}, {-first.a.Y / 100 / Layout.Foundation:0.#})");
                else if (from == null) warnings.Add($"{GameData.Item(first.item.Split('#')[0]).Name}: loose belt start at ({first.a.X / 100:0}, {first.a.Y / 100:0})");
            }
            if (liftIn.TryGetValue(ek, out var lei)) { to = new End(lei.id, "ConveyorAny0"); pz = lei.at; }
            else if (attach.TryGetValue(ek, out var ea)) to = AttachPort(ea, Dir(last.a, last.z), false, out pz);
            else { to = MachinePort(last.z, (fluid ? PipeZ : BeltZ) + last.hz * 100, true, fluid, out pz); if (to == null) warnings.Add($"{GameData.Item(first.item.Split('#')[0]).Name}: loose belt end at ({last.z.X / 100:0}, {last.z.Y / 100:0})"); }
            // polyline: exact port / attachment positions at both ends, corners in between
            var poly = new List<Pt> { pa };
            for (int i = 1; i < pts.Count - 1; i++) poly.Add(new Pt(pts[i].X, pts[i].Y, (fluid ? PipeZ : BeltZ) + hts[i] * 100));
            poly.Add(pz);
            // keep ends axis-aligned with the next point (ports can sit a little off the drawn line)
            if (poly.Count >= 3 && !curveLegs.Contains(0) && !curveLegs.Contains(pts.Count - 2))
            {
                bool firstVertical = Math.Abs(pts[0].X - pts[1].X) < Math.Abs(pts[0].Y - pts[1].Y);
                poly[1] = firstVertical ? poly[1] with { x = poly[0].x } : poly[1] with { y = poly[0].y };
                bool lastVertical = Math.Abs(pts[^1].X - pts[^2].X) < Math.Abs(pts[^1].Y - pts[^2].Y);
                poly[^2] = lastVertical ? poly[^2] with { x = poly[^1].x } : poly[^2] with { y = poly[^1].y };
            }
            string cls = Build(s.BeltFor(first.item.Split('#')[0]).cls);
            links.Add(new Link(cls, poly, from, to, curveLegs.Count > 0 ? curveLegs : null));
        }
        // a splitter / merger right by a bend (the belt on its straight-through line turns within 1.5 m of it, before or
        // after): that port faces along the line while the belt is already turning — it can't snap in (seen in game).
        // The attachment moves onto the corner instead (a splitter then faces its incoming belt, a merger its outgoing
        // one), and every belt on it is re-seated on the port facing its own leg: legs along the move just get longer or
        // shorter, legs across it slide sideways with it. Skipped where that isn't possible (a leg with no corner after
        // it to absorb the slide, two belts on one port).
        {
            static double Dot((double x, double y) a, (double x, double y) b) => a.x * b.x + a.y * b.y;
            static (double x, double y) U(Pt a, Pt b) { double dx = b.x - a.x, dy = b.y - a.y, l = Math.Sqrt(dx * dx + dy * dy); return l < 1e-6 ? (0, 0) : (dx / l, dy / l); }
            static bool Same((double x, double y) a, (double x, double y) b) => Math.Abs(Dot(a, b) - 1) < 1e-3;
            static (double x, double y) FirstDir(List<Pt> q) { foreach (var p in q) if (Math.Abs(p.x - q[0].x) + Math.Abs(p.y - q[0].y) >= 1) return U(q[0], p); return (0, 0); }
            // index of the last point of the first leg (points on the start, or along d from it, at the same height)
            static int LegEnd(List<Pt> q, (double x, double y) d)
            {
                int k = 1;
                while (k < q.Count && Math.Abs(q[k].z - q[0].z) < 1 && (Math.Abs(q[k].x - q[0].x) + Math.Abs(q[k].y - q[0].y) < 1 || Same(U(q[0], q[k]), d))) k++;
                return k - 1;
            }
            static List<Pt> Rev(List<Pt> q) { var r = new List<Pt>(q); r.Reverse(); return r; }
            int fixedN = 0;
            foreach (var (key, a) in attach.ToList())
            {
                if (a.fluid) continue;
                int ei = ents.FindIndex(e => e.id == a.id);
                if (ei < 0) continue;
                double ry = a.yaw * Math.PI / 180; var u = (x: Math.Cos(ry), y: Math.Sin(ry));
                // every belt on it, walked away from the attachment (outputs as they are, inputs reversed)
                var on = links.Select((l, i) => (l, i, leaving: l.from?.id == a.id)).Where(q => q.leaving || q.l.to?.id == a.id)
                    .Select(q => (q.l, q.i, q.leaving, away: q.leaving ? q.l.pts : Rev(q.l.pts))).ToList();
                // the through-line belt that bends right away: a splitter's input (behind, -u) or Output1 (ahead, +u); a
                // merger's Input1 (behind) or output (ahead)
                foreach (var side in new[] { 1, -1 })
                {
                    // (by port: a belt turning right at the port has no leg along the line at all)
                    var bent = on.FirstOrDefault(q => a.splitter ? (side > 0 ? q.leaving && q.l.from!.port == "Output1" : !q.leaving)
                                                                 : (side > 0 ? q.leaving : !q.leaving && q.l.to!.port == "Input1"));
                    if (bent.l == null) continue;
                    var aw = bent.away; var ax = (side * u.x, side * u.y);
                    int e0 = LegEnd(aw, ax);
                    double straight = Math.Sqrt(Math.Pow(aw[e0].x - aw[0].x, 2) + Math.Pow(aw[e0].y - aw[0].y, 2));
                    if (e0 >= aw.Count - 1 || straight >= 150 || Math.Abs(aw[e0 + 1].z - aw[e0].z) > 1) continue;
                    var v = U(aw[e0], aw[e0 + 1]); // the bent belt's leg past the corner (away from the attachment)
                    if (Math.Abs(Dot(v, u)) > 1e-3) continue;
                    var C = aw[e0];
                    var delta = (x: C.x - a.X, y: C.y - a.Y);
                    // new facing: a splitter along its incoming belt, a merger along its outgoing belt
                    double yaw = a.yaw;
                    if (a.splitter && side < 0) yaw = Math.Atan2(-v.y, -v.x) * 180 / Math.PI;   // input now arrives heading -v
                    if (!a.splitter && side > 0) yaw = Math.Atan2(v.y, v.x) * 180 / Math.PI;     // output now leaves along v
                    var tmp = (a.id, yaw, a.splitter, a.fluid, a.X + delta.x, a.Y + delta.y, a.Z);
                    var edits = new Dictionary<int, Link>(); var ports = new HashSet<string>(); bool ok = true;
                    foreach (var q in on)
                    {
                        List<Pt> w;
                        (double x, double y) d;
                        if (q.i == bent.i)
                        {
                            // from the corner on: its leg past the corner (the stub before it goes)
                            w = aw.Skip(e0 + 1).ToList();
                            w.Insert(0, C with { x = C.x + v.x * 100, y = C.y + v.y * 100 });
                            if (w.Count > 1 && Math.Abs(w[1].x - w[0].x) + Math.Abs(w[1].y - w[0].y) < 1) w.RemoveAt(1);
                            if (w.Count > 1 && !Same(U(w[0], w[1]), v)) { ok = false; break; } // (the leg past the corner is under 1 m)
                            d = v;
                        }
                        else
                        {
                            w = new List<Pt>(q.away); d = FirstDir(w);
                            double dm = Math.Sqrt(Dot(delta, delta));
                            if (Math.Abs(Dot(d, delta)) < 1e-3 * dm)
                            {
                                // a leg across the move slides sideways with it — on through ramps and straight runs — up to the
                                // first leg along the move, which absorbs it (gets longer or shorter)
                                var dn = (delta.x / dm, delta.y / dm);
                                int e = 0;
                                while (e < w.Count - 1)
                                {
                                    double hx = w[e + 1].x - w[e].x, hy = w[e + 1].y - w[e].y, hl = Math.Sqrt(hx * hx + hy * hy);
                                    if (hl >= 1 && Math.Abs(Math.Abs((hx * dn.Item1 + hy * dn.Item2) / hl) - 1) < 1e-3) break; // along the move
                                    if (hl >= 1 && Math.Abs((hx * dn.Item1 + hy * dn.Item2) / hl) > 1e-3) { e = w.Count; break; } // (diagonal)
                                    e++;
                                }
                                if (e >= w.Count - 1) { ok = false; break; }
                                for (int k = 0; k <= e; k++) w[k] = w[k] with { x = w[k].x + delta.x, y = w[k].y + delta.y };
                            }
                            // (a leg along the move: its end just moves along it)
                        }
                        var dir = q.leaving ? d : (-d.x, -d.y);
                        var end = AttachPort(tmp, dir, q.leaving, out var at);
                        if (!ports.Add(end.port)) { ok = false; break; }
                        w[0] = at with { z = w[0].z };
                        while (w.Count > 2 && Math.Abs(w[1].x - w[0].x) + Math.Abs(w[1].y - w[0].y) < 1 && Math.Abs(w[1].z - w[0].z) < 1) w.RemoveAt(1); // (a doubled point)
                        // the port must lie on the belt's first leg, facing along it
                        if (w.Count > 1 && !Same(U(w[0], w[1]), d)) { ok = false; break; }
                        var pts = q.leaving ? w : Rev(w);
                        edits[q.i] = q.leaving ? q.l with { pts = pts, from = end } : q.l with { pts = pts, to = end };
                    }
                    if (!ok)
                    {
                        // the other way round: the attachment stays, the turning belt slides back so its corner is at the
                        // attachment's centre (up to its next leg along the line, which absorbs it); the attachment only
                        // turns to face its belts, whose ports stay where they are
                        tmp = (a.id, yaw, a.splitter, a.fluid, a.X, a.Y, a.Z);
                        edits.Clear(); ports.Clear(); ok = true;
                        var back = (x: -delta.x, y: -delta.y); double bm = Math.Sqrt(Dot(back, back));
                        var bn = (back.x / bm, back.y / bm);
                        foreach (var q in on)
                        {
                            List<Pt> w; (double x, double y) d;
                            if (q.i == bent.i)
                            {
                                w = aw.Skip(e0 + 1).ToList();
                                int e = 0;
                                while (e < w.Count - 1)
                                {
                                    double hx = w[e + 1].x - w[e].x, hy = w[e + 1].y - w[e].y, hl = Math.Sqrt(hx * hx + hy * hy);
                                    if (hl >= 1 && Math.Abs(Math.Abs((hx * bn.Item1 + hy * bn.Item2) / hl) - 1) < 1e-3) break;
                                    if (hl >= 1 && Math.Abs((hx * bn.Item1 + hy * bn.Item2) / hl) > 1e-3) { e = w.Count; break; }
                                    e++;
                                }
                                if (e >= w.Count - 1) { ok = false; break; }
                                for (int k = 0; k <= e; k++) w[k] = w[k] with { x = w[k].x + back.x, y = w[k].y + back.y };
                                w.Insert(0, new Pt(a.X + v.x * 100, a.Y + v.y * 100, aw[0].z));
                                if (w.Count > 1 && Math.Abs(w[1].x - w[0].x) + Math.Abs(w[1].y - w[0].y) < 1) w.RemoveAt(1);
                                d = v;
                            }
                            else { w = new List<Pt>(q.away); d = FirstDir(w); }
                            var dir = q.leaving ? d : (-d.x, -d.y);
                            var end = AttachPort(tmp, dir, q.leaving, out var at);
                            if (!ports.Add(end.port) || Math.Abs(at.x - w[0].x) + Math.Abs(at.y - w[0].y) > 1) { ok = false; break; }
                            while (w.Count > 2 && Math.Abs(w[1].x - w[0].x) + Math.Abs(w[1].y - w[0].y) < 1 && Math.Abs(w[1].z - w[0].z) < 1) w.RemoveAt(1);
                            if (w.Count > 1 && !Same(U(w[0], w[1]), d)) { ok = false; break; }
                            var pts = q.leaving ? w : Rev(w);
                            edits[q.i] = q.leaving ? q.l with { pts = pts, from = end } : q.l with { pts = pts, to = end };
                        }
                        if (!ok) continue;
                        delta = (0, 0);
                    }
                    foreach (var (i, l) in edits) links[i] = l;
                    ents[ei] = ents[ei] with { x = a.X + delta.x, y = a.Y + delta.y, yaw = yaw };
                    attach[key] = tmp;
                    fixedN++;
                    break;
                }
            }
            if (fixedN > 0 && Environment.GetEnvironmentVariable("PNR_DEBUG") == "1") Console.WriteLine($"export: {fixedN} splitter(s) / merger(s) moved onto the bend by them");
        }
        // a conveyor lift right by a machine (1 m past its edge, as built in game) plugs straight into the port: the short
        // belt the layout draws between them goes
        var direct = new List<Direct>();
        {
            var byId = ents.ToDictionary(e => e.id);
            bool Machine(End? e) => e != null && byId.TryGetValue(e.id, out var m) && Ports.ContainsKey(m.cls) && !m.cls.Contains("Storage");
            bool Lift(End? e) => e != null && byId.TryGetValue(e.id, out var m) && m.cls.Contains("ConveyorLift");
            for (int i = links.Count - 1; i >= 0; i--)
            {
                var l = links[i];
                if (l.cls.Contains("Pipeline") || l.pts.Count > 3) continue;
                double len = 0; for (int k = 1; k < l.pts.Count; k++) len += Math.Abs(l.pts[k].x - l.pts[k - 1].x) + Math.Abs(l.pts[k].y - l.pts[k - 1].y) + Math.Abs(l.pts[k].z - l.pts[k - 1].z);
                // only a lift standing right against the machine (its centre 1 m out): further out it needs its belt — once
                // a lift 2 m out (and in the next blueprint tile) lost its refinery input altogether (seen in game)
                if (len > 120) continue;
                if (T > 0 && TileOf(l.pts[0].x, l.pts[0].y) != TileOf(l.pts[^1].x, l.pts[^1].y)) continue;
                if ((Machine(l.from) && Lift(l.to)) || (Lift(l.from) && Machine(l.to)))
                {
                    direct.Add(new Direct(l.from!, l.to!));
                    links.RemoveAt(i);
                }
            }
        }
        // the last splitter before an overflow box is a smart splitter with that output on Overflow (the others Any): the
        // overflow only takes what the machines don't (user, 2026-09-24)
        {
            int smart = 0;
            foreach (var (sid, first) in surplusIds.Select(i => (i, links.FirstOrDefault(l => l.to?.id == i)))
                         .Concat(overflowFed.SelectMany(i => links.Where(l => l.to?.id == i && !l.cls.Contains("Pipeline")).Select(l => (i, (Link?)l)))))
            {
                var cur = first;
                for (int guard = 0; cur?.from != null && guard < 50; guard++)
                {
                    var f = cur.from;
                    int ei = ents.FindIndex(e => e.id == f.id);
                    if (ei < 0) break;
                    if (ents[ei].cls.Contains("ConveyorLift")) { cur = links.FirstOrDefault(l => l.to?.id == f.id); continue; } // up / down a lift
                    if (ents[ei].cls == "Build_ConveyorAttachmentSplitter_C") { ents[ei] = ents[ei] with { cls = "Build_ConveyorAttachmentSplitterSmart_C", overflow = f.port }; smart++; }
                    break;
                }
            }
            if (smart > 0 && Environment.GetEnvironmentVariable("PNR_DEBUG") == "1") Console.WriteLine($"export: {smart} overflow splitter(s) made smart");
        }
        // every port takes one belt / pipe (two on one port: a belt snapped onto the wrong building)
        foreach (var gp in links.SelectMany(l => new[] { l.from, l.to }).Concat(direct.SelectMany(d => new[] { d.a, d.b }))
                     .Where(e => e != null).GroupBy(e => (e!.id, e.port)).Where(g => g.Count() > 1))
            warnings.Add($"{gp.Count()} belts / pipes on one port ({gp.Key.id}.{gp.Key.port})");
        // belts / pipes crossing each other at the same height (away from their ends) collide in game
        {
            var legs = new List<(int link, Pt a, Pt z)>();
            for (int i = 0; i < links.Count; i++)
                for (int k = 1; k < links[i].pts.Count; k++)
                    if (Math.Abs(links[i].pts[k].z - links[i].pts[k - 1].z) < 1) legs.Add((i, links[i].pts[k - 1], links[i].pts[k]));
            var hs = legs.Where(q => Math.Abs(q.a.y - q.z.y) < 1 && Math.Abs(q.a.x - q.z.x) > 1).ToList();
            var vs = legs.Where(q => Math.Abs(q.a.x - q.z.x) < 1 && Math.Abs(q.a.y - q.z.y) > 1).ToList();
            foreach (var h in hs)
                foreach (var v in vs)
                {
                    if (h.link == v.link || Math.Abs(h.a.z - v.a.z) > 50) continue;
                    double x = v.a.x, y = h.a.y;
                    if (x > Math.Min(h.a.x, h.z.x) + 50 && x < Math.Max(h.a.x, h.z.x) - 50 && y > Math.Min(v.a.y, v.z.y) + 50 && y < Math.Max(v.a.y, v.z.y) - 50)
                        warnings.Add($"{links[h.link].cls.Replace("Build_", "")} and {links[v.link].cls.Replace("Build_", "")} cross at the same height at ({x / 100:0}, {-y / 100:0})");
                }
        }
        // solid parts must not overlap (a lift or splitter inside another launches whoever stands there)
        {
            var small = ents.Where(e => e.cls.Contains("ConveyorLift") || e.cls.Contains("Attachment") || e.cls.Contains("PipelineJunction")).ToList();
            for (int i = 0; i < small.Count; i++)
                for (int j = i + 1; j < small.Count; j++)
                    if (Math.Abs(small[i].x - small[j].x) < 190 && Math.Abs(small[i].y - small[j].y) < 190 && Math.Abs(small[i].z - small[j].z) < 150)
                        warnings.Add($"{small[i].cls.Replace("Build_", "")} and {small[j].cls.Replace("Build_", "")} overlap at ({small[i].x / 100:0}, {-small[i].y / 100:0})");
            int FloorAt(double z) => Enumerable.Range(0, Math.Max(1, L.Floors)).Last(f => f == 0 || FloorTop + E(f) * 100 <= z + 1);
            foreach (var lf in small.Where(e => e.cls.Contains("ConveyorLift")))
                foreach (var b in L.Buildings.Where(b => b.Kind == "machine" && b.Floor == FloorAt(lf.z)))
                    if (lf.x / 100 > b.X - 0.9 && lf.x / 100 < b.X + b.W + 0.9 && -lf.y / 100 > b.Y - 0.9 && -lf.y / 100 < b.Y + b.H + 0.9)
                        warnings.Add($"lift inside a machine at ({lf.x / 100:0}, {-lf.y / 100:0})");
        }
        // pipes: fluid only rises so far on its own — flag every run that climbs more than 5 m above where it starts
        foreach (var l in links.Where(l => l.cls.Contains("Pipeline")))
        {
            double rise = (l.pts.Max(p => p.z) - l.pts[0].z) / 100;
            if (rise > 10) warnings.Add($"pipe rises {rise:0.#} m — needs a pump");
            else if (rise > 5) warnings.Add($"pipe rises {rise:0.#} m — risky without a pump");
        }
        // ---- levels: belts along the rows (east–west) stay at port height; a north–south run that crosses another belt
        //      rises to the crossing level over the crossings (ramps next to its ends, i.e. right by the splitter /
        //      machine port it serves), or above the machine where it passes over one ----
        var boxes = L.Buildings.Where(b => b.Floor == 0).Select(b =>
        {
            var (x0, y0) = G(b.X, b.Y + b.H); var (x1, y1) = G(b.X + b.W, b.Y);
            return (x0, x1, y0, y1, top: FloorTop + (b.Kind == "machine" ? Layout.HeightOf(b.Building) : 8) * 100);
        }).ToList();
        var hlegs = new List<(int link, double y, double x0, double x1)>();
        for (int i = 0; i < links.Count; i++)
            for (int k = 1; k < links[i].pts.Count; k++)
            {
                var a = links[i].pts[k - 1]; var z = links[i].pts[k];
                if (Math.Abs(a.y - z.y) < 1 && Math.Abs(a.x - z.x) > 1) hlegs.Add((i, a.y, Math.Min(a.x, z.x), Math.Max(a.x, z.x)));
            }
        // 2 m over the crossed belt, ramps at ≤ 35° where there's room; where a crossing sits right by a splitter or
        // port the ramp may steepen to 45° (the game only limits each belt's end-to-end slope, and belts may intersect)
        const double Rise = 200, Slope = 0.7, MaxSlope = 1.0;
        int overMachine = 0, steep = 0, nearBorder = 0;
        for (int i = 0; i < links.Count && !L.HasLevels; i++) // (place & route layouts carry their own heights)
        {
            var pts = links[i].pts;
            var outp = new List<Pt> { pts[0] };
            for (int k = 1; k < pts.Count; k++)
            {
                var a = pts[k - 1]; var z = pts[k];
                bool vertical = Math.Abs(a.x - z.x) < 1 && Math.Abs(a.y - z.y) > 1;
                if (!vertical) { outp.Add(z); continue; }
                double len = Math.Abs(z.y - a.y), sgn = Math.Sign(z.y - a.y);
                double baseZ = Math.Max(a.z, z.z);
                // what has to be cleared along this leg: (from, to, height) in cm along the leg
                var need = new List<(double t0, double t1, double h)>();
                foreach (var h in hlegs)
                    if (h.link != i && a.x > h.x0 + 1 && a.x < h.x1 - 1 && (h.y - a.y) * sgn > 1 && (z.y - h.y) * sgn > 1)
                    {
                        double t = Math.Abs(h.y - a.y);
                        need.Add((t - 100, t + 100, baseZ + Rise));
                    }
                bool Inside((double x0, double x1, double y0, double y1, double top) r, Pt q) => q.x > r.x0 - 1 && q.x < r.x1 + 1 && q.y > r.y0 - 1 && q.y < r.y1 + 1;
                foreach (var bx in boxes)
                    if (!Inside(bx, a) && !Inside(bx, z) && a.x > bx.x0 + 1 && a.x < bx.x1 - 1 && Math.Max(Math.Min(a.y, z.y), bx.y0) < Math.Min(Math.Max(a.y, z.y), bx.y1))
                    {
                        double t0 = Math.Abs(Math.Clamp(sgn > 0 ? bx.y0 : bx.y1, Math.Min(a.y, z.y), Math.Max(a.y, z.y)) - a.y);
                        double t1 = Math.Abs(Math.Clamp(sgn > 0 ? bx.y1 : bx.y0, Math.Min(a.y, z.y), Math.Max(a.y, z.y)) - a.y);
                        need.Add((Math.Min(t0, t1), Math.Max(t0, t1), bx.top + 150));
                        overMachine++;
                    }
                if (need.Count == 0) { outp.Add(z); continue; }
                // humps: crossings close together share one; a tile border between them splits it, and no hump
                // (ramps included) may reach within 1.5 m of a border — the belt is cut there and bridged by hand
                var borders = new List<double>();
                for (double k2 = Math.Floor(Math.Min(-a.y, -z.y) / 100 / T) + 1; k2 * T * 100 < Math.Max(-a.y, -z.y); k2++)
                    borders.Add(Math.Abs(-k2 * T * 100 - a.y));
                var humps = new List<(double t0, double t1, double h)>();
                foreach (var q in need.OrderBy(q => q.t0))
                {
                    if (humps.Count > 0)
                    {
                        var last = humps[^1];
                        bool border = borders.Any(bt => bt > last.t1 && bt < q.t0);
                        double gapNeeded = (Math.Max(last.h, q.h) - baseZ) / Slope * 2;
                        if (!border && q.t0 - last.t1 < gapNeeded) { humps[^1] = (last.t0, Math.Max(last.t1, q.t1), Math.Max(last.h, q.h)); continue; }
                    }
                    humps.Add(q);
                }
                Pt At(double t, double zz) => new(a.x, a.y + sgn * t, zz);
                foreach (var hp in humps)
                {
                    double top = hp.h, t1s = Math.Max(0, hp.t0), t2s = Math.Min(len, hp.t1);
                    double ramp = (top - baseZ) / Slope;
                    double up = Math.Max(0, t1s - ramp), down = Math.Min(len, t2s + ramp);
                    // keep off the borders: shorten the ramp towards a border (steeper), never cross it
                    foreach (var bt in borders)
                    {
                        if (bt < t1s && bt > up - 150) up = Math.Min(t1s, bt + 150);
                        if (bt > t2s && bt < down + 150) down = Math.Max(t2s, bt - 150);
                        if (bt >= t1s - 150 && bt <= t2s + 150) nearBorder++;
                    }
                    if ((t1s - up) * MaxSlope < top - baseZ - 1 || (down - t2s) * MaxSlope < top - baseZ - 1)
                    {
                        steep++;
                        if (Environment.GetEnvironmentVariable("BP_DEBUG") == "1")
                            warnings.Add($"steep: leg {len / 100:0.#} m at x {a.x / 100:0}, clear {t1s / 100:0.#}–{t2s / 100:0.#} m, rise {(top - baseZ) / 100:0.#} m");
                    }
                    if (up > 1) outp.Add(At(up, a.z));
                    outp.Add(At(Math.Max(t1s, up + 1), top));
                    outp.Add(At(Math.Min(t2s, down - 1), top));
                    if (down < len - 1) outp.Add(At(down, z.z));
                }
                outp.Add(z);
            }
            links[i] = links[i] with { pts = outp };
        }
        if (overMachine > 0) warnings.Add($"{overMachine} belt run(s) pass over a machine — they need a third, high level");
        if (nearBorder > 0) warnings.Add($"{nearBorder} belt crossing(s) sit on a tile border — the raised belt there has to be rebuilt by hand");
        if (steep > 0) warnings.Add($"{steep} belt ramp(s) steeper than 45° (a crossing too close to the end of a run)");

        // ---- split into tiles: buildings by their centre, belts cut where they cross a border ----
        var specs = new Dictionary<(int tx, int ty), (List<Entity> e, List<Link> l)>();
        (List<Entity> e, List<Link> l) Of((int, int) t) => specs.TryGetValue(t, out var v) ? v : specs[t] = (new(), new());
        foreach (var e in ents.Where(e => !byHand.Contains(e.id))) Of(TileOf(e.x, e.y)).e.Add(e);
        // each machine placed by hand takes power from one tile it touches (the one it covers most): a slot kept there
        var handPower = new Dictionary<(int, int), List<int>>(); // tile → the power floor of each machine placed by hand
        foreach (var b in L.Buildings.Where(b => b.Kind == "machine" && b.Floor < floors && s.HandPlaceAcrossTiles && Layout.CrossesTile(b, T)))
        {
            var pt = Layout.PowerTile(b, T);
            if (!handPower.TryGetValue(pt, out var hl)) handPower[pt] = hl = new();
            hl.Add(b.Building == "Desc_OilRefinery_C" && b.Floor + 1 < floors ? b.Floor + 1 : b.Floor);
            Of(pt); // (a tile with only hand machines nearby still gets its outlets)
        }
        if (hand.Count > 0) warnings.Add($"place by hand ({hand.Count}): " + string.Join("; ", hand));
        End? Keep(End? e) => e != null && byHand.Contains(e.id) ? null : e; // a belt to a machine placed by hand ends loose
        foreach (var l in links)
        {
            // split the polyline at every border crossing (all legs are axis-aligned)
            var parts = new List<List<Pt>> { new() { l.pts[0] } };
            var partCurves = new List<List<int>> { new() }; // curved legs of each part (indices within the part)
            for (int i = 1; i < l.pts.Count; i++)
            {
                var a = l.pts[i - 1]; var z = l.pts[i];
                if (l.curve != null && l.curve.Contains(i - 1)) partCurves[^1].Add(parts[^1].Count - 1); // curves never cross a border
                var cuts = new List<Pt>();
                double ta = Math.Floor(a.x / 100 / T), tz = Math.Floor(z.x / 100 / T);
                for (double k = Math.Min(ta, tz) + 1; k <= Math.Max(ta, tz); k++) cuts.Add(a with { x = k * T * 100 });
                double ua = Math.Floor(-a.y / 100 / T), uz = Math.Floor(-z.y / 100 / T);
                for (double k = Math.Min(ua, uz) + 1; k <= Math.Max(ua, uz); k++) cuts.Add(a with { y = -k * T * 100 });
                foreach (var c in cuts.OrderBy(c => Math.Abs(c.x - a.x) + Math.Abs(c.y - a.y)))
                {
                    var cut = new Pt(Math.Abs(a.x - z.x) < 1 ? a.x : c.x, Math.Abs(a.y - z.y) < 1 ? a.y : c.y, a.z + (z.z - a.z) * 0.5);
                    parts[^1].Add(cut);
                    parts.Add(new() { cut }); partCurves.Add(new());
                }
                parts[^1].Add(z);
            }
            for (int p = 0; p < parts.Count; p++)
            {
                var pts = parts[p];
                if (pts.Count < 2) continue;
                var mid = new Pt((pts[0].x + pts[1].x) / 2, (pts[0].y + pts[1].y) / 2, 0);
                var t = TileOf(mid.x, mid.y);
                // belts stop 1 m short of a tile border on both sides: placed blueprints never connect to each other,
                // so the 2 m gap is bridged by hand after placing (flat there — humps keep off the borders)
                var body = pts;
                // machines may cross tiles (no border zones): a belt / pipe that bends right by the border is finished by
                // hand there — this piece stops 3 m short of the bend (straight), the bend itself is left out
                if (s.HandPlaceAcrossTiles)
                {
                    if (p < parts.Count - 1 && body.Count > 2 && Leg(body, body.Count - 2) < 300 && Leg(body, body.Count - 3) > 100)
                    {
                        var corner = body[^2];
                        body = [.. body.Take(body.Count - 2), Along(corner, body[^3], Math.Min(300, Leg(body, body.Count - 3) - 50))];
                        handBelts.Add($"{(l.cls.Contains("Pipeline") ? "pipe" : "belt")} at ({(corner.x) / 100 / Layout.Foundation:0.#}, {-corner.y / 100 / Layout.Foundation:0.#})");
                    }
                    if (p > 0 && body.Count > 2 && Leg(body, 0) < 300 && Leg(body, 1) > 100)
                    {
                        var corner = body[1];
                        body = [Along(corner, body[2], Math.Min(300, Leg(body, 1) - 50)), .. body.Skip(2)];
                        handBelts.Add($"{(l.cls.Contains("Pipeline") ? "pipe" : "belt")} at ({(corner.x) / 100 / Layout.Foundation:0.#}, {-corner.y / 100 / Layout.Foundation:0.#})");
                    }
                }
                double back = l.cls.Contains("Pipeline") ? 200 : 100; // a pipe's end fitting reaches past its end point
                // a short piece still gets trimmed (at least 1 m for pipes), keeping 50 cm of pipe
                // keep 1.5 m of pipe after a bend (a shorter leg pinches the bend), else 0.5 m; pipes still end ≥ 1 m back
                double Trim(double leg) => Math.Min(back, leg - (l.cls.Contains("Pipeline") && body.Count > 2 ? 150 : 50));
                if (p > 0 && Trim(Leg(body, 0)) >= back / 2) body = [Along(body[0], body[1], Trim(Leg(body, 0))), .. body.Skip(1)];
                if (p < parts.Count - 1 && Trim(Leg(body, body.Count - 2)) >= back / 2) body = [.. body.Take(body.Count - 1), Along(body[^1], body[^2], Trim(Leg(body, body.Count - 2)))];
                // a piece cut down to nothing (a port right at the border — machines crossing tiles have no border zones): no
                // zero-length belt / pipe in the blueprint (the game can't build one); that port is joined by hand
                double bodyLen = 0; for (int k = 1; k < body.Count; k++) bodyLen += Math.Abs(body[k].x - body[k - 1].x) + Math.Abs(body[k].y - body[k - 1].y) + Math.Abs(body[k].z - body[k - 1].z);
                if (bodyLen < 50)
                {
                    var at = body[0];
                    warnings.Add($"join by hand: a {(l.cls.Contains("Pipeline") ? "pipe" : "belt")} port right at a tile border ({at.x / 100 / Layout.Foundation:0.#}, {-at.y / 100 / Layout.Foundation:0.#})");
                }
                else
                Of(t).l.Add(new Link(l.cls, body, p == 0 ? Keep(l.from) : null, p == parts.Count - 1 ? Keep(l.to) : null, partCurves[p].Count > 0 ? partCurves[p] : null));
                if (p > 0)
                {
                    var prevMid = new Pt((parts[p - 1][^2].x + parts[p - 1][^1].x) / 2, (parts[p - 1][^2].y + parts[p - 1][^1].y) / 2, 0);
                    joints.Add(new Joint(l.cls, TileName(TileOf(prevMid.x, prevMid.y)), TileName(t), pts[0].x / 100 / Layout.Foundation, -pts[0].y / 100 / Layout.Foundation, pts[0].z));
                }
            }
        }
        if (handBelts.Count > 0) warnings.Add($"finish by hand ({handBelts.Count} bends by a tile border): " + string.Join("; ", handBelts.Distinct()));
        var result = new List<Spec>();
        foreach (var (t, (e, l)) in specs.OrderBy(kv => kv.Key.ty).ThenBy(kv => kv.Key.tx))
        {
            // tile frame: origin at the tile centre; 4 m foundations under the whole tile
            double ox = (t.tx * T + T / 2) * 100, oy = -(t.ty * T + T / 2) * 100;
            var te = e.Select(v => v with { x = v.x - ox, y = v.y - oy }).ToList();
            for (int i = 0; i < dim; i++)
                for (int j = 0; j < dim; j++)
                    te.Insert(0, new Entity($"fnd{t.tx}_{t.ty}_{i}_{j}", "Build_Foundation_8x4_01_C", (i * 8 + 4 - T / 2) * 100, -(j * 8 + 4 - T / 2) * 100, FloorTop / 2, 0));
            // upper floors: 1 m foundations, none over a hole (a tall machine below reaches through); a passthrough
            // where a lift / pipe goes through the floor
            for (int f = 1; f < floors; f++)
            {
                double fz = FloorTop + E(f) * 100 - 50;
                var holes = L.Buildings.Where(b => b.Floor == f && b.Kind == "hole").ToList();
                for (int i = 0; i < dim; i++)
                    for (int j = 0; j < dim; j++)
                    {
                        double x0 = t.tx * T + i * 8, y0 = t.ty * T + j * 8; // layout metres
                        if (holes.Any(h => h.X < x0 + 8 - 0.01 && h.X + h.W > x0 + 0.01 && h.Y < y0 + 8 - 0.01 && h.Y + h.H > y0 + 0.01)) continue;
                        te.Insert(0, new Entity($"fnd{f}_{t.tx}_{t.ty}_{i}_{j}", "Build_Foundation_8x1_01_C", (i * 8 + 4 - T / 2) * 100, -(j * 8 + 4 - T / 2) * 100, fz, 0));
                    }
                foreach (var cr in L.Crossings.Where(cr => cr.lo < f && f <= cr.hi))
                {
                    var (cx, cy) = G(cr.x, cr.y);
                    if (TileOf(cx, cy) != t) continue;
                    te.Add(new Entity($"pt{f}_{te.Count}", cr.fluid ? "Build_FoundationPassthrough_Pipe_C" : "Build_FoundationPassthrough_Lift_C", cx - ox, cy - oy, fz, 0));
                }
            }
            var tl = l.Select(v => v with { pts = v.pts.Select(q => q with { x = q.x - ox, y = q.y - oy }).ToList() }).ToList();
            // a belt / pipe whose end part sits in the neighbouring tile (a splitter right on the border — no border
            // zones when machines may cross tiles) ends loose here: joined by hand
            var here = te.Select(v => v.id).ToHashSet();
            for (int i = 0; i < tl.Count; i++)
            {
                var v = tl[i];
                bool badFrom = v.from != null && !here.Contains(v.from.id), badTo = v.to != null && !here.Contains(v.to.id);
                if (!badFrom && !badTo) continue;
                warnings.Add($"tile {TileName(t)}: a {(v.cls.Contains("Pipeline") ? "pipe" : "belt")} ends loose by the border (its connection is in the next tile) — join by hand");
                tl[i] = v with { from = badFrom ? null : v.from, to = badTo ? null : v.to };
            }
            var tw = new List<Wire>();
            Power(t, te, tl, tw, ox, oy);
            var ids = te.Select(v => v.id).ToHashSet();
            var td = direct.Where(d => ids.Contains(d.a.id) && ids.Contains(d.b.id)).ToList();
            result.Add(new Spec($"{name} {TileName(t)}", dim, $"SatisfactoryPlanner · tile {TileName(t)} of {L.Width / 8:0}×{L.Height / 8:0} foundations", baseBlueprint, te, tl, tw, td));
        }
        return result;

        // ---- power: per floor, an 8 m × 1 m wall with wall outlets (Mk2 for Mk2 tiles, Mk3 for Mk3) wired to that floor's
        //      machines, so no wire runs through a floor (user, 2026-09-25). A refinery (too tall: it reaches through the
        //      floor above) takes power from the floor above when there is one. The wall hangs under the next floor up, or
        //      stands on the top floor; it sits at the free spot nearest the one below (the floors' walls are joined by one
        //      short wire straight up), the lowest nearest the tile centre. Outlets chained; two plug slots left free on the
        //      lowest floor for wiring the tiles together by hand. ----
        void Power((int tx, int ty) t, List<Entity> te, List<Link> tl, List<Wire> tw, double ox, double oy)
        {
            var machines = te.Where(v => Ports.ContainsKey(v.cls) && !v.cls.Contains("Storage") && !v.cls.Contains("PipeStorage")).ToList();
            var handFloors = handPower.GetValueOrDefault(t) ?? new List<int>(); // slots kept free for machines placed by hand
            if (machines.Count + handFloors.Count == 0) return;
            string outletCls = dim >= 6 ? "Build_PowerPoleWall_Mk3_C" : "Build_PowerPoleWall_Mk2_C";
            int cap = dim >= 6 ? 10 : 7; // connections per outlet (same as the power pole of that tier)
            int FloorAt(double z) { int f = 0; for (int i = 1; i < floors; i++) if (FloorTop + E(i) * 100 <= z + 1) f = i; return f; }
            int PowerFloor(Entity m) { int f = FloorAt(m.z); return m.cls == "Build_OilRefinery_C" && f + 1 < floors ? f + 1 : f; }
            var byFloor = machines.GroupBy(PowerFloor).ToDictionary(g => g.Key, g => g.ToList());
            var used = byFloor.Keys.Concat(handFloors).Distinct().OrderBy(f => f).ToList();
            double half = T * 50;
            (double x, double y)? prev = null;
            string? prevLast = null;
            for (int gi = 0; gi < used.Count; gi++)
            {
                int f = used[gi];
                var ms = byFloor.GetValueOrDefault(f) ?? new List<Entity>();
                int handSlots = handFloors.Count(h => h == f);
                bool lowest = gi == 0, highest = gi == used.Count - 1;
                int links = (lowest ? 0 : 1) + (highest ? 0 : 1); // wires to the floor below / above
                int free = lowest ? 2 : 0;                       // plug slots for wiring the tiles together
                int k = 1;
                while (k * cap - 2 * (k - 1) - free - links < ms.Count + handSlots) k++;
                if (k > 4) { warnings.Add($"tile {TileName(t)} floor {f + 1}: {ms.Count} machines need more than 4 outlets"); k = 4; }
                bool hanging = f + 1 < floors;
                // obstacles in the tile frame (cm) on this floor: its buildings (and tall ones from below reaching up),
                // holes in the floor it stands on / the ceiling it hangs from, and (for a wall standing) its belts
                var rects = new List<(double x0, double x1, double y0, double y1)>();
                foreach (var v in te.Where(v => !v.cls.Contains("Foundation")))
                {
                    int vf = FloorAt(v.z);
                    if (vf != f && !(vf < f && v.cls == "Build_OilRefinery_C" && vf + 1 == f)) continue;
                    double hw = 250, hh = 250;
                    var fb = L.Buildings.FirstOrDefault(b => Math.Abs((b.X + b.W / 2) * 100 - (v.x + ox)) < 1 && Math.Abs(-(b.Y + b.H / 2) * 100 - (v.y + oy)) < 1);
                    if (fb != null) { hw = fb.W * 50; hh = fb.H * 50; }
                    rects.Add((v.x - hw - 50, v.x + hw + 50, v.y - hh - 50, v.y + hh + 50));
                }
                foreach (var h in L.Buildings.Where(b => b.Kind == "hole" && (b.Floor == f + 1 && hanging || b.Floor == f && f > 0)))
                {
                    double hx0 = h.X * 100 - ox, hx1 = (h.X + h.W) * 100 - ox, hy0 = -(h.Y + h.H) * 100 - oy, hy1 = -h.Y * 100 - oy;
                    rects.Add((hx0 - 50, hx1 + 50, hy0 - 50, hy1 + 50));
                }
                int solidCount = rects.Count;
                if (!hanging)
                    foreach (var ln in tl)
                        for (int i = 1; i < ln.pts.Count; i++)
                        {
                            if (FloorAt(Math.Min(ln.pts[i - 1].z, ln.pts[i].z)) != f) continue;
                            rects.Add((Math.Min(ln.pts[i - 1].x, ln.pts[i].x) - 150, Math.Max(ln.pts[i - 1].x, ln.pts[i].x) + 150,
                                       Math.Min(ln.pts[i - 1].y, ln.pts[i].y) - 150, Math.Max(ln.pts[i - 1].y, ln.pts[i].y) + 150));
                        }
                (double x, double y) aim = prev ?? (0, 0);
                (double x, double y)? Find(List<(double x0, double x1, double y0, double y1)> rs)
                {
                    (double x, double y)? best = null; double bestD = double.MaxValue;
                    for (double x = -half + 150; x <= half - 150; x += 100)
                        for (double y = -half + 500; y <= half - 500; y += 100)
                        {
                            // the wall runs north–south: 1 m × 8 m
                            if (rs.Any(r => x + 50 > r.x0 && x - 50 < r.x1 && y + 400 > r.y0 && y - 400 < r.y1)) continue;
                            double d = (x - aim.x) * (x - aim.x) + (y - aim.y) * (y - aim.y);
                            if (d < bestD) { bestD = d; best = (x, y); }
                        }
                    return best;
                }
                var spot = Find(rects);
                if (spot == null && !hanging)
                {
                    // crowded floor: allow the wall over belts (it may clip them) rather than leave it unpowered
                    spot = Find(rects.Take(solidCount).ToList());
                    if (spot != null) warnings.Add($"tile {TileName(t)} floor {f + 1}: power wall placed over belts (no clear spot)");
                }
                if (spot == null) { warnings.Add($"tile {TileName(t)} floor {f + 1}: no free 8 m × 1 m spot for the power wall"); continue; }
                double wz = hanging ? FloorTop + E(f + 1) * 100 - 100 - 100 : FloorTop + E(f) * 100; // under the 1 m foundation above
                string wid = $"pw{t.tx}_{t.ty}_{f}";
                te.Add(new Entity(wid, "Build_Wall_Orange_8x1_C", spot.Value.x, spot.Value.y, wz, 180));
                var outlets = new List<string>();
                for (int i = 0; i < k; i++)
                {
                    string oid = $"po{t.tx}_{t.ty}_{f}_{i}";
                    te.Add(new Entity(oid, outletCls, spot.Value.x, spot.Value.y + (-300 + 200 * i), wz + 100, 180));
                    if (i > 0) tw.Add(new Wire(outlets[^1], oid));
                    outlets.Add(oid);
                }
                var load = outlets.Select((_, i) => (i > 0 ? 1 : 0) + (i < outlets.Count - 1 ? 1 : 0)).ToArray();
                if (prevLast != null) { tw.Add(new Wire(prevLast, outlets[0])); load[0]++; } // up from the floor below
                if (!highest) load[^1]++;                                                   // (on to the floor above)
                load[^1] += free;
                // slots for machines placed by hand (they're wired in game): kept free on the outlets with most room
                for (int h = 0; h < handSlots; h++) { int o = Enumerable.Range(0, outlets.Count).OrderBy(i => load[i]).First(); load[o]++; }
                if (handSlots > 0) warnings.Add($"tile {TileName(t)} floor {f + 1}: {handSlots} outlet slot(s) kept for machines placed by hand");
                foreach (var m in ms.OrderBy(m => Math.Abs(m.x - spot.Value.x) + Math.Abs(m.y - spot.Value.y)))
                {
                    int o = Enumerable.Range(0, outlets.Count).Where(i => load[i] < cap).OrderBy(i => load[i]).FirstOrDefault(-1);
                    if (o < 0) { warnings.Add($"tile {TileName(t)} floor {f + 1}: not enough outlet slots"); break; }
                    load[o]++;
                    tw.Add(new Wire(outlets[o], m.id));
                }
                prev = spot; prevLast = outlets[^1];
            }
        }
    }

    static double Leg(List<Pt> p, int i) => Math.Sqrt(Math.Pow(p[i + 1].x - p[i].x, 2) + Math.Pow(p[i + 1].y - p[i].y, 2));
    static Pt Along(Pt from, Pt to, double d)
    {
        double l = Math.Max(1e-9, Math.Sqrt(Math.Pow(to.x - from.x, 2) + Math.Pow(to.y - from.y, 2)));
        return new Pt(from.x + (to.x - from.x) * d / l, from.y + (to.y - from.y) * d / l, from.z + (to.z - from.z) * d / l);
    }
    /// <summary>One tier down (Mk.1 belts one up, so there is always a visible difference).</summary>
    static string Lower(string cls)
    {
        if (cls == "Build_PipelineMK2_C") return "Build_Pipeline_C";
        if (cls == "Build_Pipeline_C") return "Build_PipelineMK2_C";
        var m = System.Text.RegularExpressions.Regex.Match(cls, @"Mk(\d)");
        if (!m.Success) return cls;
        int n = int.Parse(m.Groups[1].Value);
        return cls.Replace($"Mk{n}", $"Mk{(n > 1 ? n - 1 : 2)}");
    }

    static bool OnInterior((double X, double Y) a, (double X, double Y) z, (double X, double Y) p)
    {
        const double e = 5;
        bool horiz = Math.Abs(a.Y - z.Y) < e, vert = Math.Abs(a.X - z.X) < e;
        if (horiz && Math.Abs(p.Y - a.Y) < e) return p.X > Math.Min(a.X, z.X) + e && p.X < Math.Max(a.X, z.X) - e;
        if (vert && Math.Abs(p.X - a.X) < e) return p.Y > Math.Min(a.Y, z.Y) + e && p.Y < Math.Max(a.Y, z.Y) - e;
        return false;
    }
    static (double X, double Y) Dir((double X, double Y) a, (double X, double Y) z)
    {
        double dx = z.X - a.X, dy = z.Y - a.Y, l = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
        return (dx / l, dy / l);
    }

    public static string ToJson(Spec spec) => JsonSerializer.Serialize(spec);
}
