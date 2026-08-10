namespace PriceBotPipeline;

/// <summary>OCR motoru soyutlaması (2026-08-07, PaddleOCR spike sonrası eklendi). Tesseract tabanlı
/// FullScanOcr implementasyonu 2026-08-07'de kaldırıldı (bkz. git tag checkpoint/dual-ocr-engines —
/// Tesseract+Paddle birlikteyken son durum; bir sorun çıkarsa oradan geri dönülebilir) — artık tek
/// implementasyon PaddleScanOcr'dır, ama Worker.cs ve geri kalan HER ŞEY (kod eşleştirme, marka
/// tespiti, fuzzy/yaş-aralığı mantığı, raporlama) hâlâ bu arayüz üzerinden çalışır; ileride başka
/// bir motor denenmek istenirse (veya Tesseract'a geri dönülürse) tek bunu implemente etmek yeterli.</summary>
public interface IOcrEngine : IDisposable
{
    /// <param name="descriptions">v11 yaş-aralığı/aile-stili çapraz doğrulaması için opsiyonel —
    /// bkz. PaddleScanOcr.FindProductCodes.</param>
    ScanResult FindProductCodes(string imagePath, IReadOnlySet<string> excelCodes,
        IReadOnlyDictionary<string, string>? descriptions = null);

    /// <summary>Marka-yazısı kurtarma taraması (bkz. BrandMatcher.MatchFromOcrTokens).</summary>
    Dictionary<string, float> CollectBrandTokens(string imagePath);
}

/// <summary><paramref name="Source"/> (2026-08-10, varsayılan "OCR"): normal OCR eşleşmeleri hiç
/// dokunmadan geçer; Worker.cs'in Gemini kod-tespiti fallback'i (bkz. GeminiVisionClassifier.cs)
/// sentetik bir CodeMatch oluştururken "Gemini görü tespiti" verir — rapor/log bunu ayırt
/// edebilsin diye (aynı Levenshtein-fuzzy güven skoruna sahip değil, enum-zorlamalı "kesin" bir
/// seçim, ikisi karıştırılmamalı).</summary>
public sealed record CodeMatch(string Code, float Confidence, bool IsFuzzy = false, string Source = "OCR");

/// <summary>Tokens: OCR geçişlerinde toplanan TÜM ham kelimeler → en yüksek güven skoru.
/// Ürün kodu eşleştirmesinin yan ürünüdür ama marka tespiti de (BrandMatcher) ek bir OCR
/// maliyeti ödemeden bu havuzu kullanır.</summary>
public sealed record ScanResult(List<CodeMatch> Matches, List<string> Candidates, Dictionary<string, float> Tokens);
