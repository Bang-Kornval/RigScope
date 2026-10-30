using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RigScope.Core.Rules;
using RigScope.Core.Storage;

namespace RigScope.App.Controls;

/// <summary>
/// Squarified treemap of a folder's children. Click a tile to open that folder (raises <see cref="OpenCommand"/>).
/// Drawn directly with DrawingContext, so thousands of folders stay fast.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(
        nameof(Node), typeof(FolderNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapControl)d).Relayout()));

    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(
        nameof(OpenCommand), typeof(ICommand), typeof(TreemapControl));

    public FolderNode? Node { get => (FolderNode?)GetValue(NodeProperty); set => SetValue(NodeProperty, value); }
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }

    private sealed record Tile(Rect Rect, string Name, long Size, FolderNode? Node, int ColorIndex);

    private List<Tile> _tiles = [];
    private Tile? _hover;
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    public TreemapControl()
    {
        Focusable = true;
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 150);
        SizeChanged += (_, _) => Relayout();
    }

    private void Relayout()
    {
        _tiles = [];
        if (Node is null || ActualWidth < 2 || ActualHeight < 2) { InvalidateVisual(); return; }

        var items = Node.Children.Where(c => c.Size > 0).Select((c, i) => (Name: c.Name, c.Size, Node: (FolderNode?)c, Color: i)).ToList();
        // Many tiny folders become one grey tile; files directly in the folder get their own.
        double total = Math.Max(1, Node.Size);
        var small = items.Where(i => i.Size / total < 0.002).ToList();
        if (small.Count > 3)
        {
            items = items.Except(small).ToList();
            items.Add(($"{small.Count} small folders", small.Sum(s => s.Size), null, -1));
        }
        if (Node.OwnFileBytes > 0) items.Add(("Files directly here", Node.OwnFileBytes, null, -1));

        var layout = Squarify(items.Select(i => (double)i.Size).ToList(), new Rect(0, 0, ActualWidth, ActualHeight));
        for (int i = 0; i < layout.Count; i++)
            _tiles.Add(new Tile(layout[i], items[i].Name, items[i].Size, items[i].Node, items[i].Color));
        InvalidateVisual();
    }

    /// <summary>Squarified layout (Bruls, Huizing, van Wijk). Input sizes in any order; output matches input order.</summary>
    internal static List<Rect> Squarify(List<double> sizes, Rect bounds)
    {
        var order = sizes.Select((s, i) => (s, i)).Where(x => x.s > 0).OrderByDescending(x => x.s).ToList();
        var result = new Rect[sizes.Count];
        double total = order.Sum(x => x.s);
        if (total <= 0) return [.. result];
        double scale = bounds.Width * bounds.Height / total;
        var rest = order.Select(x => (Area: x.s * scale, Index: x.i)).ToList();
        double x0 = bounds.X, y0 = bounds.Y, w = bounds.Width, h = bounds.Height;

        while (rest.Count > 0)
        {
            double side = Math.Min(w, h);
            var row = new List<(double Area, int Index)>();
            double best = double.MaxValue;
            foreach (var r in rest)
            {
                var test = row.Append(r).ToList();
                double sum = test.Sum(t => t.Area), max = test.Max(t => t.Area), min = test.Min(t => t.Area);
                double worst = Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
                if (worst > best) break;
                best = worst;
                row = test;
            }
            double rowSum = row.Sum(t => t.Area);
            if (w >= h)
            {
                double cw = rowSum / h, cy = y0;
                foreach (var r in row) { double ch = r.Area / cw; result[r.Index] = new Rect(x0, cy, cw, ch); cy += ch; }
                x0 += cw; w -= cw;
            }
            else
            {
                double rh = rowSum / w, cx = x0;
                foreach (var r in row) { double rw = r.Area / rh; result[r.Index] = new Rect(cx, y0, rw, rh); cx += rw; }
                y0 += rh; h -= rh;
            }
            rest = rest.Skip(row.Count).ToList();
        }
        return [.. result];
    }

    protected override void OnRender(DrawingContext dc)
    {
        var palette = (Application.Current.TryFindResource("TreemapPalette") as Color[]) ?? [Colors.SteelBlue];
        var border = new Pen((Brush?)TryFindResource("SolidBackgroundFillColorBaseBrush") ?? Brushes.Black, 1.5);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceMono = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable background
        foreach (var t in _tiles)
        {
            var color = t.ColorIndex < 0 ? Color.FromRgb(0x6B, 0x74, 0x7A) : palette[t.ColorIndex % palette.Length];
            if (t == _hover) color = Lighten(color, 0.15);
            var brush = new SolidColorBrush(color);
            dc.DrawRectangle(brush, border, t.Rect);
            if (t.Node is null) // pseudo tiles get a hatch so they read as "not a folder you can open"
                dc.DrawRectangle(Hatch, null, t.Rect);

            if (t.Rect.Width < 50 || t.Rect.Height < 32) continue;
            dc.PushClip(new RectangleGeometry(new Rect(t.Rect.X + 6, t.Rect.Y + 4, Math.Max(0, t.Rect.Width - 12), Math.Max(0, t.Rect.Height - 8))));
            var name = new FormattedText(t.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12.5, Brushes.White, dpi)
                { MaxTextWidth = Math.Max(1, t.Rect.Width - 12), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(name, new Point(t.Rect.X + 6, t.Rect.Y + 4));
            var size = new FormattedText(SnapshotQuery.FormatBytes(t.Size), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typefaceMono, 11, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), dpi);
            dc.DrawText(size, new Point(t.Rect.X + 6, t.Rect.Y + 21));
            dc.Pop();
        }
        if (_tiles.Count == 0 && Node is not null)
        {
            var empty = new FormattedText("This folder has no subfolders.", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 13,
                (Brush?)TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray, dpi);
            dc.DrawText(empty, new Point(12, 12));
        }
    }

    private static readonly Brush Hatch = CreateHatch();

    private static Brush CreateHatch()
    {
        var b = new DrawingBrush(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 3), Geometry.Parse("M0,10 L10,0")))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 10, 10), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        b.Freeze();
        return b;
    }

    private static Color Lighten(Color c, double amount) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    private Tile? HitTile(Point p) => _tiles.FirstOrDefault(t => t.Rect.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var t = HitTile(e.GetPosition(this));
        if (t == _hover) return;
        _hover = t;
        Cursor = t?.Node is { Children.Count: > 0 } ? Cursors.Hand : Cursors.Arrow;
        _tip.Content = t is null ? null : $"{t.Name}\n{SnapshotQuery.FormatBytes(t.Size)} · {t.Size * 100.0 / Math.Max(1, Node?.Size ?? 1):0.0}%"
                                          + (t.Node is { Children.Count: > 0 } ? "\nClick to open" : "");
        _tip.IsOpen = t is not null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (HitTile(e.GetPosition(this))?.Node is { Children.Count: > 0 } node && OpenCommand?.CanExecute(node) == true)
        {
            _tip.IsOpen = false;
            OpenCommand.Execute(node);
        }
    }
}
