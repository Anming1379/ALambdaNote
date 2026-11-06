param(
    [string]$Png = (Join-Path $PSScriptRoot "..\assets\text-file.png"),
    [string]$Ico = (Join-Path $PSScriptRoot "..\assets\text-file.ico")
)

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Xaml

$size = 1080
$scale = $size / 40.0
$visual = New-Object Windows.Media.DrawingVisual
$context = $visual.RenderOpen()

$paperBrush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#FFFFFF'))
$outlineBrush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#D5D7DA'))
$outlinePen = New-Object Windows.Media.Pen($outlineBrush, 1.5)
$outlinePen.LineJoin = [Windows.Media.PenLineJoin]::Round

$blueBrush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#155EEF'))
$bluePen = New-Object Windows.Media.Pen($blueBrush, 1.5)
$bluePen.StartLineCap = [Windows.Media.PenLineCap]::Round
$bluePen.EndLineCap = [Windows.Media.PenLineCap]::Round
$bluePen.LineJoin = [Windows.Media.PenLineJoin]::Round

try {
    $context.PushTransform((New-Object Windows.Media.ScaleTransform($scale, $scale)))

    $outline = [Windows.Media.Geometry]::Parse('M4.75 4A3.25 3.25 0 0 1 8 .75h16c.121 0 .238.048.323.134l10.793 10.793a.46.46 0 0 1 .134.323v24A3.25 3.25 0 0 1 32 39.25H8A3.25 3.25 0 0 1 4.75 36z')
    $context.DrawGeometry($paperBrush, $outlinePen, $outline)

    $fold = [Windows.Media.Geometry]::Parse('M24 .5V8a4 4 0 0 0 4 4h7.5')
    $context.DrawGeometry($paperBrush, $outlinePen, $fold)

    $lines = [Windows.Media.Geometry]::Parse('M11.9 19.5h16.2m-16.2 3.6h16.2m-16.2 3.6h16.2m-16.2 3.6h12.6')
    $context.DrawGeometry($null, $bluePen, $lines)
}
finally {
    $context.Pop()
    $context.Close()
}

$bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)

$encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$pngPath = [IO.Path]::GetFullPath($Png)
$stream = [IO.File]::Create($pngPath)
try {
    $encoder.Save($stream)
}
finally {
    $stream.Dispose()
}

& (Join-Path $PSScriptRoot 'make-icon.ps1') -Source $pngPath -Target ([IO.Path]::GetFullPath($Ico))
Write-Output "Text icon written: $([IO.Path]::GetFullPath($Ico))"
