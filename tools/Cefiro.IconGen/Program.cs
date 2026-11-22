using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

// Uso: Cefiro.IconGen <ruta a Logo.xaml> <carpeta de salida>
// Genera cefiro.ico (16-256 px), cefiro-256.png y cefiro-1024.png.

var logoPath = args.Length > 0 ? args[0] : "src/Cefiro.App/Themes/Logo.xaml";
var output = args.Length > 1 ? args[1] : "src/Cefiro.App/Assets";
Directory.CreateDirectory(output);

var thread = new Thread(() =>
{
    var dictionary = (ResourceDictionary)XamlReader.Parse(File.ReadAllText(logoPath));
    var logo = (DrawingImage)dictionary["CefiroLogoImage"];

    var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
    var pngs = sizes.Select(s => (Size: s, Png: Render(logo, s))).ToList();

    WriteIco(Path.Combine(output, "cefiro.ico"), pngs);
    File.WriteAllBytes(Path.Combine(output, "cefiro-256.png"), pngs.Last().Png);
    File.WriteAllBytes(Path.Combine(output, "cefiro-1024.png"), Render(logo, 1024));
    Console.WriteLine($"Icono generado en {Path.GetFullPath(output)}");
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static byte[] Render(DrawingImage logo, int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        var s = (double)size;
        var radius = s * 0.23;
        var rect = new Rect(0.5, 0.5, s - 1, s - 1);

        // Fondo: cuadrado redondeado oscuro con un degradado sutil.
        var background = new LinearGradientBrush(
            Color.FromRgb(0x12, 0x1A, 0x33), Color.FromRgb(0x05, 0x07, 0x0E), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(background, null, rect, radius, radius);

        // Halo de color detrás del logotipo.
        var glow = new RadialGradientBrush(Color.FromArgb(0x66, 0x00, 0xE5, 0xFF), Color.FromArgb(0x00, 0x7C, 0x4D, 0xFF))
        {
            GradientOrigin = new Point(0.45, 0.4),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.55,
            RadiusY = 0.55,
        };
        dc.DrawRoundedRectangle(glow, null, rect, radius, radius);

        // Borde fino de "cristal" (solo en tamaños donde se aprecia).
        if (size >= 32)
        {
            var border = new LinearGradientBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 90);
            dc.DrawRoundedRectangle(null, new Pen(border, Math.Max(1, s / 128)), rect, radius, radius);
        }

        var inset = size <= 24 ? s * 0.08 : s * 0.16;
        dc.DrawImage(logo, new Rect(inset, inset, s - inset * 2, s - inset * 2));
    }

    // Brillo de neón: se dibuja el logotipo desenfocado debajo del nítido.
    var host = new DrawingVisual();
    using (var dc = host.RenderOpen())
    {
        dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, size, size));
    }

    var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    target.Render(visual);
    if (size >= 48)
    {
        var glowVisual = new DrawingVisual { Effect = new BlurEffect { Radius = size / 18.0 }, Opacity = 0.45 };
        using (var dc = glowVisual.RenderOpen())
        {
            var inset = size * 0.16;
            dc.DrawImage(logo, new Rect(inset, inset, size - inset * 2, size - inset * 2));
        }

        target.Render(glowVisual);
        target.Render(visual);
    }

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(target));
    using var ms = new MemoryStream();
    encoder.Save(ms);
    return ms.ToArray();
}

static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> images)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0);           // reservado
    w.Write((ushort)1);           // tipo: icono
    w.Write((ushort)images.Count);

    var offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);  // paleta
        w.Write((byte)0);  // reservado
        w.Write((ushort)1);   // planos
        w.Write((ushort)32);  // bits por píxel
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }

    foreach (var (_, png) in images)
        w.Write(png);
}
