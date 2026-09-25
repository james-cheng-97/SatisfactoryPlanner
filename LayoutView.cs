using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SatisfactoryPlanner;

/// <summary>
/// Blueprint-style sky view of a <see cref="Layout"/>, drawn like the in-game map: dark background, foundation grid,
/// buildings as coloured outlines, belts yellow, pipes blue, small symbols for splitters, mergers, valves and guards.
/// North is up; the station wall (inputs/outputs) is along the bottom edge.
/// </summary>
public class LayoutView : Canvas
{
    const double Px = 4; // pixels per metre at 100 % zoom

    static SolidColorBrush B(string hex, byte a = 255)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    static readonly Brush Background_ = B("#1E2A33"), GridMinor = B("#2B3945"), GridMajor = B("#3A4B59"),
        BeltBrush = B("#F2C94C"), PipeBrush = B("#56CCF2"), Text = B("#E8EEF2"), SubText = B("#9FB3C2"),
        GuardBrush = B("#E74C3C"), SplitterBrush = B("#FFFFFF");

    static string ColorFor(string building, string kind) => kind switch
    {
        "input" => "#27AE60",
        "output" => "#7C4DFF", // violet: apart from the gold belts and every machine colour
        "surplus" => "#95A5A6",
        _ => building switch
        {
            "Desc_SmelterMk1_C" => "#E67E22",
            "Desc_ConstructorMk1_C" => "#A3D86B",
            "Desc_AssemblerMk1_C" => "#E74C3C",
            "Desc_FoundryMk1_C" => "#9B59B6",
            "Desc_ManufacturerMk1_C" => "#FF6FB5",
            "Desc_OilRefinery_C" => "#1ABC9C",
            "Desc_Packager_C" => "#5DADE2",
            "Desc_Blender_C" => "#48C9B0",
            "Desc_HadronCollider_C" => "#F1948A",
            "Desc_Converter_C" => "#BB8FCE",
            "Desc_QuantumEncoder_C" => "#85C1E9",
            _ => "#BDC3C7",
        }
    };

    /// <summary>Draw one floor (other floors as faint outlines), or all floors at once with <paramref name="floor"/> = -1.</summary>
    public void Render(Layout layout, int floor = -1)
    {
        bool On(int f) => floor < 0 || f == floor;
        var beltsOn = layout.Belts.Where(s => On(s.Floor)).ToList();
        var buildingsOn = layout.Buildings.Where(b => On(b.Floor)).ToList();
        var markersOn = layout.Markers.Where(m => On(m.Floor)).ToList();
        Children.Clear();
        if (layout.Width <= 0) { Width = Height = 0; return; }
        double W = layout.Width * Px, H = layout.Height * Px;
        Width = W; Height = H;
        Background = Background_;
        Point P(double x, double y) => new(x * Px, H - y * Px); // north up

        // foundation grid (8 m), with a stronger line every 4 foundations
        var grid = new GeometryGroup();
        var major = new GeometryGroup();
        for (double x = 0; x <= layout.Width + 0.1; x += Layout.Foundation)
            ((int)(x / Layout.Foundation) % 4 == 0 ? major : grid).Children.Add(new LineGeometry(P(x, 0), P(x, layout.Height)));
        for (double y = 0; y <= layout.Height + 0.1; y += Layout.Foundation)
            ((int)(y / Layout.Foundation) % 4 == 0 ? major : grid).Children.Add(new LineGeometry(P(0, y), P(layout.Width, y)));
        Children.Add(new Path { Data = grid, Stroke = GridMinor, StrokeThickness = 0.6 });
        Children.Add(new Path { Data = major, Stroke = GridMajor, StrokeThickness = 1 });

        // belts and pipes (batched into one path each: fast even for big layouts)
        var belts = new GeometryGroup();
        var pipes = new GeometryGroup();
        var lifted = new GeometryGroup();
        var liftedPipes = new GeometryGroup();
        var lifts = new List<Point>();
        // other floors: faint outlines for orientation
        if (floor >= 0)
            foreach (var b in layout.Buildings.Where(b => b.Floor != floor))
            {
                var tl0 = P(b.X, b.Y + b.H);
                var ghost = new Rectangle { Width = b.W * Px, Height = b.H * Px, Stroke = B("#9FB3C2", 70), StrokeThickness = 1, StrokeDashArray = [3, 3], IsHitTestVisible = false };
                SetLeft(ghost, tl0.X); SetTop(ghost, tl0.Y);
                Children.Add(ghost);
            }
        foreach (var s in beltsOn)
        {
            var g = s.Elevated ? (s.Fluid ? liftedPipes : lifted) : (s.Fluid ? pipes : belts);
            g.Children.Add(new LineGeometry(P(s.X1, s.Y1), P(s.X2, s.Y2)));
            if (s.Elevated) { lifts.Add(P(s.X1, s.Y1)); lifts.Add(P(s.X2, s.Y2)); }
        }
        // drawn at their real in-game widths (belt 2 m, pipe ≈1 m)
        Children.Add(new Path { Data = belts, Stroke = BeltBrush, StrokeThickness = Layout.BeltWidth * Px, Opacity = 0.75, StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square });
        Children.Add(new Path { Data = pipes, Stroke = PipeBrush, StrokeThickness = Layout.PipeWidth * Px, Opacity = 0.9 });
        // belts lifted over other belts: dashed, with a lift (2×2 m) at each end
        Children.Add(new Path { Data = lifted, Stroke = BeltBrush, StrokeThickness = Layout.BeltWidth * Px * 0.7, StrokeDashArray = [1.2, 0.8], Opacity = 0.9 });
        Children.Add(new Path { Data = liftedPipes, Stroke = PipeBrush, StrokeThickness = Layout.PipeWidth * Px, StrokeDashArray = [1.5, 1], Opacity = 0.9 });
        foreach (var lp in lifts) Children.Add(Square(lp, 2 * Px, B("#F39C12", 220), B("#1E2A33")));
        // congested corridors (several belts side by side): an orange band — solid border once stacked
        foreach (var cr in layout.Corridors.Where(c => On(c.Floor)))
        {
            var ct = P(cr.X0 - 1, cr.Y1 + 1);
            var band = new Rectangle { Width = (cr.X1 - cr.X0 + 2) * Px, Height = (cr.Y1 - cr.Y0 + 2) * Px, Fill = B("#FF9800", 80), Stroke = B("#FFA726"),
                StrokeThickness = 2.2, StrokeDashArray = cr.Stacked ? null : [3, 2], IsHitTestVisible = false };
            SetLeft(band, ct.X); SetTop(band, ct.Y);
            Children.Add(band);
            var ctag = new TextBlock { Text = $"≡{cr.Lines}", Foreground = B("#FFB74D"), FontSize = 10, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
            SetLeft(ctag, ct.X + 2); SetTop(ctag, ct.Y - 13);
            Children.Add(ctag);
        }
        // belt / pipe stretches built by hand (a bend right by a tile border): lime, dashed, over the normal line
        foreach (var (x1, y1, x2, y2, hf, fl) in layout.HandRuns.Where(h => On(h.floor)))
            Children.Add(new Line { X1 = P(x1, y1).X, Y1 = P(x1, y1).Y, X2 = P(x2, y2).X, Y2 = P(x2, y2).Y, Stroke = B("#C6FF00"),
                StrokeThickness = (fl ? Layout.PipeWidth : Layout.BeltWidth) * Px * 0.8, StrokeDashArray = [1.2, 0.6], StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false });
        // power hints for machines placed by hand: a dotted line to the tile whose outlets keep a slot for it
        foreach (var (x1, y1, x2, y2, hf) in layout.PowerHints.Where(h => On(h.floor)))
            Children.Add(new Line { X1 = P(x1, y1).X, Y1 = P(x1, y1).Y, X2 = P(x2, y2).X, Y2 = P(x2, y2).Y, Stroke = B("#FFD54F", 200), StrokeThickness = 1.5, StrokeDashArray = [2, 3], IsHitTestVisible = false });
        // flow direction: a small chevron every ~10 m along each belt / pipe, pointing the way items move
        var chevrons = new StreamGeometry();
        using (var ctx = chevrons.Open())
            foreach (var s in beltsOn)
            {
                double len = Math.Abs(s.X2 - s.X1) + Math.Abs(s.Y2 - s.Y1);
                if (len < 3) continue;
                double dx = Math.Sign(s.X2 - s.X1), dy = Math.Sign(s.Y2 - s.Y1);
                int n = Math.Max(1, (int)(len / 10));
                double h = (s.Fluid ? 0.6 : 0.9) * Px; // half-width of the chevron (m → px)
                for (int i = 0; i < n; i++)
                {
                    double f = (i + 0.5) / n;
                    var c = P(s.X1 + (s.X2 - s.X1) * f, s.Y1 + (s.Y2 - s.Y1) * f);
                    // screen y grows downward, world y upward
                    double ux = dx, uy = -dy, px = -uy, py = ux;
                    ctx.BeginFigure(new Point(c.X - ux * h + px * h, c.Y - uy * h + py * h), false, false);
                    ctx.LineTo(new Point(c.X + ux * h * 0.6, c.Y + uy * h * 0.6), true, false);
                    ctx.LineTo(new Point(c.X - ux * h - px * h, c.Y - uy * h - py * h), true, false);
                }
            }
        chevrons.Freeze();
        Children.Add(new Path { Data = chevrons, Stroke = B("#10161B", 230), StrokeThickness = Math.Max(1, 0.45 * Px), StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false });

        // buildings
        foreach (var b in buildingsOn)
        {
            if (b.Kind == "hole")
            {
                // no foundation here: a tall machine below reaches through (open to the sky)
                var th = P(b.X, b.Y + b.H);
                var hatch = new DrawingBrush(new GeometryDrawing(null, new Pen(B("#5DADE2", 120), 1), Geometry.Parse("M0,8 L8,0")))
                { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
                var hr = new Rectangle { Width = b.W * Px, Height = b.H * Px, Fill = hatch, Stroke = B("#5DADE2"), StrokeThickness = 1.4, StrokeDashArray = [4, 3], ToolTip = b.Label };
                SetLeft(hr, th.X); SetTop(hr, th.Y);
                Children.Add(hr);
                var ht = new TextBlock { Text = b.Label, Foreground = B("#AED6F1"), FontSize = 10, IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, Width = Math.Max(40, b.W * Px - 8) };
                SetLeft(ht, th.X + 4); SetTop(ht, th.Y + 4);
                Children.Add(ht);
                continue;
            }
            var hex = ColorFor(b.Building, b.Kind);
            var tl = P(b.X, b.Y + b.H);
            var rect = new Rectangle
            {
                Width = b.W * Px, Height = b.H * Px, Fill = B(hex, (byte)(b.ByHand ? 25 : 60)), Stroke = B(hex), StrokeThickness = b.ByHand ? 2.2 : 1.6,
                StrokeDashArray = b.ByHand ? [4, 2.5] : null, // placed by hand (not in any blueprint)
                RadiusX = 3, RadiusY = 3,
                ToolTip = (b.Label.Length > 0 ? b.Label + "\n" : "") + $"{GameData.Buildings.GetValueOrDefault(b.Building)?.Name} · x {b.X:0} m, y {b.Y:0} m ({b.X / Layout.Foundation:0.#}, {b.Y / Layout.Foundation:0.#} foundations)"
            };
            SetLeft(rect, tl.X); SetTop(rect, tl.Y);
            Children.Add(rect);
            if (b.ByHand)
            {
                var hb = new TextBlock { Text = "✋", ToolTip = Loc.T("layout.handTag"), Foreground = B("#FFD54F"), FontSize = 10, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
                SetLeft(hb, tl.X + 3); SetTop(hb, tl.Y + b.H * Px - 15);
                Children.Add(hb);
            }
            // small inner mark so identical machines read as a row of "symbols" like the map
            var dot = new Ellipse { Width = Math.Min(b.W, b.H) * Px * 0.35, Height = Math.Min(b.W, b.H) * Px * 0.35, Fill = B(hex, 170), IsHitTestVisible = false };
            SetLeft(dot, tl.X + b.W * Px / 2 - dot.Width / 2); SetTop(dot, tl.Y + b.H * Px / 2 - dot.Height / 2);
            Children.Add(dot);
            if (b.Label.Length > 0)
            {
                var t = new TextBlock { Text = b.Label, Foreground = b.Kind == "machine" ? Text : B(hex), FontSize = 10, IsHitTestVisible = false };
                if (b.Kind == "machine")
                {
                    // two-line title (row + anchor) above the machine; anchor line in orange
                    t.Inlines.Clear();
                    var lines = b.Label.Split('\n');
                    t.Inlines.Add(new System.Windows.Documents.Run(lines[0]));
                    if (lines.Length > 1) { t.Inlines.Add(new System.Windows.Documents.LineBreak()); t.Inlines.Add(new System.Windows.Documents.Run(lines[1]) { Foreground = B("#F39C12") }); }
                    t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    SetLeft(t, tl.X); SetTop(t, tl.Y - t.DesiredSize.Height - 2);
                }
                else
                {
                    t.LayoutTransform = new RotateTransform(-90);
                    t.FontSize = 9;
                    t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    SetLeft(t, tl.X + b.W * Px / 2 - t.DesiredSize.Width / 2);
                    // label on the side away from the box's belt: station boxes in a row's free zone connect north or south
                    bool beltSouth = layout.Wall.Any(w => Math.Abs(w.y - b.Y) < 0.01 && w.x >= b.X && w.x <= b.X + b.W);
                    if (beltSouth) SetTop(t, tl.Y - t.DesiredSize.Height - 2);
                    else if (b.Y + b.H <= 2 * 8 + 0.01) SetTop(t, H - t.DesiredSize.Height - 2 - b.H * Px - 4); // wall strip
                    else SetTop(t, tl.Y + b.H * Px + 2);
                }
                Children.Add(t);
            }
        }

        // symbols
        var onTop = new List<UIElement>(); // lift tags: drawn after everything else so nothing covers them
        foreach (var m in markersOn)
        {
            var p = P(m.X, m.Y);
            FrameworkElement? e = m.Kind switch
            {
                // real sizes: splitter/merger 4×4 m, valve/junction 2×2 m
                "splitter" => Square(p, Layout.SplitterSize * Px, B("#FFFFFF", 200), B("#1E2A33")),
                "merger" => Square(p, Layout.SplitterSize * Px, B("#1E2A33", 200), SplitterBrush),
                "junction" => Dot(p, Layout.ValveSize * Px, PipeBrush),
                "valve" => Diamond(p, Layout.ValveSize * Px * 0.75, PipeBrush, GuardBrush),
                "guard" => Square(p, Layout.SplitterSize * Px, GuardBrush, B("#FFFFFF")),
                "anchor" => Anchor(p),
                "link" => Diamond(p, 5, BeltBrush, B("#FFFFFF")),
                "liftbase" => Square(p, 2 * Px, B("#8E44AD", 240), B("#FFFFFF")),              // conveyor lift / vertical pipe start
                "floorhole" => Square(p, 4 * Px, B("#8E44AD", 90), B("#D2B4DE")),              // conveyor lift / pipe floor hole
                _ => null
            };
            if (e != null) Children.Add(e);
            if (m.Text.Length > 0 && m.Kind != "splitter")
            {
                var t = new TextBlock
                {
                    Text = m.Text, FontSize = m.Kind == "lanelabel" ? 9 : 9,
                    Foreground = m.Kind is "guard" or "valve" ? GuardBrush : m.Kind is "liftbase" or "floorhole" ? B("#F4ECF7") : m.Kind is "lift" or "anchortext" ? B("#F39C12") : SubText, IsHitTestVisible = false
                };
                if (m.Kind == "lanelabel") t.LayoutTransform = new RotateTransform(-90);
                if (m.Kind is "liftbase" or "floorhole")
                {
                    // readable over belts: small tag with a dark backing, offset to the upper right of the lift
                    t.Background = B("#1E2A33", 220); t.Padding = new Thickness(2, 0, 2, 0); t.FontSize = 8;
                }
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (m.Kind == "lanelabel") { SetLeft(t, p.X - t.DesiredSize.Width / 2); SetTop(t, p.Y - t.DesiredSize.Height); }
                else if (m.Kind == "anchortext") { SetLeft(t, p.X + 6); SetTop(t, p.Y + 4); } // just under the anchor, inside the input strip
                else if (m.Kind is "liftbase" or "floorhole") { SetLeft(t, p.X + 2.5 * Px); SetTop(t, p.Y - 2.5 * Px - t.DesiredSize.Height); }
                else { SetLeft(t, p.X + 3); SetTop(t, p.Y - t.DesiredSize.Height - 1); }
                if (m.Kind is "liftbase" or "floorhole") onTop.Add(t); else Children.Add(t);
            }
        }
        // lift tags: nudge upward until they don't overlap each other
        var placed = new List<Rect>();
        foreach (var e in onTop.OfType<FrameworkElement>())
        {
            var r = new Rect(GetLeft(e), GetTop(e), e.DesiredSize.Width, e.DesiredSize.Height);
            for (int guard = 0; guard < 20 && placed.Any(q => q.IntersectsWith(r)); guard++) r.Y -= r.Height + 1;
            SetTop(e, r.Y);
            placed.Add(r);
            Children.Add(e);
        }
        FitToContent(W, H);
    }

    /// <summary>Grow the canvas so labels sticking out past the factory edge aren't cut off (content shifted in).</summary>
    void FitToContent(double w, double h)
    {
        const double Pad = 6;
        double minX = 0, minY = 0, maxX = w, maxY = h;
        foreach (UIElement c in Children)
        {
            double l = GetLeft(c), tp = GetTop(c);
            if (double.IsNaN(l) || double.IsNaN(tp) || c is not FrameworkElement fe) continue;
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            minX = Math.Min(minX, l); minY = Math.Min(minY, tp);
            maxX = Math.Max(maxX, l + fe.DesiredSize.Width); maxY = Math.Max(maxY, tp + fe.DesiredSize.Height);
        }
        double dx = Pad - minX, dy = Pad - minY;
        foreach (UIElement c in Children)
        {
            double l = GetLeft(c), tp = GetTop(c);
            SetLeft(c, (double.IsNaN(l) ? 0 : l) + dx);
            SetTop(c, (double.IsNaN(tp) ? 0 : tp) + dy);
        }
        Width = maxX - minX + 2 * Pad;
        Height = maxY - minY + 2 * Pad;
    }

    static FrameworkElement Square(Point p, double size, Brush? fill, Brush? stroke)
    {
        var r = new Rectangle { Width = size, Height = size, Fill = fill, Stroke = stroke, StrokeThickness = 1.2, IsHitTestVisible = false };
        SetLeft(r, p.X - size / 2); SetTop(r, p.Y - size / 2);
        return r;
    }

    /// <summary>Crosshair on a row's anchor corner (first machine's south-west corner, on a foundation corner).</summary>
    static FrameworkElement Anchor(Point p)
    {
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(new Point(p.X - 7, p.Y), new Point(p.X + 7, p.Y)));
        g.Children.Add(new LineGeometry(new Point(p.X, p.Y - 7), new Point(p.X, p.Y + 7)));
        g.Children.Add(new EllipseGeometry(p, 3.5, 3.5));
        return new Path { Data = g, Stroke = B("#F39C12"), StrokeThickness = 1.5, IsHitTestVisible = false };
    }

    static FrameworkElement Dot(Point p, double size, Brush fill)
    {
        var r = new Ellipse { Width = size, Height = size, Fill = fill, IsHitTestVisible = false };
        SetLeft(r, p.X - size / 2); SetTop(r, p.Y - size / 2);
        return r;
    }

    static FrameworkElement Diamond(Point p, double size, Brush fill, Brush stroke) => new Polygon
    {
        Points = [new Point(p.X, p.Y - size), new Point(p.X + size, p.Y), new Point(p.X, p.Y + size), new Point(p.X - size, p.Y)],
        Fill = fill, Stroke = stroke, StrokeThickness = 1.2, IsHitTestVisible = false
    };
}
