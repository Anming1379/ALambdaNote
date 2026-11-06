param(
    [string]$Source = (Join-Path $PSScriptRoot "..\assets\icon3.png"),
    [string]$Target = (Join-Path $PSScriptRoot "..\assets\app.ico")
)

Add-Type -AssemblyName System.Drawing

$builder = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IconBuilder
{
    public static void Build(string source, string target)
    {
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        byte[][] payloads = new byte[sizes.Length][];

        using (Image image = Image.FromFile(source))
        {
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.Clear(Color.Transparent);
                        graphics.DrawImage(image, new Rectangle(0, 0, size, size));
                    }

                    payloads[i] = BuildDib(bitmap);
                }
            }
        }

        WriteIco(target, sizes, payloads);
    }

    private static byte[] BuildDib(Bitmap bitmap)
    {
        int size = bitmap.Width;
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, size, size),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        byte[] pixels = new byte[size * size * 4];
        try
        {
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride),
                    pixels,
                    y * size * 4,
                    size * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        int maskStride = ((size + 31) / 32) * 4;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write((uint)40);
            writer.Write(size);
            writer.Write(size * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)0);
            writer.Write((uint)(size * size * 4));
            writer.Write((uint)0);
            writer.Write((uint)0);
            writer.Write((uint)0);
            writer.Write((uint)0);

            for (int y = size - 1; y >= 0; y--)
            {
                writer.Write(pixels, y * size * 4, size * 4);
            }

            for (int y = size - 1; y >= 0; y--)
            {
                byte[] maskRow = new byte[maskStride];
                for (int x = 0; x < size; x++)
                {
                    if (pixels[(y * size + x) * 4 + 3] < 128)
                    {
                        maskRow[x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }

                writer.Write(maskRow);
            }

            writer.Flush();
            return stream.ToArray();
        }
    }

    private static void WriteIco(string target, int[] sizes, byte[][] payloads)
    {
        int offset = 6 + (16 * sizes.Length);
        using (FileStream stream = File.Create(target))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);

            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)payloads[i].Length);
                writer.Write((uint)offset);
                offset += payloads[i].Length;
            }

            for (int i = 0; i < payloads.Length; i++)
            {
                writer.Write(payloads[i]);
            }
        }
    }
}
'@

if (-not ("IconBuilder" -as [type])) {
    Add-Type -TypeDefinition $builder -ReferencedAssemblies System.Drawing
}

$sourcePath = (Resolve-Path $Source).Path
$targetPath = [IO.Path]::GetFullPath($Target)
[IconBuilder]::Build($sourcePath, $targetPath)
Write-Output "Icon written: $targetPath"
