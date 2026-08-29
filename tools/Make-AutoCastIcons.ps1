# Turns the hand-cut layers into the PNGs the mod embeds, plus the grey plate the control sits on.
#
# Each state is two layers, because the arrows turn and the frame does not. They arrive already
# cut and already sharing one canvas, so all this does is frame and scale them - identically, so
# that stacking them at rest reproduces the original icon and nothing shifts between states.
#
# The layers carry a real alpha channel. That is worth stating because a detour was spent without
# one: copies that had been flattened onto black, where alpha had to be guessed back from
# brightness. That guess is only right for art that is pure glow, and these are not - the silver
# icon is grey metal inside a dark outline, so brightness as alpha left the outline four fifths
# transparent and the shape lost the line that framed it. Take the alpha that is there.
#
# Two things still have to be right:
#
#  - Resample premultiplied, unpremultiply at the end. In the other order every edge keeps a dark
#    halo, because a transparent pixel's colour is meaningless and averaging it in is meaningless
#    too.
#  - Resample properly. GDI's HighQualityBicubic is a fixed four-tap kernel, so reducing by five
#    it reads four source pixels out of every twenty-six and aliases the result, which is exactly
#    what makes an edge look chopped. The filter below widens its support with the reduction.
param(
    [string]$LayerDir = "",
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Size = 256,
    [double]$Padding = 0.04
)

if ([string]::IsNullOrEmpty($LayerDir)) { $LayerDir = Join-Path $PSScriptRoot "layers" }

$names = @(
    "autocast_off_ring", "autocast_off_arrows",
    "autocast_on_ring", "autocast_on_arrows",
    "autocast_locked_ring", "autocast_locked_arrows"
)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class Layers
{
    private struct Weights
    {
        public int start;
        public float[] w;
    }

    // Mitchell-Netravali with B = C = 1/3: the usual choice for reduction, sharp enough without
    // the negative lobes that would let a Catmull-Rom ring an edge into transparency.
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

    // The support widens with the reduction, which is the whole point: at 1:1 it spans the usual
    // two pixels, and reducing by five it spans ten, so nothing is skipped over.
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

    public static string Build(Bitmap cell, Bitmap output)
    {
        int w = cell.Width, h = cell.Height, n = w * h;
        var pr = new float[n];
        var pg = new float[n];
        var pb = new float[n];
        var pa = new float[n];

        var hist = new int[256];
        var data = cell.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4, p = y * w + x;
                    int a = row[i + 3];
                    hist[a]++;

                    // Premultiplied, which is the only space an average of neighbouring pixels
                    // means anything in.
                    float k = a / 255f;
                    pb[p] = row[i] * k;
                    pg[p] = row[i + 1] * k;
                    pr[p] = row[i + 2] * k;
                    pa[p] = a;
                }
            }
        }
        finally { cell.UnlockBits(data); }

        // These export a solid interior at 253 rather than 255 - a whole icon sitting at 99%
        // opacity for no reason. Normalising against the highest value present does almost
        // nothing, because the few pixels at 254 are strays; what has to be found is the plateau,
        // the value the solid body of the art actually sits at. Left alone unless it is high
        // enough to be a solid body in the first place, so genuinely translucent art is not
        // blown up.
        int plateau = 255;
        int most = 0;
        for (int a = 192; a <= 255; a++)
        {
            if (hist[a] <= most) continue;
            most = hist[a];
            plateau = a;
        }
        float gain = plateau >= 200 ? 255f / plateau : 1f;

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

                    // Colour comes back out of premultiplied space against the alpha it went in
                    // with; only then is the alpha itself stretched.
                    row[i] = Clamp(rb[p] * 255f / a);
                    row[i + 1] = Clamp(rg[p] * 255f / a);
                    row[i + 2] = Clamp(rr[p] * 255f / a);
                    row[i + 3] = Clamp(a * gain);
                }
                Marshal.Copy(row, 0, outData.Scan0 + y * outData.Stride, row.Length);
            }
        }
        finally { output.UnlockBits(outData); }

        return string.Format("plateau {0} -> 255", plateau);
    }

    private static byte Clamp(float v)
    {
        if (v <= 0f) return 0;
        if (v >= 255f) return 255;
        return (byte)(v + 0.5f);
    }
}
"@ -ReferencedAssemblies ([System.Drawing.Bitmap].Assembly.Location)

# One square cell for every layer, big enough for the widest of them plus a margin, so nothing
# touches the sprite edge - a ring flush against it bleeds when the mip chain is built.
$sources = @{}
$cell = 0
foreach ($n in $names) {
    $path = Join-Path $LayerDir "$n.png"
    if (-not (Test-Path $path)) { throw "missing layer: $path" }
    $img = [System.Drawing.Bitmap]::new([System.Drawing.Image]::FromFile($path))
    $sources[$n] = $img
    $cell = [Math]::Max($cell, [Math]::Max($img.Width, $img.Height))
}
$cell = [int][Math]::Ceiling($cell * (1.0 + $Padding))

try {
    foreach ($n in $names) {
        $src = $sources[$n]

        # Centred on the cell rather than cropped to its own content: the layers share a canvas,
        # and framing each to its own bounds would pull the arrows off the ring. Copied rather
        # than blended, so the alpha arrives untouched.
        $square = New-Object System.Drawing.Bitmap($cell, $cell, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($square)
        try {
            $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.DrawImage($src, [int](($cell - $src.Width) / 2), [int](($cell - $src.Height) / 2), $src.Width, $src.Height)
        }
        finally { $g.Dispose() }

        $canvas = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stats = [Layers]::Build($square, $canvas)
        $square.Dispose()

        $path = Join-Path $OutDir "$n.png"
        $canvas.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $canvas.Dispose()

        "{0,-24} {1}x{1}  {2}  {3:N0} bytes" -f $n, $Size, $stats, (Get-Item $path).Length
    }
}
finally { foreach ($img in $sources.Values) { $img.Dispose() } }

# A plain white disc with a clean edge, drawn at four times the size and scaled down because GDI
# antialiasing in one pass leaves a visibly stepped rim. It carries no colour of its own: how
# grey, how solid and how large are all decided at runtime.
$super = $Size * 4
$big = New-Object System.Drawing.Bitmap($super, $super, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($big)
try {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.FillEllipse([System.Drawing.Brushes]::White, 0, 0, ($super - 1), ($super - 1))
}
finally { $g.Dispose() }

$plate = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($plate)
try {
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($big, 0, 0, $Size, $Size)
}
finally { $g.Dispose(); $big.Dispose() }

$path = Join-Path $OutDir "plate.png"
$plate.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$plate.Dispose()
"{0,-24} {1}x{1}  {2:N0} bytes" -f "plate", $Size, (Get-Item $path).Length
