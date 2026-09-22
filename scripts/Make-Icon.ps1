Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64, 64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(23, 107, 80))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 4
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($pen, 16, 19, 47, 19)
$graphics.DrawLine($pen, 16, 31, 37, 31)
$graphics.DrawLine($pen, 16, 43, 30, 43)
$graphics.DrawLine($pen, 41, 38, 49, 46)
$graphics.DrawLine($pen, 41, 46, 49, 46)
$graphics.DrawLine($pen, 49, 38, 49, 46)
$png = New-Object System.IO.MemoryStream
$bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$bytes = $png.ToArray()
$path = Join-Path $PSScriptRoot '..\src\LineRename.App\app.ico'
$stream = [System.IO.File]::Create($path)
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]64)
$writer.Write([byte]64)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$bytes.Length)
$writer.Write([uint32]22)
$writer.Write($bytes)
$writer.Dispose()
$png.Dispose()
$graphics.Dispose()
$pen.Dispose()
$bitmap.Dispose()
