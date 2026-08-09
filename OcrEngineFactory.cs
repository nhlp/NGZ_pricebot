// OcrEngineFactory.cs (2026-08-07) — appsettings.json'daki "OcrEngine" alanına göre OCR motoru
// havuzunu kurar. Varsayılan: "Paddle" (spike sonucu canlıya alındı, bkz. CLAUDE.md). Olumsuz
// geri dönüş olursa appsettings.json'da "OcrEngine": "Tesseract" yazıp servisi yeniden başlatmak
// yeterlidir — Tesseract tarafı (FullScanOcr.cs) hiç değiştirilmedi, kod değişikliği/yeniden
// derleme gerekmez.
namespace PriceBotPipeline;

using Sdcb.PaddleOCR.Models.Local;

public static class OcrEngineFactory
{
    public const string CandidatePattern = @"\d{3,7}";

    public static OcrEnginePool Create(string? engineName, string tessdataPath, int parallelism, int cacheCapacity = 4)
    {
        bool useTesseract = string.Equals(engineName, "Tesseract", StringComparison.OrdinalIgnoreCase);

        var engines = new List<IOcrEngine>(parallelism);
        if (useTesseract)
        {
            for (int i = 0; i < parallelism; i++)
                engines.Add(new FullScanOcr(tessdataPath, CandidatePattern));
        }
        else
        {
            // PaddleOcrAll'ın altındaki native motor thread-affinity gerektiriyor (bkz.
            // PaddleScanOcr.cs dosya başı notu) -- N bağımsız örnek yerine TEK bir paylaşılan
            // QueuedPaddleOcrAll (consumerCount=parallelism adanmış thread) kurulur; havuzun
            // geri kalan yuvaları bu paylaşılan kuyruğa yönlendiren ince sarmalayıcılardır.
            var owner = new PaddleScanOcr(LocalFullModels.EnglishV4, CandidatePattern, consumerCount: parallelism, cacheCapacity: cacheCapacity);
            engines.Add(owner);
            for (int i = 1; i < parallelism; i++)
                engines.Add(new PaddleScanOcr(owner));
        }

        return new OcrEnginePool(engines);
    }
}
