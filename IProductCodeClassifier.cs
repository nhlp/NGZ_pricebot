namespace PriceBotPipeline;

/// <summary>Görü tabanlı (OCR'a alternatif/tamamlayıcı) ürün kodu tespiti yapan sağlayıcılar için
/// ortak arayüz (2026-08-13) — Worker.cs'in Aşama 3 son-çare zincirinin (Gemini birincil key ->
/// Gemini ikincil key -> Groq -> Claude) TEK bir döngüyle, sağlayıcıya özel kod tekrarı olmadan
/// gezebilmesi için (bkz. IOcrEngine.cs ile aynı desen: birden fazla somut uygulama, ortak arayüz
/// üzerinden). <see cref="GeminiVisionClassifier"/>, <see cref="GroqVisionClassifier"/> ve
/// <see cref="AnthropicVisionClassifier"/> bu arayüzü uygular.
///
/// <c>RetryAfter</c> (2026-08-13, kullanıcı isteği: kota/rate-limit'te SENKRON beklemek yerine
/// "diğer görselleri işlerken geçecek doğal süreyi kullan, sonra bir kez daha dene"): bir sağlayıcı
/// kota/rate-limit yüzünden şu an cevap veremiyorsa ama YAKIN gelecekte (Google/Groq'un önerdiği
/// süre kadar sonra) verebilecekse, <c>Code=null, ApiFailed=false, RetryAfter=&lt;önerilen süre&gt;</c>
/// döner — bu, "kalıcı olarak öldü" (ApiFailed=true, devre kesici) ile "şimdilik boş/BULUNAMADI"
/// (üçü de null/false) arasında ÜÇÜNCÜ bir durumdur. Çağıran taraf (Worker.cs) bu görseli hemen
/// "atlandı" raporlamaz/bir sonraki sağlayıcıya (varsa) geçer; klasördeki TÜM görseller için ilk
/// tur bittikten SONRA, sadece bu şekilde ertelenmiş görselleri TEK bir ek turda tekrar dener —
/// senkron `Task.Delay` YERİNE, aradan geçen gerçek işlem süresini (diğer görsellerin OCR/
/// damgalama/gönderim süresi) "bekleme" olarak kullanır (bkz. Worker.cs "Aşama 3.5").
///
/// Marka tespiti (ClassifyBrandAsync) bu arayüze DAHİL DEĞİL — o klasör-genelinde farklı bir akışa
/// sahip (ilk 4 görsel, ilk başarılı sonuçta dur) ve Worker.cs'te ayrı, tek bir sağlayıcıyla
/// (GeminiVisionClassifier) çağrılıyor; kod tespitindeki gibi bir kota-tükenmesi sorunu hiç
/// gözlenmedi (bkz. CLAUDE.md), bu yüzden çoklu-sağlayıcı zincirine dahil edilmedi.</summary>
public interface IProductCodeClassifier
{
    Task<(string? Code, bool ApiFailed, TimeSpan? RetryAfter)> ClassifyCodeAsync(
        string imagePath, IReadOnlyCollection<string> candidateCodes, CancellationToken ct);
}
