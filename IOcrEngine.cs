namespace PriceBotPipeline;

/// <summary>OCR motoru soyutlaması (2026-08-07, PaddleOCR spike sonrası): FullScanOcr (Tesseract)
/// ve PaddleScanOcr (PaddleOCR) bu arayüzü uygular. Worker.cs, appsettings.json'daki "OcrEngine"
/// alanına göre (bkz. OcrEngineFactory) hangi implementasyonun havuzlanacağına karar verir — kod
/// eşleştirme, marka tespiti, fuzzy/yaş-aralığı mantığı ve raporlama dahil geri kalan HER ŞEY bu
/// arayüz üzerinden çalışır ve motor değişse de değişmez. Tesseract'a dönüş appsettings.json'da
/// "OcrEngine": "Tesseract" yazıp servisi yeniden başlatmaktan ibarettir — kod değişikliği/yeniden
/// derleme gerekmez (bkz. CLAUDE.md).</summary>
public interface IOcrEngine : IDisposable
{
    /// <param name="descriptions">v11 yaş-aralığı/aile-stili çapraz doğrulaması için opsiyonel —
    /// bkz. FullScanOcr.FindProductCodes.</param>
    ScanResult FindProductCodes(string imagePath, IReadOnlySet<string> excelCodes,
        IReadOnlyDictionary<string, string>? descriptions = null);

    /// <summary>Marka-yazısı kurtarma taraması (bkz. BrandMatcher.MatchFromOcrTokens). PaddleOCR
    /// implementasyonunda normal tarama zaten marka yazısını genelde yakaladığı için bu, Tesseract'a
    /// göre çok daha nadir tetiklenen ucuz bir tekrar-taramadır.</summary>
    Dictionary<string, float> CollectBrandTokens(string imagePath);
}
