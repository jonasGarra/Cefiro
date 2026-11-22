<#
.SYNOPSIS
    Genera docs\banner.png (1280×640): portada del repositorio y vista previa al compartir el enlace.
    Uso: powershell -ExecutionPolicy Bypass -File build\banner.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$root = Split-Path $PSScriptRoot -Parent
$W = 1280; $H = 640

function Brush([string]$hex) { [System.Windows.Media.BrushConverter]::new().ConvertFromString($hex) }
function Image([string]$path) {
    $bmp = [System.Windows.Media.Imaging.BitmapImage]::new()
    $bmp.BeginInit(); $bmp.CacheOption = 'OnLoad'; $bmp.UriSource = [Uri]::new($path); $bmp.EndInit(); $bmp
}

$canvas = [System.Windows.Controls.Canvas]::new()
$canvas.Width = $W; $canvas.Height = $H

# Fondo con degradado y dos halos de neón.
$bg = [System.Windows.Media.LinearGradientBrush]::new()
$bg.StartPoint = '0,0'; $bg.EndPoint = '1,1'
$bg.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Color]::FromRgb(0x0B,0x11,0x22), 0))
$bg.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Color]::FromRgb(0x06,0x08,0x0F), 1))
$canvas.Background = $bg

foreach ($halo in @(@{X=-160; Y=-220; S=640; C='#5500E5FF'}, @{X=760; Y=300; S=720; C='#557C4DFF'})) {
    $e = [System.Windows.Shapes.Ellipse]::new(); $e.Width = $halo.S; $e.Height = $halo.S
    $rg = [System.Windows.Media.RadialGradientBrush]::new()
    $rg.GradientStops.Add([System.Windows.Media.GradientStop]::new((Brush $halo.C).Color, 0))
    $rg.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Colors]::Transparent, 1))
    $e.Fill = $rg
    [System.Windows.Controls.Canvas]::SetLeft($e, $halo.X); [System.Windows.Controls.Canvas]::SetTop($e, $halo.Y)
    [void]$canvas.Children.Add($e)
}

# Rejilla de puntos.
$dots = [System.Windows.Media.DrawingGroup]::new()
$dots.Children.Add([System.Windows.Media.GeometryDrawing]::new((Brush '#14FFFFFF'), $null, [System.Windows.Media.EllipseGeometry]::new([System.Windows.Point]::new(2,2), 1.2, 1.2)))
$dotBrush = [System.Windows.Media.DrawingBrush]::new($dots)
$dotBrush.TileMode = 'Tile'; $dotBrush.Viewport = '0,0,22,22'; $dotBrush.ViewportUnits = 'Absolute'; $dotBrush.Viewbox = '0,0,22,22'; $dotBrush.ViewboxUnits = 'Absolute'
$grid = [System.Windows.Shapes.Rectangle]::new(); $grid.Width = $W; $grid.Height = $H; $grid.Fill = $dotBrush
[void]$canvas.Children.Add($grid)

function Add([System.Windows.UIElement]$el, [double]$x, [double]$y) {
    [System.Windows.Controls.Canvas]::SetLeft($el, $x); [System.Windows.Controls.Canvas]::SetTop($el, $y); [void]$canvas.Children.Add($el)
}

# Logo con brillo.
$logo = [System.Windows.Controls.Image]::new(); $logo.Source = Image (Join-Path $root 'src\Cefiro.App\Assets\cefiro-256.png')
$logo.Width = 104; $logo.Height = 104
$glow = [System.Windows.Media.Effects.DropShadowEffect]::new(); $glow.BlurRadius = 40; $glow.ShadowDepth = 0; $glow.Opacity = 0.8; $glow.Color = (Brush '#00E5FF').Color
$logo.Effect = $glow
Add $logo 72 96

function Text([string]$t, [double]$size, [string]$weight, [string]$color, [string]$family = 'Segoe UI Variable Display') {
    $tb = [System.Windows.Controls.TextBlock]::new(); $tb.Text = $t; $tb.FontSize = $size
    $tb.FontFamily = [System.Windows.Media.FontFamily]::new("$family, Segoe UI"); $tb.FontWeight = [System.Windows.FontWeights]::$weight
    $tb.Foreground = Brush $color; $tb
}

$title = Text 'Céfiro' 104 'SemiBold' '#F2F6FF'
$tg = [System.Windows.Media.LinearGradientBrush]::new((Brush '#F2F6FF').Color, (Brush '#7FEFFF').Color, 0)
$title.Foreground = $tg
Add $title 66 204

$tag = Text 'Ventiladores e iluminación RGB' 34 'Normal' '#F2F6FF'
Add $tag 74 350
$tag2 = Text 'con seguridad para tu líquida' 34 'Normal' '#9AA6C0'
Add $tag2 74 394

# Chips.
$x = 74
foreach ($chip in 'Curvas de ventilador', 'Efectos RGB', 'Perfiles', 'Seguridad') {
    $b = [System.Windows.Controls.Border]::new(); $b.CornerRadius = 14; $b.Padding = '14,7'
    $b.Background = Brush '#1AFFFFFF'; $b.BorderBrush = Brush '#3300E5FF'; $b.BorderThickness = 1
    $b.Child = Text $chip 17 'SemiBold' '#CFF8FF' 'Segoe UI Variable Text'
    $b.Measure([System.Windows.Size]::new([double]::PositiveInfinity, [double]::PositiveInfinity))
    Add $b $x 474
    $x += $b.DesiredSize.Width + 10
}

$foot = Text 'Windows 11  ·  .NET 10  ·  LibreHardwareMonitor  ·  OpenRGB' 17 'Normal' '#5D6880' 'Segoe UI Variable Text'
Add $foot 76 552

# Captura real del panel, con marco de cristal y sombra.
$shot = [System.Windows.Controls.Image]::new(); $shot.Source = Image (Join-Path $root 'docs\capturas\panel.png'); $shot.Stretch = 'UniformToFill'
$frame = [System.Windows.Controls.Border]::new(); $frame.Width = 596; $frame.Height = 378; $frame.CornerRadius = 18
$frame.BorderBrush = Brush '#5500E5FF'; $frame.BorderThickness = 2; $frame.ClipToBounds = $true
$inner = [System.Windows.Controls.Border]::new(); $inner.CornerRadius = 16; $inner.Child = $shot
$inner.Clip = [System.Windows.Media.RectangleGeometry]::new([System.Windows.Rect]::new(0,0,592,374), 16, 16)
$frame.Child = $inner
$shadow = [System.Windows.Media.Effects.DropShadowEffect]::new(); $shadow.BlurRadius = 60; $shadow.ShadowDepth = 0; $shadow.Opacity = 0.55; $shadow.Color = (Brush '#00E5FF').Color
$frame.Effect = $shadow
$frame.RenderTransformOrigin = '0.5,0.5'
$frame.RenderTransform = [System.Windows.Media.RotateTransform]::new(-3)
Add $frame 648 130

$canvas.Measure([System.Windows.Size]::new($W, $H)); $canvas.Arrange([System.Windows.Rect]::new(0, 0, $W, $H)); $canvas.UpdateLayout()
$rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($W, $H, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($canvas)
$png = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
$png.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$out = Join-Path $root 'docs\banner.png'
$fs = [IO.File]::Create($out); $png.Save($fs); $fs.Close()
"Banner: $out"
