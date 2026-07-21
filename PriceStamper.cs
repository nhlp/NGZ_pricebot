using System;
using System.IO;
using SkiaSharp;

public class PriceStamper
{
    public static string Stamp(string sourcePath, string outputDir, decimal priceUsd)
    {
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

        var text = $"${priceUsd:N2}";
        
        // Metin boyutu ve boya ayarları
        float fontSize = info.Height * 0.05f;
        using var textPaint = new SKPaint
        {
            Color = new SKColor(30, 30, 30),
            IsAntialias = true,
            TextSize = fontSize,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };
        
        // String kabul eden güvenli MeasureText metodu:
        float textWidth = textPaint.MeasureText(text);
        
        float pad = fontSize * 0.5f;
        float margin = fontSize * 0.4f;
        float bandHeight = fontSize + pad * 1.6f;

        // Sağ Alt Köşe Kutusu
        var band = new SKRect(
            info.Width - textWidth - pad * 2 - margin,
            info.Height - bandHeight - margin,
            info.Width - margin,
            info.Height - margin
        );

        using var bandPaint = new SKPaint { Color = SKColors.White.WithAlpha(200), IsAntialias = true };
        canvas.DrawRoundRect(band, band.Height * 0.35f, band.Height * 0.35f, bandPaint);

        var m = textPaint.FontMetrics;
        float yPos = band.MidY - (m.Ascent + m.Descent) / 2;
        
        // String kabul eden güvenli DrawText metodu:
        canvas.DrawText(text, band.Left + pad, yPos, textPaint);

        var outPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(sourcePath) + "_fiyatli.jpg");

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var fs = File.OpenWrite(outPath);
        data.SaveTo(fs);

        return outPath;
    }
}