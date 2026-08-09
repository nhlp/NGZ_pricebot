// OcrEngineFactory.cs (2026-08-07) — PaddleOCR tabanlı OCR motor havuzunu kurar (spike sonucu
// canlıya alındı, bkz. CLAUDE.md "OCR performansı"). Tesseract tabanlı alternatif (FullScanOcr)
// 2026-08-07'de kaldırıldı — geri dönmek gerekirse git tag checkpoint/dual-ocr-engines, ikisinin
// birlikte olduğu son durumu işaretler.
namespace PriceBotPipeline;

using Sdcb.PaddleOCR.Models.Local;

public static class OcrEngineFactory
{
    // Alt sınır 3: bazı fiyat listeleri 3 haneli ürün kodu kullanıyor (gerçek vaka,
    // BABY Hi 2026-08-03: "473" gibi kodlar 4 haneli varsayımıyla aday listesine hiç
    // girmiyordu, Excel'de karşılığı olsa bile karşılaştırmaya ulaşamıyordu).
    public const string CandidatePattern = @"\d{3,7}";

    public static OcrEnginePool Create(int parallelism, int cacheCapacity = 4)
    {
        // PaddleOcrAll'ın altındaki native motor thread-affinity gerektiriyor (bkz.
        // PaddleScanOcr.cs dosya başı notu) -- N bağımsız örnek yerine TEK bir paylaşılan
        // QueuedPaddleOcrAll (consumerCount=parallelism adanmış thread) kurulur; havuzun
        // geri kalan yuvaları bu paylaşılan kuyruğa yönlendiren ince sarmalayıcılardır.
        var owner = new PaddleScanOcr(LocalFullModels.EnglishV4, CandidatePattern, consumerCount: parallelism, cacheCapacity: cacheCapacity);
        var engines = new List<IOcrEngine>(parallelism) { owner };
        for (int i = 1; i < parallelism; i++)
            engines.Add(new PaddleScanOcr(owner));

        return new OcrEnginePool(engines);
    }
}
