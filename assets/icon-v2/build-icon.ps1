param(
    [string]$SourcePng = (Join-Path $PSScriptRoot 'prompt-optimizer-v2.png'),
    [string]$OutputIco = (Join-Path $PSScriptRoot 'prompt-optimizer-v2.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if(-not ('PromptIconNativeCheck' -as [type])){
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PromptIconNativeCheck {
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr LoadImage(IntPtr instance,string name,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
}
'@
}
$source = [Drawing.Bitmap]::new($SourcePng)
$frames = [Collections.Generic.List[object]]::new()
try {
    foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
        $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source,[Drawing.Rectangle]::new(0,0,$size,$size))
            $memory = [IO.MemoryStream]::new()
            try {
                $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
                $frames.Add([pscustomobject]@{Size=$size;Bytes=$memory.ToArray()})
            } finally { $memory.Dispose() }
        } finally { $graphics.Dispose();$bitmap.Dispose() }
    }
    $output = [IO.File]::Create($OutputIco)
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
            $writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    } finally { $writer.Dispose();$output.Dispose() }
    foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
        $handle = [PromptIconNativeCheck]::LoadImage([IntPtr]::Zero,$OutputIco,1,$size,$size,0x10)
        if($handle -eq [IntPtr]::Zero){throw "Windows could not load icon at $size"}
        $icon = [Drawing.Icon]::FromHandle($handle)
        try { if($icon.Width -ne $size -or $icon.Height -ne $size){throw "Unexpected icon dimensions for $size"} }
        finally { $icon.Dispose();[void][PromptIconNativeCheck]::DestroyIcon($handle) }
    }
    [pscustomobject]@{
        SourceWidth=$source.Width
        SourceHeight=$source.Height
        TransparentCorner=($source.GetPixel(0,0).A -eq 0)
        Sizes=@($frames | ForEach-Object Size)
        OutputIco=$OutputIco
    } | ConvertTo-Json
} finally { $source.Dispose() }
