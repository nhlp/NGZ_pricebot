using SkiaSharp;

public class PriceStamper
{
    /// <summary>Tek bir kod/fiyat için geriye dönük uyumlu sarmalayıcı: eskisi gibi sade
    /// "$fiyat" basar (kod yazılmaz).</summary>
    public static string Stamp(string sourcePath, string outputDir, decimal priceUsd) =>
        Stamp(sourcePath, outputDir, [(string.Empty, priceUsd)]);

    /// <summary>Görselin sağ altına fiyat(ları) damgalar. Tek girdi varsa eskisi gibi sade
    /// "$fiyat" yazılır (kod gösterilmez). Birden fazla girdi varsa (aynı görsel birden fazla
    /// Excel koduyla eşleşti — ör. tek fotoğraf birden fazla yaş/beden grubunu temsil ediyorsa,
    /// 2026-08 vakası) her biri "kod: $fiyat" olarak, daha küçük fontla, alt alta basılır; tümü
    /// sığması için tek satırlık büyük font yerine çok satırlı küçük font kullanılır.</summary>
    public static string Stamp(string sourcePath, string outputDir, IReadOnlyList<(string Code, decimal PriceUsd)> entries)
    {
        if (entries.Count == 0)
            throw new ArgumentException("En az bir fiyat girdisi gerekli.", nameof(entries));

        using var src = SKBitmap.Decode(sourcePath)
            ?? throw new InvalidDataException("Görsel açılamadı");

        var scale = Math.Min(1.0, 1600.0 / Math.Max(src.Width, src.Height));
        var info = new SKImageInfo((int)(src.Width * scale), (int)(src.Height * scale));

        using var resized = new SKBitmap(info);
        using (var resizeCanvas = new SKCanvas(resized))
        {
            resizeCanvas.Clear(SKColors.Transparent);
            resizeCanvas.DrawBitmap(src, new SKRect(0, 0, src.Width, src.Height), new SKRect(0, 0, info.Width, info.Height));
        }

        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.DrawBitmap(resized, 0, 0);

        bool multi = entries.Count > 1;
        var texts = multi
            ? entries.Select(e => $"{e.Code}: ${e.PriceUsd:N2}").ToArray()
            : [$"${entries[0].PriceUsd:N2}"];

        // Metin boyutu ve boya ayarları — çok satırlı olduğunda tek satırlık orijinal fonttan
        // (0.05 × yükseklik) daha küçük bir font kullanılır ki hepsi köşeye sığsın.
        float fontSize = info.Height * (multi ? 0.032f : 0.05f);
        using var textPaint = new SKPaint
        {
            Color = new SKColor(30, 30, 30),
            IsAntialias = true,
            TextSize = fontSize,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        float textWidth = texts.Max(t => textPaint.MeasureText(t));

        float pad = fontSize * 0.5f;
        float margin = fontSize * 0.4f;
        float lineHeight = fontSize * 1.35f;
        float bandHeight = lineHeight * texts.Length + pad * 1.2f;

        // Sağ Alt Köşe Kutusu
        var band = new SKRect(
            info.Width - textWidth - pad * 2 - margin,
            info.Height - bandHeight - margin,
            info.Width - margin,
            info.Height - margin
        );

        using var bandPaint = new SKPaint { Color = SKColors.White.WithAlpha(200), IsAntialias = true };
        float cornerRadius = fontSize * 0.5f;
        canvas.DrawRoundRect(band, cornerRadius, cornerRadius, bandPaint);

        var m = textPaint.FontMetrics;
        for (int i = 0; i < texts.Length; i++)
        {
            float lineCenterY = band.Top + pad * 0.6f + lineHeight * i + lineHeight / 2;
            float yPos = lineCenterY - (m.Ascent + m.Descent) / 2;
            canvas.DrawText(texts[i], band.Left + pad, yPos, textPaint);
        }

        var outPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(sourcePath) + "_fiyatli.jpg");

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var fs = File.OpenWrite(outPath);
        data.SaveTo(fs);

        return outPath;
    }
}
