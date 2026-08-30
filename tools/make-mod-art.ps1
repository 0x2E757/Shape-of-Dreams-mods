# Produces a mod's icon and preview from artwork drawn at the right aspect.
#
# The template wants 128x128 and 636x358, so the sources want to be square and 16:9. Given that,
# this is a straight downscale - no cropping, which would clip lettering, and no padding, which
# would leave bars along the edges.
#
# The reduction is the whole job and the reason this does not use GDI. HighQualityBicubic is a
# fixed four-tap kernel: taking 1254 down to 128 it reads four source pixels out of every hundred
# and aliases everything it steps over, which on lettering shows up as a crawling, broken edge. The
# filter below is a separable Mitchell whose support widens with the ratio, so every source pixel
# is accounted for.
#
# Colour is resampled premultiplied and unpremultiplied afterwards, so that art with transparency
# does not pick up a dark fringe where it meets nothing.
param(
    [Parameter(Mandatory = $true)][string]$IconSource,
    [Parameter(Mandatory = $true)][string]$PreviewSource,
    [Parameter(Mandatory = $true)][string]$OutDir
)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class ModArt
{
    private struct Weights
    {
        public int start;
        public float[] w;
    }

    private static float Mitchell(float x)
    {
        const float B = 1f / 3f, C = 1f / 3f;
        x = Math.Abs(x);
        float x2 = x * x, x3 = x2 * x;
        if (x < 1f)
            return ((12f - 9f * B - 6f * C) * x3 + (-18f + 12f * B + 6f * C) * x2 + (6f - 2f * B)) / 6f;
        if (x < 2f)
            return ((-B - 6f * C) * x3 + (6f * B + 30f * C) * x2 + (-12f * B - 48f * C) * x + (8f * B + 24f * C)) / 6f;
        return 0f;
    }

    private static Weights[] BuildWeights(int srcSize, int dstSize)
    {
        float scale = (float)srcSize / dstSize;
        float filterScale = Math.Max(1f, scale);
        float support = 2f * filterScale;

        var all = new Weights[dstSize];
        for (int i = 0; i < dstSize; i++)
        {
            float center = (i + 0.5f) * scale;
            int left = Math.Max(0, (int)Math.Floor(center - support));
            int right = Math.Min(srcSize - 1, (int)Math.Ceiling(center + support));

            var w = new float[right - left + 1];
            float total = 0f;
            for (int j = left; j <= right; j++)
            {
                float v = Mitchell((j + 0.5f - center) / filterScale);
                w[j - left] = v;
                total += v;
            }
            if (total > 0f) for (int k = 0; k < w.Length; k++) w[k] /= total;

            all[i] = new Weights { start = left, w = w };
        }
        return all;
    }

    private static float[] Resample(float[] src, int sw, int sh, int dw, int dh)
    {
        var wx = BuildWeights(sw, dw);
        var mid = new float[dw * sh];
        for (int y = 0; y < sh; y++)
        {
            int row = y * sw;
            for (int x = 0; x < dw; x++)
            {
                var weights = wx[x];
                float sum = 0f;
                for (int k = 0; k < weights.w.Length; k++) sum += src[row + weights.start + k] * weights.w[k];
                mid[y * dw + x] = sum;
            }
        }

        var wy = BuildWeights(sh, dh);
        var dst = new float[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            var weights = wy[y];
            for (int x = 0; x < dw; x++)
            {
                float sum = 0f;
                for (int k = 0; k < weights.w.Length; k++) sum += mid[(weights.start + k) * dw + x] * weights.w[k];
                dst[y * dw + x] = sum;
            }
        }
        return dst;
    }

    public static void Scale(Bitmap source, Bitmap output)
    {
        int w = source.Width, h = source.Height, n = w * h;
        var pr = new float[n];
        var pg = new float[n];
        var pb = new float[n];
        var pa = new float[n];

        var data = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4, p = y * w + x;
                    float a = row[i + 3];
                    float k = a / 255f;
                    pb[p] = row[i] * k;
                    pg[p] = row[i + 1] * k;
                    pr[p] = row[i + 2] * k;
                    pa[p] = a;
                }
            }
        }
        finally { source.UnlockBits(data); }

        int dw = output.Width, dh = output.Height;
        var rr = Resample(pr, w, h, dw, dh);
        var rg = Resample(pg, w, h, dw, dh);
        var rb = Resample(pb, w, h, dw, dh);
        var ra = Resample(pa, w, h, dw, dh);

        var outData = output.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[dw * 4];
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    int p = y * dw + x, i = x * 4;
                    float a = ra[p];
                    if (a <= 0.5f) { row[i] = row[i + 1] = row[i + 2] = row[i + 3] = 0; continue; }

                    row[i] = Clamp(rb[p] * 255f / a);
                    row[i + 1] = Clamp(rg[p] * 255f / a);
                    row[i + 2] = Clamp(rr[p] * 255f / a);
                    row[i + 3] = Clamp(a);
                }
                Marshal.Copy(row, 0, outData.Scan0 + y * outData.Stride, row.Length);
            }
        }
        finally { output.UnlockBits(outData); }
    }

    private static byte Clamp(float v)
    {
        if (v <= 0f) return 0;
        if (v >= 255f) return 255;
        return (byte)(v + 0.5f);
    }
}
"@ -ReferencedAssemblies ([System.Drawing.Bitmap].Assembly.Location)

function Resize-To([string]$source, [string]$destination, [int]$width, [int]$height) {
    $src = [System.Drawing.Bitmap]::new([System.Drawing.Image]::FromFile($source))
    try {
        $sourceAspect = $src.Width / $src.Height
        $targetAspect = $width / $height
        if ([Math]::Abs($sourceAspect - $targetAspect) -gt 0.01) {
            Write-Warning ("{0}: source is {1:N3}:1 but the target is {2:N3}:1, so it will be stretched" -f `
                (Split-Path $destination -Leaf), $sourceAspect, $targetAspect)
        }

        $canvas = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        [ModArt]::Scale($src, $canvas)
        $canvas.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
        $canvas.Dispose()

        "{0,-12} {1}x{2}  from {3}x{4}  {5:N0} bytes" -f `
            (Split-Path $destination -Leaf), $width, $height, $src.Width, $src.Height,
            (Get-Item $destination).Length
    }
    finally { $src.Dispose() }
}

Resize-To $IconSource (Join-Path $OutDir "icon.png") 128 128
Resize-To $PreviewSource (Join-Path $OutDir "preview.png") 636 358
