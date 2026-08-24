namespace PriceBotPipeline;

/// <summary>Görü tabanlı klasör-markası tespiti yapan sağlayıcılar için ortak arayüz
/// (2026-08-24, kullanıcı isteği: "markaları daha iyi tespit edebilmek... farklı fontlarla
/// yazılmış marka isimlerini daha iyi okuyabilmek"). <see cref="IProductCodeClassifier"/>'ın
/// dosya başı notundaki eski karar ("Marka tespiti bu arayüze DAHİL DEĞİL... kota-tükenmesi
/// sorunu hiç gözlenmedi, bu yüzden çoklu-sağlayıcı zincirine dahil edilmedi") artık GEÇERSİZ —
/// iki ayrı gerekçeyle çoklu-sağlayıcıya genişletildi:
/// (1) Kota direnci — kod tespitiyle aynı gerekçe: bir sağlayıcının kotası tükenirse (gerçek
///     vaka: NGZ/MİNİCE, bkz. GeminiVisionClassifier.cs "429 (KOTA)" notu) marka tespiti erken
///     WhatsApp sorusuna düşmemeli.
/// (2) DOĞRULUK — kod tespitinden FARKLI bir gerekçe: orada bir görselde kod ya vardır ya
///     yoktur (farklı model aynı sonucu bulur ya da bulamaz), ama "bu logo hangi markaya
///     benziyor" öznel bir görsel-tanıma sorusudur — FARKLI vision modelleri (farklı eğitim
///     verisiyle) AYNI dekoratif/stilize logoyu farklı tanıyabilir. Bu yüzden bir sağlayıcı
///     "BULUNAMADI" dese bile bir SONRAKİ sağlayıcı denenir (sadece hata durumunda değil).
///
/// Worker.cs bir <c>brandClassifiers</c> listesini SIRAYLA dener (Gemini birincil key -> Gemini
/// ikincil key -> Groq -> Claude — codeClassifiers ile AYNI sıra/gerekçe: ücretsiz+ucuz önce).
/// Bir sağlayıcı marka BULURSA hemen durulur; bulamazsa (ApiFailed true ya da false farketmez —
/// her iki durumda da "bu sağlayıcı bu klasörde işe yaramadı") bir SONRAKİ sağlayıcı denenir.
/// <c>ApiFailed</c> sadece log/tanı amaçlı ayrı tutulur ("hata mı yoksa gerçekten tanımadı mı").
///
/// Her uygulama KENDİ İÇİNDE zaten "ilk N görseli sırayla dene, ilk başarıda dur" davranışını
/// koruyor (GeminiVisionClassifier'daki kota-koruma deseni, 2026-08-10) — bu arayüz sadece
/// SAĞLAYICILAR ARASI zinciri ekliyor, görsel-başına davranışı değiştirmiyor. Kod tespitindeki
/// (<see cref="IProductCodeClassifier"/>) ayrıntılı kota-bekle/RetryAfter zinciri BİLİNÇLİ olarak
/// buraya taşınmadı — marka tespiti klasör başına TEK seferlik bir çağrı (kod tespiti gibi
/// onlarca görsel için tekrar tekrar çağrılmıyor), bu yüzden "diğer görselleri işlerken geçen
/// süreyi kullan" ertelemesinin faydası yok; bir sağlayıcı 429 alırsa sadece SIRADAKİ sağlayıcıya
/// geçilir.
///
/// OCR İPUCU ENJEKSİYONU (aynı tarih, aynı kullanıcı isteği): <c>ocrHint</c> — OCR'ın (kod
/// taraması + dosya adı + letterhead birleşimi) ürettiği ama hiçbir resmi marka adıyla
/// eşleşmeyen, yine de ayırt edici (jenerik olmayan, >=4 harf) ham kelimeler (bkz.
/// BrandMatcher.ExtractDistinctiveHintWords). Vision modeline "OCR bulanık/yaklaşık şunu okudu"
/// ek BAĞLAM olarak veriliyor — halüsinasyon riskini ARTIRMAZ (model hâlâ SADECE kapalı listeden
/// enum seçimine zorlanıyor), sadece görsel-benzerlik kararını (özellikle OCR'ın kesik/hatalı
/// okuduğu dekoratif fontlarda) kolaylaştırabilir. Boş/null ise prompt hiç değişmez (davranış
/// aynı kalır).</summary>
public interface IBrandClassifier
{
    Task<(BrandMultiplier? Brand, string? RawLabel, bool ApiFailed)> ClassifyBrandAsync(
        IReadOnlyList<string> imagePaths,
        IReadOnlyList<BrandMultiplier> candidates,
        string? ocrHint,
        CancellationToken ct);
}
