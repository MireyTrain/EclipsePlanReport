Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "EclipsePlanReport\debug\EclipsePlanReport.exe"
$assembly = [Reflection.Assembly]::LoadFrom($exe)

$renderUtils = $assembly.GetType("EclipsePlanReport.RenderUtils")
$viewEnum = $renderUtils.GetNestedType("ManikinView", [Reflection.BindingFlags]"NonPublic,Public")
$method = $renderUtils.GetMethod(
    "DrawManikin",
    [Reflection.BindingFlags]"Public,Static",
    $null,
    [Type[]]@([Windows.Media.DrawingContext], [double], [double], [double], $viewEnum),
    $null)

$visual = New-Object Windows.Media.DrawingVisual
$dc = $visual.RenderOpen()
$dc.DrawRectangle([Windows.Media.Brushes]::Black, $null, (New-Object Windows.Rect 0, 0, 760, 220))

$labels = @("ThreeD", "Frontal", "Sagittal", "Transversal")
for ($i = 0; $i -lt $labels.Count; $i++) {
    $x = 35 + $i * 185
    $dc.DrawRectangle(
        [Windows.Media.Brushes]::Black,
        (New-Object Windows.Media.Pen ([Windows.Media.Brushes]::DimGray), 1),
        (New-Object Windows.Rect $x, 20, 145, 165))

    $value = [Enum]::Parse($viewEnum, $labels[$i])
    $method.Invoke($null, @($dc, [double]($x + 25), [double]35, [double]120, $value)) | Out-Null

    $ft = New-Object Windows.Media.FormattedText(
        $labels[$i],
        [Globalization.CultureInfo]::InvariantCulture,
        [Windows.FlowDirection]::LeftToRight,
        (New-Object Windows.Media.Typeface "Segoe UI"),
        16,
        [Windows.Media.Brushes]::White,
        1.0)
    $dc.DrawText($ft, (New-Object Windows.Point ($x + 10), 190))
}

$dc.Close()

$bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap(
    760,
    220,
    96,
    96,
    [Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($visual)

$out = Join-Path $root "bildchen\manikin_vector_preview.png"
$encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$stream = [IO.File]::Create($out)
$encoder.Save($stream)
$stream.Close()

Write-Output $out
