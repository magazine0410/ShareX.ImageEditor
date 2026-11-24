#region License Information (GPL v3)

/*
    ShareX.ImageEditor - The UI-agnostic Editor library for ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Avalonia.Media.Imaging;
using ShareX.ImageEditor.Presentation.Rendering;
using SkiaSharp;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;
using static Vortice.Direct2D1.D2D1;
using static Vortice.DirectWrite.DWrite;

namespace ShareX.ImageEditor.Presentation.Emoji;

public static class WindowsEmojiBitmapRenderer
{
    private const string EmojiFontFamily = "Segoe UI Emoji";
    private const int PreviewPadding = 6;
    private const int StickerPadding = 14;
    private const int MaxStickerCacheEntries = 256;
    private const float RawCanvasScale = 2.0f;
    private const float RawFontScale = 1.32f;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, WeakReference<Bitmap>> PreviewCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SKBitmap> StickerCache = new(StringComparer.Ordinal);
    private static readonly ID2D1Factory7? D2DFactory;
    private static readonly IDWriteFactory? DWriteFactoryInstance;
    private static readonly IWICImagingFactory? WicFactory;

    // Shaping positions are in these units and scaled to the font size when drawing.
    private const int SystemEmojiShapingScale = 1024;
    private static bool SystemEmojiFontResolved;
    private static SKTypeface? SystemEmojiTypeface;
    private static HarfBuzzSharp.Font? SystemEmojiShapingFont;

    static WindowsEmojiBitmapRenderer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        D2D1CreateFactory(out D2DFactory);
        DWriteCreateFactory(out DWriteFactoryInstance);
        WicFactory = new IWICImagingFactory();
    }

    public static Bitmap? RenderPreviewBitmap(string unicodeSequence, int size)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        string cacheKey = $"{unicodeSequence}:{size}";

        lock (SyncRoot)
        {
            if (PreviewCache.TryGetValue(cacheKey, out var reference) && reference.TryGetTarget(out Bitmap? cached))
            {
                return cached;
            }

            using SKBitmap? bitmap = RenderSquareBitmap(unicodeSequence, size, PreviewPadding);
            if (bitmap == null)
            {
                return null;
            }

            Bitmap preview = BitmapConversionHelpers.ToAvaloniBitmap(bitmap);
            // Controls borrow previews; evict references without disposing images still in use.
            if (PreviewCache.Count >= 256) PreviewCache.Clear();
            PreviewCache[cacheKey] = new WeakReference<Bitmap>(preview);
            return preview;
        }
    }

    public static SKBitmap? RenderStickerBitmap(string unicodeSequence, int size = 160)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        return RenderStickerBitmapCore(unicodeSequence, size);
    }

    public static SKBitmap? RenderInteractiveStickerBitmap(string unicodeSequence, int size = 160)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        return RenderStickerBitmapCore(unicodeSequence, GetInteractiveStickerSize(size));
    }

    public static int GetInteractiveStickerSize(int size)
    {
        size = Math.Max(1, size);

        if (size <= 64)
        {
            return size;
        }

        int step = size <= 128 ? 4 : size <= 256 ? 8 : 12;
        return Math.Max(64, (int)Math.Round(size / (double)step) * step);
    }

    private static SKBitmap? RenderSquareBitmap(string unicodeSequence, int size, int padding)
    {
        string glyph = EmojiCatalogService.ToGlyph(unicodeSequence);
        if (string.IsNullOrEmpty(glyph))
        {
            return null;
        }

        try
        {
            int rawCanvasSize = Math.Max(64, (int)Math.Ceiling(size * RawCanvasScale));
            float rawFontSize = Math.Max(24, size * RawFontScale);

            using SKBitmap? rawBitmap = RenderWithDirect2D(glyph, rawCanvasSize, rawFontSize) ?? RenderWithSkiaFallback(glyph, rawCanvasSize);
            if (rawBitmap == null)
            {
                return null;
            }

            using SKBitmap trimmedBitmap = TrimTransparentBounds(rawBitmap) ?? rawBitmap.Copy();
            return FitIntoSquare(trimmedBitmap, size, padding);
        }
        catch
        {
            return RenderWithSkiaFallback(glyph, size);
        }
    }

    private static SKBitmap? RenderWithDirect2D(string glyph, int canvasSize, float fontSize)
    {
        if (!OperatingSystem.IsWindows() || D2DFactory == null || DWriteFactoryInstance == null || WicFactory == null)
        {
            return null;
        }

        using IWICBitmap wicBitmap = WicFactory.CreateBitmap((uint)canvasSize, (uint)canvasSize, PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        using ID2D1RenderTarget renderTarget = D2DFactory.CreateWicBitmapRenderTarget(wicBitmap, new RenderTargetProperties());
        using ID2D1SolidColorBrush brush = renderTarget.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        using IDWriteTextFormat textFormat = DWriteFactoryInstance.CreateTextFormat(EmojiFontFamily, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, fontSize);
        using IDWriteTextLayout textLayout = DWriteFactoryInstance.CreateTextLayout(glyph, textFormat, canvasSize, canvasSize);

        textFormat.TextAlignment = TextAlignment.Center;
        textFormat.ParagraphAlignment = ParagraphAlignment.Center;

        renderTarget.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        renderTarget.BeginDraw();
        renderTarget.Clear(new Color4(0f, 0f, 0f, 0f));
        renderTarget.DrawTextLayout(new System.Numerics.Vector2(0f, 0f), textLayout, brush, DrawTextOptions.EnableColorFont);
        renderTarget.EndDraw();

        return CopyWicBitmapToSkBitmap(wicBitmap, canvasSize, canvasSize);
    }

    private static SKBitmap? RenderWithSkiaFallback(string glyph, int canvasSize)
    {
        // Linux has no "Segoe UI Emoji", and the default typeface has no emoji at all.
        if (OperatingSystem.IsLinux() && RenderWithSystemEmojiFont(glyph, canvasSize) is { } systemEmoji)
        {
            return systemEmoji;
        }

        using SKTypeface? typeface = SKFontManager.Default.MatchFamily(EmojiFontFamily) ?? SKTypeface.Default;
        using var rawBitmap = new SKBitmap(new SKImageInfo(canvasSize, canvasSize, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(rawBitmap);
        using var font = new SKFont(typeface, canvasSize * 0.68f);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White
        };

        canvas.Clear(SKColors.Transparent);

        SKRect bounds = default;
        font.MeasureText(glyph, out bounds);

        float x = (canvasSize - bounds.Width) / 2f - bounds.Left;
        float y = (canvasSize - bounds.Height) / 2f - bounds.Top;

        canvas.DrawText(glyph, x, y, font, paint);
        canvas.Flush();

        return rawBitmap.Copy();
    }

    /// <summary>
    /// Draws the emoji with the system's color emoji font (Noto Color Emoji on most Linux systems).
    /// The sequence is shaped with HarfBuzz first, because drawing its code points one by one
    /// shows a skin tone, a flag or a joined emoji as separate glyphs.
    /// Returns null when the system has no emoji font or the font lacks the emoji.
    /// </summary>
    private static SKBitmap? RenderWithSystemEmojiFont(string glyph, int canvasSize)
    {
        if (!TryGetSystemEmojiFont(out SKTypeface? typeface, out HarfBuzzSharp.Font? shapingFont))
        {
            return null;
        }

        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(glyph);
        buffer.GuessSegmentProperties();
        shapingFont.Shape(buffer);

        HarfBuzzSharp.GlyphInfo[] infos = buffer.GlyphInfos;
        HarfBuzzSharp.GlyphPosition[] positions = buffer.GlyphPositions;
        if (infos.Length == 0 || infos.Any(info => info.Codepoint == 0))
        {
            return null;
        }

        float fontSize = canvasSize * 0.68f;
        float scale = fontSize / SystemEmojiShapingScale;
        var glyphIds = new ushort[infos.Length];
        var points = new SKPoint[infos.Length];
        float penX = 0;
        float penY = 0;
        for (int i = 0; i < infos.Length; i++)
        {
            glyphIds[i] = (ushort)infos[i].Codepoint;
            points[i] = new SKPoint(penX + positions[i].XOffset * scale, penY - positions[i].YOffset * scale);
            penX += positions[i].XAdvance * scale;
            penY -= positions[i].YAdvance * scale;
        }

        using var font = new SKFont(typeface, fontSize);
        using SKTextBlob? blob = SKTextBlob.CreatePositioned(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(glyphIds.AsSpan()), SKTextEncoding.GlyphId, font, points);
        if (blob == null)
        {
            return null;
        }

        var bitmap = new SKBitmap(new SKImageInfo(canvasSize, canvasSize, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.Clear(SKColors.Transparent);

        // The caller trims and centers the result; it only has to be inside the canvas here.
        SKRect bounds = blob.Bounds;
        canvas.DrawText(blob, (canvasSize - bounds.Width) / 2f - bounds.Left, (canvasSize - bounds.Height) / 2f - bounds.Top, paint);
        canvas.Flush();
        return bitmap;
    }

    private static bool TryGetSystemEmojiFont(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SKTypeface? typeface,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HarfBuzzSharp.Font? shapingFont)
    {
        // Callers hold SyncRoot, so the fonts are created once and never used by two threads at a time.
        if (!SystemEmojiFontResolved)
        {
            SystemEmojiFontResolved = true;
            try
            {
                // "Zsye" asks for the emoji presentation; without it a monochrome symbol font can win.
                SKTypeface? match = SKFontManager.Default.MatchCharacter(null, SKFontStyle.Normal, ["und-Zsye"], 0x1F600);
                if (match != null)
                {
                    using SKStreamAsset stream = match.OpenStream(out int faceIndex);
                    int length = stream.Length;
                    IntPtr data = System.Runtime.InteropServices.Marshal.AllocHGlobal(length);
                    stream.Read(data, length);
                    // The blob owns the copy of the font file for as long as the shaping font lives.
                    using var fontData = new HarfBuzzSharp.Blob(data, length, HarfBuzzSharp.MemoryMode.ReadOnly,
                        () => System.Runtime.InteropServices.Marshal.FreeHGlobal(data));
                    using var face = new HarfBuzzSharp.Face(fontData, faceIndex);
                    var font = new HarfBuzzSharp.Font(face);
                    font.SetScale(SystemEmojiShapingScale, SystemEmojiShapingScale);
                    font.SetFunctionsOpenType();
                    SystemEmojiTypeface = match;
                    SystemEmojiShapingFont = font;
                }
            }
            catch
            {
                SystemEmojiTypeface = null;
                SystemEmojiShapingFont = null;
            }
        }

        typeface = SystemEmojiTypeface;
        shapingFont = SystemEmojiShapingFont;
        return typeface != null && shapingFont != null;
    }

    private static SKBitmap CopyWicBitmapToSkBitmap(IWICBitmap wicBitmap, int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        wicBitmap.CopyPixels((uint)stride, pixels);

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    private static SKBitmap? TrimTransparentBounds(SKBitmap source)
    {
        int minX = source.Width;
        int minY = source.Height;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).Alpha == 0)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return null;
        }

        int width = Math.Max(1, maxX - minX + 1);
        int height = Math.Max(1, maxY - minY + 1);
        var trimmed = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        using var canvas = new SKCanvas(trimmed);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(
            source,
            new SKRectI(minX, minY, maxX + 1, maxY + 1),
            new SKRect(0, 0, width, height));

        return trimmed;
    }

    private static SKBitmap FitIntoSquare(SKBitmap source, int size, int padding)
    {
        var output = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(output);
        using var paint = new SKPaint
        {
            IsAntialias = true
        };

        canvas.Clear(SKColors.Transparent);

        float availableWidth = Math.Max(1, size - (padding * 2));
        float availableHeight = Math.Max(1, size - (padding * 2));
        float scale = Math.Min(availableWidth / source.Width, availableHeight / source.Height);
        float drawWidth = Math.Max(1, source.Width * scale);
        float drawHeight = Math.Max(1, source.Height * scale);
        float left = (size - drawWidth) / 2f;
        float top = (size - drawHeight) / 2f;

        using SKImage sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(sourceImage, new SKRect(left, top, left + drawWidth, top + drawHeight), new SKSamplingOptions(SKCubicResampler.CatmullRom), paint);
        return output;
    }

    private static SKBitmap? RenderStickerBitmapCore(string unicodeSequence, int size)
    {
        string cacheKey = $"{unicodeSequence}:{size}";

        lock (SyncRoot)
        {
            if (StickerCache.TryGetValue(cacheKey, out SKBitmap? cachedBitmap))
            {
                return cachedBitmap.Copy();
            }

            SKBitmap? renderedBitmap = RenderSquareBitmap(unicodeSequence, size, StickerPadding);
            if (renderedBitmap == null)
            {
                return null;
            }

            if (StickerCache.Count >= MaxStickerCacheEntries)
            {
                ClearStickerCache();
            }

            StickerCache[cacheKey] = renderedBitmap.Copy();
            return renderedBitmap;
        }
    }

    private static void ClearStickerCache()
    {
        foreach (SKBitmap bitmap in StickerCache.Values)
        {
            bitmap.Dispose();
        }

        StickerCache.Clear();
    }
}
