using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SatisfactoryPlanner;

/// <summary>
/// Production-flow diagram: raw resources on the left, targets on the right, one box per machine group,
/// links sized by items/min. Column = longest path to a final output; rows ordered by neighbour barycentre
/// to reduce crossings. Loops (e.g. recycled rubber/plastic) are handled by ignoring back edges for layout.
/// </summary>
public class TreeDiagram : Canvas
{
    const double W = 210, H = 62, ColGap = 170, RowGap = 56, PortRoom = 12, Pad = 30;

    static readonly Brush[] Palette =
    [
        Brush("#E07A1F"), Brush("#2F7ED8"), Brush("#3AA35B"), Brush("#9B59B6"), Brush("#D64550"),
        Brush("#1FA2A8"), Brush("#B8860B"), Brush("#5D6D7E"), Brush("#C2185B"), Brush("#607D3B"),
    ];

    static SolidColorBrush Brush(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    static Brush EdgeBrush(string item) => Palette[(int)((uint)item.GetHashCode() % Palette.Length)];

    public void Render(Plan plan)
    {
        Children.Clear();
        var nodes = plan.Nodes.GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.First());
        var edges = plan.Edges.Where(e => nodes.ContainsKey(e.From) && nodes.ContainsKey(e.To)).ToList();
        if (nodes.Count == 0 || edges.Count == 0 && nodes.Count == 0) { Width = Height = 0; return; }

        var outs = nodes.Keys.ToDictionary(k => k, _ => new List<GraphEdge>());
        var ins = nodes.Keys.ToDictionary(k => k, _ => new List<GraphEdge>());
        foreach (var e in edges) { outs[e.From].Add(e); ins[e.To].Add(e); }

        // column = longest distance to a sink, via DFS that skips back edges (cycles)
        var col = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        int Depth(string k)
        {
            if (col.TryGetValue(k, out var d)) return d;
            onStack.Add(k);
            int best = 0;
            foreach (var e in outs[k])
                if (!onStack.Contains(e.To)) best = Math.Max(best, Depth(e.To) + 1);
            onStack.Remove(k);
            return col[k] = best;
        }
        foreach (var k in nodes.Keys) Depth(k);
        int maxCol = col.Values.Max();
        foreach (var n in nodes.Values.Where(n => n.Kind is NodeKind.Raw or NodeKind.Import)) col[n.Key] = maxCol; // sources line up on the left

        // order inside columns: start by item name, then a few barycentre sweeps
        var columns = Enumerable.Range(0, maxCol + 1)
            .Select(c => nodes.Values.Where(n => col[n.Key] == c).OrderBy(n => n.Kind).ThenBy(n => n.Title).Select(n => n.Key).ToList()).ToList();
        var pos = new Dictionary<string, double>();
        void Index() { foreach (var c in columns) for (int i = 0; i < c.Count; i++) pos[c[i]] = i - c.Count / 2.0; }
        Index();
        for (int sweep = 0; sweep < 6; sweep++)
        {
            var order = sweep % 2 == 0 ? Enumerable.Range(0, columns.Count).Reverse() : Enumerable.Range(0, columns.Count);
            foreach (var c in order)
            {
                columns[c] = columns[c].OrderBy(k =>
                {
                    var nb = outs[k].Select(e => (e.To, e.Rate)).Concat(ins[k].Select(e => (To: e.From, e.Rate))).ToList();
                    return nb.Count == 0 ? pos[k] : nb.Sum(x => pos[x.To] * x.Rate) / nb.Sum(x => x.Rate);
                }).ToList();
                Index();
            }
        }

        // vertical placement: each node gets a slot sized by how many links attach to it (busy nodes get more air),
        // then nodes are pulled toward the average height of their neighbours so links run as straight as possible
        double Slot(string k) => H + RowGap + PortRoom * Math.Max(0, Math.Max(outs[k].Count, ins[k].Count) - 2);
        var y = new Dictionary<string, double>();
        foreach (var c in columns)
        {
            double cur = 0;
            foreach (var k in c) { y[k] = cur; cur += Slot(k); }
        }
        double tallest = columns.Max(c => c.Sum(Slot));
        foreach (var c in columns) // centre shorter columns to start with
        {
            var off = (tallest - c.Sum(Slot)) / 2;
            foreach (var k in c) y[k] += off;
        }
        for (int pass = 0; pass < 12; pass++)
        {
            var order = pass % 2 == 0 ? Enumerable.Range(0, columns.Count) : Enumerable.Range(0, columns.Count).Reverse();
            foreach (var ci in order)
            {
                var c = columns[ci];
                // desired centre = rate-weighted mean of neighbour centres
                var want = c.Select(k =>
                {
                    var nb = outs[k].Select(e => (n: e.To, e.Rate)).Concat(ins[k].Select(e => (n: e.From, e.Rate))).ToList();
                    return nb.Count == 0 ? y[k] : nb.Sum(x => y[x.n] * x.Rate) / nb.Sum(x => x.Rate);
                }).ToList();
                // keep the order, respect minimum spacing: sweep down then up
                for (int i = 0; i < c.Count; i++)
                    y[c[i]] = Math.Max(want[i], i == 0 ? 0 : y[c[i - 1]] + Slot(c[i - 1]));
                for (int i = c.Count - 2; i >= 0; i--)
                    y[c[i]] = Math.Min(y[c[i]], y[c[i + 1]] - Slot(c[i]));
                if (c.Count > 0 && y[c[0]] < 0) { var shift = -y[c[0]]; foreach (var k in c) y[k] += shift; }
            }
        }
        var minY = y.Values.Min();
        var at = new Dictionary<string, Point>();
        for (int c = 0; c <= maxCol; c++)
        {
            double x = Pad + (maxCol - c) * (W + ColGap);
            foreach (var k in columns[c]) at[k] = new Point(x, Pad + y[k] - minY);
        }
        Width = Pad * 2 + (maxCol + 1) * W + maxCol * ColGap;
        Height = Pad * 2 + at.Values.Max(p => p.Y) + H;

        // edges first so boxes sit on top; stack ports so parallel links don't overlap
        double maxRate = Math.Max(1e-9, edges.Select(e => e.Rate).DefaultIfEmpty(0).Max());
        var outOffset = new Dictionary<string, double>();
        var inOffset = new Dictionary<string, double>();
        // ports: outgoing links ordered by where they go, incoming by where they come from → no crossings at the box
        var outPort = new Dictionary<GraphEdge, double>();
        var inPort = new Dictionary<GraphEdge, double>();
        foreach (var k in nodes.Keys)
        {
            foreach (var e in outs[k].OrderBy(e => at[e.To].Y)) outPort[e] = Port(outOffset, k, outs[k].Count);
            foreach (var e in ins[k].OrderBy(e => at[e.From].Y)) inPort[e] = Port(inOffset, k, ins[k].Count);
        }
        const double Arrow = 9;
        var visuals = new List<(GraphEdge e, UIElement[] parts)>();
        foreach (var e in edges)
        {
            var thick = 1.5 + 7 * Math.Sqrt(e.Rate / maxRate);
            var a = at[e.From]; var b = at[e.To];
            var p0 = new Point(a.X + W, a.Y + H / 2 + outPort[e]);
            var tip = new Point(b.X, b.Y + H / 2 + inPort[e]);
            var p3 = new Point(tip.X - Arrow, tip.Y); // line stops at the arrowhead's base
            bool back = p3.X <= p0.X; // loop edge: route around
            var dx = back ? 140 : Math.Max(50, (p3.X - p0.X) / 2);
            var fig = new PathFigure { StartPoint = p0 };
            fig.Segments.Add(new BezierSegment(new Point(p0.X + dx, p0.Y + (back ? 80 : 0)), new Point(p3.X - dx, p3.Y + (back ? 80 : 0)), p3, true));
            var brush = EdgeBrush(e.Item);
            var half = Math.Max(4.5, thick * 0.9);
            var arrow = new Polygon
            {
                Points = [tip, new Point(tip.X - Arrow, tip.Y - half), new Point(tip.X - Arrow, tip.Y + half)],
                Fill = brush, Opacity = 0.9, IsHitTestVisible = false
            };
            Children.Add(arrow);
            var path = new Path
            {
                Data = new PathGeometry([fig]), Stroke = brush, StrokeThickness = thick, Opacity = 0.55,
                StrokeDashArray = back ? [4, 3] : null,
                ToolTip = $"{GameData.Item(e.Item).Name}: {e.Rate:0.##}/min"
            };
            path.MouseEnter += (_, _) => path.Opacity = 1;
            path.MouseLeave += (_, _) => path.Opacity = 0.55;
            Children.Add(path);

            // rate label near the consumer end
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 21, 23, 26)), CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0),
                Child = new TextBlock { Text = $"{e.Rate:0.##} {GameData.Item(e.Item).Name}" + (plan.BeltNote(e.Item, e.Rate) is { } bn ? $" ({bn})" : ""), FontSize = 10, Foreground = brush },
                IsHitTestVisible = false
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(label, p3.X - label.DesiredSize.Width - 6);
            SetTop(label, p3.Y - label.DesiredSize.Height - 1);
            Children.Add(label);
            visuals.Add((e, [path, arrow, label]));
        }

        foreach (var n in nodes.Values)
        {
            var box = NodeBox(n);
            // hover a box: keep only its own links visible so long, crossing flows are easy to follow
            var key = n.Key;
            box.MouseEnter += (_, _) =>
            {
                foreach (var (e, parts) in visuals)
                {
                    bool mine = e.From == key || e.To == key;
                    foreach (var part in parts) part.Opacity = mine ? 1 : 0.07;
                }
            };
            box.MouseLeave += (_, _) =>
            {
                foreach (var (_, parts) in visuals)
                {
                    parts[0].Opacity = 0.55; parts[1].Opacity = 0.9; parts[2].Opacity = 1;
                }
            };
            SetLeft(box, at[n.Key].X);
            SetTop(box, at[n.Key].Y);
            Children.Add(box);
        }
    }

    static double Port(Dictionary<string, double> used, string key, int count)
    {
        var step = count <= 1 ? 0 : Math.Min(10, (H - 16) / (count - 1));
        var i = used.GetValueOrDefault(key);
        used[key] = i + 1;
        return (i - (count - 1) / 2.0) * step;
    }

    static FrameworkElement NodeBox(GraphNode n)
    {
        var (bg, border) = n.Kind switch
        {
            // dark cards (the app's theme), the kind told by the border colour
            NodeKind.Raw => ("#241F19", "#B07A3C"),
            NodeKind.Import => ("#2A1C1C", "#D05A4C"),
            NodeKind.Target => ("#1B2621", "#4FAF7C"),
            NodeKind.Surplus => ("#202326", "#6B737C"),
            _ => ("#1D2024", "#F28C28"),
        };
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icons.Children.Add(new Image { Source = ImageCache.Get(n.Item), Width = 34, Height = 34 });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = n.Title, FontWeight = FontWeights.SemiBold, Foreground = Brush("#E8E6E1"), TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = new StackPanel { Orientation = Orientation.Horizontal };
        if (n.Building != null) sub.Children.Add(new Image { Source = ImageCache.Get(n.Building), Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
        sub.Children.Add(new TextBlock { Text = n.Subtitle, FontSize = 11, Foreground = Brush("#C9CDD2"), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(sub);
        text.Children.Add(new TextBlock { Text = $"{n.Rate:0.##} /min", FontSize = 11, Foreground = Brush("#9AA1A9") });
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icons, Dock.Left);
        dock.Children.Add(icons);
        dock.Children.Add(text);
        FrameworkElement content = dock;
        if (n.Guard != null)
        {
            // small shield badge in the corner: this machine needs an overflow guard on some output
            var badge = new Border
            {
                Background = Brush("#C0392B"), CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -4, 0),
                Child = new TextBlock { Text = "⛨ " + Loc.T("guard.badge"), FontSize = 9, Foreground = Brushes.White },
                ToolTip = Loc.T("guard.badgeTip", n.Guard)
            };
            var grid = new Grid();
            grid.Children.Add(dock);
            grid.Children.Add(badge);
            content = grid;
        }
        return new Border
        {
            Width = W, Height = H, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.5), Padding = new Thickness(8, 4, 8, 4),
            Background = Brush(bg), BorderBrush = Brush(n.Guard != null ? "#C0392B" : border), Child = content,
            ToolTip = $"{n.Title}\n{n.Subtitle}\n{n.Rate:0.##} /min",
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.18 }
        };
    }
}
