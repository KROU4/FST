$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$fstIconPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Fst.App\Assets\fst.ico'))
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fstIconPath)) | Out-Null
$fstImages = @()
foreach ($fstSize in @(16,20,24,32,48,64,128,256)) {
    $fstBitmap = [Drawing.Bitmap]::new($fstSize,$fstSize)
    $fstGraphics = [Drawing.Graphics]::FromImage($fstBitmap)
    $fstGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $fstGraphics.Clear([Drawing.Color]::Transparent)
    $fstGraphics.ScaleTransform($fstSize/32.0,$fstSize/32.0)
    $fstBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(230,230,222))
    $fstGraphics.FillRectangle([Drawing.Brushes]::Black,1,1,30,30)
    $fstGraphics.FillRectangle($fstBrush,7,7,4,18)
    $fstGraphics.FillRectangle($fstBrush,11,7,14,4)
    $fstGraphics.FillRectangle($fstBrush,11,14,10,4)
    $fstGraphics.FillRectangle($fstBrush,22,22,3,3)
    $fstMemory = [IO.MemoryStream]::new()
    $fstBitmap.Save($fstMemory,[Drawing.Imaging.ImageFormat]::Png)
    $fstImages += ,@($fstSize,$fstMemory.ToArray())
    $fstMemory.Dispose();$fstGraphics.Dispose();$fstBrush.Dispose();$fstBitmap.Dispose()
}
$fstOutput = [IO.File]::Create($fstIconPath)
$fstWriter = [IO.BinaryWriter]::new($fstOutput)
$fstWriter.Write([uint16]0);$fstWriter.Write([uint16]1);$fstWriter.Write([uint16]$fstImages.Count)
$fstOffset = 6 + 16*$fstImages.Count
foreach ($fstImage in $fstImages) {
    $fstDimension = if($fstImage[0] -eq 256){0}else{$fstImage[0]}
    $fstWriter.Write([byte]$fstDimension);$fstWriter.Write([byte]$fstDimension);$fstWriter.Write([byte]0);$fstWriter.Write([byte]0)
    $fstWriter.Write([uint16]1);$fstWriter.Write([uint16]32);$fstWriter.Write([uint32]$fstImage[1].Length);$fstWriter.Write([uint32]$fstOffset)
    $fstOffset += $fstImage[1].Length
}
foreach($fstImage in $fstImages){$fstWriter.Write([byte[]]$fstImage[1])}
$fstWriter.Dispose()
