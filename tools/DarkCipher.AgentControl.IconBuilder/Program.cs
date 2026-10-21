using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DarkCipher.AgentControl.IconBuilder;

/// <summary>
/// The DarkCipher AgentControl mark: a dark hexagon (the controlled boundary) holding an open cipher ring ("C") around a
/// light agent core. Three teal nodes sit on the ring and are wired to the core (orchestration); a fourth spoke leaves
/// through the ring's opening to a square output node (controlled action). Detail drops out as the size shrinks so the
/// mark stays readable: 16-24 px = hexagon + ring + core; 32-48 px adds the nodes; 64 px and up adds the wiring.
/// All geometry is in a 256-unit design space. Colours are the DarkCipher Systems palette (WhisperDesk/ServerManagement).
/// </summary>
internal static class IconArt
{
    public const string Teal = "#36D2CB";      // brand accent (DefaultAccent in both reference apps)
    public const string Ink = "#050909";       // Bg
    public const string Panel = "#0B1314";     // Surface
    public const string Core = "#D2F2EF";      // Fg
    public const string Wire = "#1F7A75";      // teal mixed 55% towards Bg: visible on the panel, quieter than the nodes

    public enum Detail { Small, Medium, Large }

    public static Detail For(int px) => px <= 24 ? Detail.Small : px <= 48 ? Detail.Medium : Detail.Large;

    public static Point[] Hexagon(double inset)
    {
        var r = 122 - inset;
        return Enumerable.Range(0, 6).Select(i =>
        {
            var a = Math.PI / 180 * (-90 + 60 * i);
            return new Point(128 + r * Math.Cos(a), 128 + r * Math.Sin(a));
        }).ToArray();
    }

    /// <summary>Stroke widths per detail level (design units). Small sizes get proportionally thicker strokes.</summary>
    public static (double Hex, double Ring, double Wire) Strokes(Detail d) => d switch
    {
        Detail.Small => (22, 30, 0),
        Detail.Medium => (14, 20, 0),
        _ => (11, 16, 7),
    };

    public const double RingRadius = 64;
    public const double GapHalfDegrees = 38;   // opening on the right
    public static double CoreRadius(Detail d) => d == Detail.Small ? 22 : 19;
    public const double NodeRadius = 13;
    public const double OutputX = 128 + RingRadius + 17;   // square output node, clear of the hexagon rim
    public const double OutputHalf = 11;
    public static readonly double[] NodeAngles = [-90, 180, 90];   // top, left, bottom

    public static Point OnRing(double degrees, double radius = RingRadius)
    {
        var a = Math.PI / 180 * degrees;
        return new Point(128 + radius * Math.Cos(a), 128 + radius * Math.Sin(a));
    }

    public static void Draw(DrawingContext dc, Detail d)
    {
        var (hexW, ringW, wireW) = Strokes(d);
        var teal = Brush(Teal);

        // Hexagon: dark panel with a teal rim (the rim keeps it visible on dark taskbars, the fill on light ones).
        var hex = Poly(Hexagon(hexW / 2));
        dc.DrawGeometry(Brush(Panel), new Pen(teal, hexW) { LineJoin = PenLineJoin.Round }, hex);

        // Wiring first so nodes sit on top of it.
        if (d == Detail.Large)
        {
            var wire = new Pen(Brush(Wire), wireW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            foreach (var a in NodeAngles) dc.DrawLine(wire, new Point(128, 128), OnRing(a));
            dc.DrawLine(new Pen(teal, wireW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(128, 128), new Point(128 + RingRadius + 14, 128));
        }

        // Open cipher ring.
        var ring = new Pen(teal, ringW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, ring, Arc(GapHalfDegrees, 360 - GapHalfDegrees, RingRadius));

        if (d == Detail.Medium)
        {
            // 32-48 px: an inked centre turns to mud, so the nodes are solid bumps on the ring.
            foreach (var a in NodeAngles) dc.DrawEllipse(teal, null, OnRing(a), 17, 17);
        }
        else if (d == Detail.Large)
        {
            foreach (var a in NodeAngles)
            {
                var p = OnRing(a);
                dc.DrawEllipse(Brush(Ink), new Pen(teal, ringW * 0.55), p, NodeRadius, NodeRadius);
                dc.DrawEllipse(teal, null, p, NodeRadius * 0.45, NodeRadius * 0.45);
            }
        }
        if (d == Detail.Large)
        {
            var s = OutputHalf;
            var c = new Point(OutputX, 128);
            dc.DrawRoundedRectangle(teal, null, new Rect(c.X - s, c.Y - s, 2 * s, 2 * s), 3, 3);
        }

        // Agent core.
        dc.DrawEllipse(Brush(Core), new Pen(teal, d == Detail.Small ? 0 : 5), new Point(128, 128), CoreRadius(d), CoreRadius(d));
    }

    public static Geometry Arc(double fromDeg, double toDeg, double r)
    {
        var start = OnRing(fromDeg, r);
        var end = OnRing(toDeg, r);
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    private static Geometry Poly(Point[] pts)
    {
        var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
        foreach (var p in pts.Skip(1)) fig.Segments.Add(new LineSegment(p, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    public static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>The editable master (Large detail), generated from the same numbers as the bitmaps.</summary>
    public static string Svg()
    {
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var (hexW, ringW, wireW) = Strokes(Detail.Large);
        var hex = string.Join(" ", Hexagon(hexW / 2).Select(p => $"{F(p.X)},{F(p.Y)}"));
        var a0 = OnRing(GapHalfDegrees);
        var a1 = OnRing(360 - GapHalfDegrees);
        var sb = new StringBuilder();
        sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\" width=\"256\" height=\"256\">");
        sb.AppendLine("  <title>DarkCipher AgentControl</title>");
        sb.AppendLine("  <!-- Master artwork (64 px and larger). Generated by tools/DarkCipher.AgentControl.IconBuilder; edit IconArt there and re-run. -->");
        sb.AppendLine($"  <polygon points=\"{hex}\" fill=\"{Panel}\" stroke=\"{Teal}\" stroke-width=\"{F(hexW)}\" stroke-linejoin=\"round\"/>");
        foreach (var a in NodeAngles) { var p = OnRing(a); sb.AppendLine($"  <line x1=\"128\" y1=\"128\" x2=\"{F(p.X)}\" y2=\"{F(p.Y)}\" stroke=\"{Wire}\" stroke-width=\"{F(wireW)}\" stroke-linecap=\"round\"/>"); }
        sb.AppendLine($"  <line x1=\"128\" y1=\"128\" x2=\"{F(128 + RingRadius + 14)}\" y2=\"128\" stroke=\"{Teal}\" stroke-width=\"{F(wireW)}\" stroke-linecap=\"round\"/>");
        sb.AppendLine($"  <path d=\"M{F(a0.X)},{F(a0.Y)} A{F(RingRadius)},{F(RingRadius)} 0 1 1 {F(a1.X)},{F(a1.Y)}\" fill=\"none\" stroke=\"{Teal}\" stroke-width=\"{F(ringW)}\" stroke-linecap=\"round\"/>");
        foreach (var a in NodeAngles)
        {
            var p = OnRing(a);
            sb.AppendLine($"  <circle cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"{F(NodeRadius)}\" fill=\"{Ink}\" stroke=\"{Teal}\" stroke-width=\"{F(ringW * 0.55)}\"/>");
            sb.AppendLine($"  <circle cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"{F(NodeRadius * 0.45)}\" fill=\"{Teal}\"/>");
        }
        sb.AppendLine($"  <rect x=\"{F(OutputX - OutputHalf)}\" y=\"{F(128 - OutputHalf)}\" width=\"{F(2 * OutputHalf)}\" height=\"{F(2 * OutputHalf)}\" rx=\"3\" fill=\"{Teal}\"/>");
        sb.AppendLine($"  <circle cx=\"128\" cy=\"128\" r=\"{F(CoreRadius(Detail.Large))}\" fill=\"{Core}\" stroke=\"{Teal}\" stroke-width=\"5\"/>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}

internal static class Program
{
    private static readonly int[] PngSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512];
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: dcac-iconbuilder <output folder>"); return 2; }
        var outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);

        var frames = new Dictionary<int, BitmapSource>();
        foreach (var px in PngSizes.Union(IcoSizes).OrderBy(x => x))
        {
            var bmp = Render(px);
            frames[px] = bmp;
            if (PngSizes.Contains(px)) File.WriteAllBytes(Path.Combine(outDir, $"app-{px}.png"), Png(bmp));
        }
        File.WriteAllBytes(Path.Combine(outDir, "app.ico"), Ico(IcoSizes.Select(s => (s, frames[s])).ToList()));
        File.WriteAllText(Path.Combine(outDir, "app-master.svg"), IconArt.Svg(), new UTF8Encoding(false));
        Console.WriteLine($"Wrote {PngSizes.Length} PNGs, app.ico ({IcoSizes.Length} frames) and app-master.svg to {outDir}");
        return 0;
    }

    /// <summary>Renders the mark at <paramref name="px"/> pixels, transparent outside the hexagon.</summary>
    private static BitmapSource Render(int px)
    {
        var detail = IconArt.For(px);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / 256.0, px / 256.0));
            IconArt.Draw(dc, detail);
            dc.Pop();
        }
        RenderOptions.SetEdgeMode(dv, EdgeMode.Unspecified);
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var straight = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return straight;
    }

    private static byte[] Png(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Classic ICO: 32-bit BMP frames (with AND mask) below 256 px for every Windows surface, a PNG frame at 256.</summary>
    private static byte[] Ico(IReadOnlyList<(int Size, BitmapSource Bmp)> frames)
    {
        var images = frames.Select(f => f.Size >= 256 ? Png(f.Bmp) : Dib(f.Bmp, f.Size)).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            var s = frames[i].Size;
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(images[i].Length); w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Dib(BitmapSource bmp, int size)
    {
        var stride = size * 4;
        var pixels = new byte[stride * size];
        bmp.CopyPixels(pixels, stride, 0);
        var maskStride = (size + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(size); w.Write(size * 2); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(0); w.Write(stride * size + maskStride * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (var y = size - 1; y >= 0; y--) w.Write(pixels, y * stride, stride);   // bottom-up BGRA
        for (var y = size - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (var x = 0; x < size; x++)
                if (pixels[y * stride + x * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
            w.Write(row);
        }
        w.Flush();
        return ms.ToArray();
    }
}
