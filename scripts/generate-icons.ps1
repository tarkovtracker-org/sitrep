# Generates multi-resolution Assets/app.ico and Assets/logo-64.png from the master logo.
# The master (SITREP.png at the repo root, ~1.3 MB) is kept local and gitignored; the generated
# assets are committed, so this only needs to run again when the master artwork changes.
param(
  [string]$Source = "$PSScriptRoot/../SITREP.png",
  [string]$DestDir = "$PSScriptRoot/../src/Sitrep.Desktop/Assets"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationCore

if (-not (Test-Path -LiteralPath $Source)) {
  throw "Source image not found: $Source"
}

if (-not (Test-Path -LiteralPath $DestDir)) {
  New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
}

$srcImage = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source).Path)
try {
  function Resize-Bmp([System.Drawing.Bitmap]$orig, [int]$w, [int]$h) {
    $dest = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dest)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($orig, 0, 0, $w, $h)
    $g.Dispose()
    return $dest
  }

  function Save-Png([System.Drawing.Bitmap]$bmp, [string]$outPath) {
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
  }

  # Header logo badge (MainWindow.xaml). Only assets referenced by the app are generated and embedded.
  $logo64 = Resize-Bmp $srcImage 64 64
  Save-Png $logo64 (Join-Path $DestDir "logo-64.png")
  $logo64.Dispose()

  # Build multi-resolution .ico (16, 24, 32, 48, 64, 128, 256)
  $icoSizes = @(16, 24, 32, 48, 64, 128, 256)
  $pngDataList = @()
  foreach ($s in $icoSizes) {
    $bmp = Resize-Bmp $srcImage $s $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngDataList += ,( $ms.ToArray() )
    $ms.Dispose()
    $bmp.Dispose()
  }

  $icoPath = Join-Path $DestDir "app.ico"
  $icoStream = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($icoStream)

  # Header: reserved (0), type (1 = icon), count
  $bw.Write([UInt16]0)
  $bw.Write([UInt16]1)
  $bw.Write([UInt16]$icoSizes.Count)

  # Directory entries (16 bytes each)
  $dataOffset = 6 + ($icoSizes.Count * 16)
  for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $size = $icoSizes[$i]
    $data = $pngDataList[$i]
    $dimByte = if ($size -ge 256) { [byte]0 } else { [byte]$size }

    $bw.Write($dimByte)              # bWidth
    $bw.Write($dimByte)              # bHeight
    $bw.Write([byte]0)               # bColorCount
    $bw.Write([byte]0)               # bReserved
    $bw.Write([UInt16]1)             # wPlanes
    $bw.Write([UInt16]32)            # wBitCount
    $bw.Write([UInt32]$data.Length)  # dwBytesInRes
    $bw.Write([UInt32]$dataOffset)   # dwImageOffset

    $dataOffset += $data.Length
  }

  # Image data chunks
  for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $bw.Write($pngDataList[$i])
  }

  [System.IO.File]::WriteAllBytes($icoPath, $icoStream.ToArray())
  $bw.Dispose()
  $icoStream.Dispose()

  # Verify created icon
  $verifyStream = [System.IO.File]::OpenRead($icoPath)
  try {
    $decoder = New-Object System.Windows.Media.Imaging.IconBitmapDecoder(
      $verifyStream,
      [System.Windows.Media.Imaging.BitmapCreateOptions]::None,
      [System.Windows.Media.Imaging.BitmapCacheOption]::Default
    )
    if ($decoder.Frames.Count -ne $icoSizes.Count) {
      throw "Decoded frame count ($($decoder.Frames.Count)) does not match expected ($($icoSizes.Count))."
    }
  } finally {
    $verifyStream.Dispose()
  }

  Write-Host "Successfully generated icon and logo assets in $DestDir"
} finally {
  $srcImage.Dispose()
}
