using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace PriceBotPipeline;

/// <summary>OCR hiçbir marka/ürün kodu bulamadığında son çare olarak devreye giren görü tabanlı
/// kapalı-liste sınıflandırıcı — Google Gemini API (ücretsiz katman, bkz.
/// https://ai.google.dev/gemini-api/docs/rate-limits) kullanır. İKİ görevi var (2026-08-10'da
/// TEK sınıfta birleştirildi — eskiden sadece marka yapan `GeminiBrandClassifier`'dı, `IOcrEngine`'in
/// hem `FindProductCodes` hem `CollectBrandTokens`'ı TEK arayüzde toplamasıyla aynı desen):
/// <see cref="ClassifyBrandAsync"/> (klasör markası) ve <see cref="ClassifyCodeAsync"/> (görsel
/// başına ürün kodu). İkisi de aynı ortak çekirdeği (<see cref="ClassifyLabelAsync"/>) kullanır.
///
/// NEDEN GEREKLİ (bkz. Worker.cs "Son çare" yorumları): OCR motorları (Paddle dahil) sadece
/// HARF/RAKAM okuyabilir. Marka logosu bazen tamamen dekoratif bir fontla (okunabilir ama
/// `BrandMatcher`'ın kelime-eşleştirmesinin kaçırdığı) bazen de tamamen GRAFİK/nakış olarak
/// geliyor; ürün kodu bazen çok küçük/bulanık ya da beden/yaş rakamlarıyla karışık basılı oluyor
/// — bu durumlarda ortada OCR'ın güvenle okuyabileceği bir şey yok. Görü modeli harf/rakam okumak
/// yerine insan gibi bütünsel görsel tanıma yaptığı için ikisini de çözebiliyor.
///
/// HALÜSİNASYON ENGELLEME: model'e her zaman KAPALI bir aday listesi (marka: ~300 Nebim markası;
/// kod: o klasörün Excel kodları) veriliyor ve Gemini'nin `responseSchema` + `enum` özelliğiyle
/// (bkz. https://ai.google.dev/gemini-api/docs/structured-output) SADECE bu listeden bir değer ya
/// da "BULUNAMADI" döndürmesi ZORUNLU kılınıyor — serbest metin/JSON'a bırakılsa model listede
/// olmayan bir marka/kod uydurabilirdi, enum zorlaması bunu yapısal olarak imkansız kılıyor.
///
/// OPSİYONEL/EK: <c>apiKey</c> boşsa hiçbir ağ isteği atılmaz, her iki metot da her zaman null/
/// (null,false) döner — Worker.cs bu sınıfı OcrEnginePool gibi hep (null kontrolüne gerek
/// kalmadan) parametre olarak geçirebilir. Mevcut davranış (OCR bulamazsa WhatsApp'a sor/görseli
/// atla) hiç değişmez.
///
/// KOTA KORUMA (2026-08-10, kullanıcı isteği): `ClassifyBrandAsync` görselleri TEK TEK, sıralı
/// istek olarak dener — eskiden ilk 4 görsel TEK istekte birlikte gönderiliyordu. Bir Gonderim
/// klasörü her zaman TEK markadır (iş kuralı) ve canlı testte doğrulandı (aynı ürünün 3 farklı
/// görseli BAĞIMSIZ olarak hep aynı doğru markayı verdi) — yani birden fazla görseli aynı anda
/// göndermek doğruluğu artırmıyor, sadece token/kota harcıyor. İlk başarılı sonuçta durulur.
/// `ClassifyCodeAsync` ise TEK görsel + TEK istektir zaten (kod görsele özgüdür, klasör genelinde
/// sabit DEĞİL — "aynı kodlu başka görsel" diye bir kavram yok, tekrar deneme mantığı marka
/// gibi işlemez). Kod tespiti tarafında klasör başına devre kesici Worker.cs'te (bkz. orada).
///
/// MODEL ADI appsettings.json'dan (`GeminiBrandModel`) değiştirilebilir — Gemini model
/// adlandırması hızlı değiştiği için kod içi sabit varsayıma güvenilmiyor (bkz. CLAUDE.md
/// "8 vCPU varsayımı yanlıştı" dersinden gelen desen: OcrParallelism/OcrProcessPriority).
///
/// YEDEK MODEL ZİNCİRİ (2026-08-10, kullanıcı isteği, canlı testle kalibre edildi): `gemini-
/// flash-latest` bir takma ad olduğu için riski AZALTIYOR ama garanti ETMİYOR (Google modelleri
/// istediği zaman değiştirebilir/kısıtlayabilir — gerçek vaka aynı gün: `gemini-2.5-flash` sabit
/// adı 404 döndü). Birincil model HTTP 400/404 (KALICI konfigürasyon hatası — "model yok"/"artık
/// desteklenmiyor" gibi) döndürürse, AYNI istek otomatik olarak `GeminiBrandModelFallback`'e
/// (appsettings.json, varsayılan `gemini-flash-lite-latest`) tekrar denenir — TEK seferlik
/// (sonsuz zincir yok, kota koruma ruhuyla tutarlı). İlk denemede yedek varsayılanı bilinçli
/// olarak "farklı model ailesi" mantığıyla `gemini-pro-latest` seçilmişti, ama canlı testte bu
/// hesapta ÜCRETSİZ KOTASI SIFIR çıktı (HTTP 429 "limit: 0" — Pro katmanı genelde faturalandırma
/// gerektiriyor); `gemini-flash-lite-latest` (Flash ailesinde ama farklı bir takma ad) hem canlı
/// testte doğru çalıştı hem ücretsiz kaldı. 5xx/timeout/güvenlik engeli gibi GEÇİCİ ya da
/// içerik-özel hatalarda yedek DENENMEZ — bunlar ikinci modelde de aynı şekilde başarısız olur ya
/// da modelden bağımsızdır, yedek denemek sadece kotayı ikiye katlar. **Bilinçli tek istisna**
/// (2026-08-11, kullanıcı onayıyla, gerçek vaka: DECO 57-013): <see cref="ClassifyCodeAsync"/>'in
/// deneme zincirinin SON basamağı — aynı modele bir kez tekrar denendikten SONRA hâlâ geçici hata
/// alınırsa yedek modele de bir kez geçilir. Kod tespiti TEK görsel + TEK istek olduğu için
/// (markadaki gibi "sıradaki görsel" diye bir kurtarma yolu yok) ve 503 çoğunlukla bir modelin
/// sunucu havuzuna ÖZGÜ olabildiği için (farklı model farklı havuzda çalışabilir) burada risk/ödül
/// dengesi farklı — bkz. <see cref="ClassifyCodeAsync"/> dosya-içi yorumu. <see
/// cref="ClassifyBrandAsync"/> ve genel 400/404 config-hatası yedek mantığı bu istisnadan
/// ETKİLENMEDİ, aynen "geçici hatada yedek yok" kalıyor.
///
/// 429 (KOTA) ARTIK YUKARIDAKİ "geçici/içerik-özel hatalarda yedek yok" KURALININ DIŞINDA
/// (2026-08-13, kullanıcı isteği, gerçek vaka: NGZ/MİNİCE klasörü — `generate_content_free_tier_
/// requests` metriğinde `limit: 5`, 38 görsellik bir klasörde 6. istekten sonra kota tükenip devre
/// kesici tetiklendi, kalan ~31 görsel için Gemini hiç denenmedi). Google'ın kota metriği MODELE
/// ÖZGÜ (`model: gemini-3.6-flash` gibi) olduğu için farklı bir modelin ayrı/dolmamış bir kotası
/// olması muhtemel — bu varsayım (eski "429'da yedek denemek kotayı ikiye katlar" mantığının
/// tersi) doğrulanmadı ama denemenin maliyeti (tek ekstra istek) düşük, ödülü yüksek. Artık: (1)
/// <see cref="ClassifyLabelAsync"/>'in birincil-model-hata-sonrası otomatik yedek geçişi 400/404
/// yanında 429'u da tetikliyor; (2) <see cref="ClassifyCodeAsync"/>'in bekle-ve-tekrar-dene adımı
/// (eskiden SADECE <c>IsTransientFailure</c>/5xx için) artık 429'u da kapsıyor — bekleme süresi
/// sabit değil, Google'ın 429 yanıtında önerdiği süre (<c>ParseRetryDelay</c>, bkz.
/// GeminiVisionClassifierLabelResolver.cs) kullanılıyor, yoksa <c>CodeRetryDelay</c>'e düşülüyor.
/// Devre kesici (Worker.cs <c>geminiCodeApiHealthy</c>) hâlâ var — ama artık İLK 429'da değil,
/// bekleme + birincil + yedek modelin HEPSİ tükendikten SONRA tetikleniyor. <see
/// cref="ClassifyBrandAsync"/>'in kendi döngü-içi "kalıcı hatada dur" davranışı bilinçli olarak
/// DEĞİŞTİRİLMEDİ (bu istek sadece kod tespiti/devre kesici içindi) — ama paylaşılan <see
/// cref="ClassifyLabelAsync"/> üzerinden dolaylı olarak o da artık 429'da bir kez yedek model
/// deniyor (aynı görsel için, sıradaki görsele geçmeden önce).
///
/// RESMİ/TOPLULUK BİR .NET SDK'SI KULLANILMIYOR — ham HTTP POST + System.Text.Json,
/// Worker.cs'in bot'un kendi API'sine karşı zaten kullandığı desenle aynı (bkz.
/// TrySendTextAsync/TrySendAsync). Yeni NuGet paketi eklenmedi (dependency-hafifliği
/// önceliğiyle tutarlı — bkz. CLAUDE.md Tesseract kaldırma notu).
///
/// <see cref="IProductCodeClassifier"/> UYGULAR (2026-08-13): Worker.cs'in çoklu-sağlayıcı
/// zincirinde (bkz. o arayüzün dosya başı yorumu) Gemini birincil/ikincil key olarak İKİ AYRI
/// örnek (aynı sınıf, farklı apiKey) bu arayüz üzerinden aynı döngüyle çağrılıyor.
///
/// <see cref="IBrandClassifier"/> DA UYGULAR (2026-08-24): marka tespiti de artık AYNI iki-key
/// zincire (+ Groq + Claude) dahil — bkz. o arayüzün dosya başı yorumu.</summary>
public sealed partial class GeminiVisionClassifier : IProductCodeClassifier, IBrandClassifier
{
    private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/models";
    private const int MaxImages = 4;
    private const int MaxLongEdgePixels = 1024;
    private const int JpegQuality = 85;

    private const string BrandSystemPrompt =
        "Sen bir e-ticaret/toptan çocuk giyim ürün fotoğraflarındaki marka logolarını tanıyan bir " +
        "görsel sınıflandırma asistanısın. Cevabın SADECE verilen kapalı listeden bir marka adı ya " +
        "da 'BULUNAMADI' olmalı.";
    private const string BrandUserPrompt =
        "Bu görsel bir çocuk giyim ürününün etiketi/fotoğrafıdır. Görselde görünen marka adını, " +
        "verilen listeden seçerek bildir. Marka yazısı bazen dekoratif/stilize bir fontla, bazen de " +
        "harfler değil tamamen bir grafik/logo/nakış olarak gelebilir — sadece harfleri okumaya " +
        "çalışma, görsel örüntüyü de değerlendir. Listede olmayan bir marka görüyorsan veya emin " +
        "değilsen kesinlikle 'BULUNAMADI' de; listede olmayan bir isim UYDURMA.";

    private const string CodeSystemPrompt =
        "Sen bir toptan çocuk giyim ürün fotoğraflarındaki/etiketlerindeki ürün kodlarını (SKU) " +
        "okuyan bir görsel sınıflandırma asistanısın. Cevabın SADECE verilen kapalı listeden bir " +
        "ürün kodu ya da 'BULUNAMADI' olmalı.";
    private const string CodeUserPrompt =
        "Bu bir toptan ürün fotoğrafı/etiketidir; üzerinde küçük yazılı bir ürün kodu (SKU) " +
        "olabilir — etikette, köşede, kenarda ya da ürünün üzerinde basılı/yazılı sayısal ya da " +
        "alfanümerik bir kod arayın (beden/yaş numaralarıyla KARIŞTIRMAYIN — kod genelde daha uzun " +
        "veya 'code:'/'kod:' gibi bir önekle birlikte gelir). Görünen kodu, verilen listeden " +
        "seçerek bildir. Kod bulanık, küçük ya da kısmen kapalı olabilir — dikkatlice bakın. " +
        "Listede olmayan bir kod görüyorsan veya emin değilsen kesinlikle 'BULUNAMADI' de; " +
        "listede olmayan bir kod UYDURMA.";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _fallbackModel;
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    /// <summary>Worker'ın bot-gönderim için kullandığı paylaşılan HttpClient İLE AYNI
    /// örnek DEĞİL (NebimRateProvider/NebimBrandProvider'ın kendi bağlantısını sahiplenmesiyle
    /// aynı desen) — kısa bir timeout burada ayarlanabiliyor ki tıkanan bir Gemini isteği
    /// 10 sn'lik ana tarama döngüsünü bloklamasın.</summary>
    public GeminiVisionClassifier(string apiKey, string model, string fallbackModel, ILogger logger)
    {
        _apiKey = apiKey?.Trim() ?? "";
        // "gemini-flash-latest" bilinçli olarak bir SABİT sürüm değil, Google'ın her zaman güncel
        // flash modeline yönlendirdiği bir TAKMA AD (2026-08-10 canlı testinde doğrulandı) — sabit
        // bir "gemini-2.5-flash" gibi model adı, hesaba göre "artık yeni kullanıcılara kapalı" (404)
        // hâle gelebiliyor (gerçek vaka: aynı gün, bkz. CLAUDE.md). Takma ad kullanmak bu riski
        // yapısal olarak azaltıyor; yine de appsettings.json'dan override edilebilir kalıyor.
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-flash-latest" : model.Trim();
        // Yedek model — bkz. dosya başı "YEDEK MODEL ZİNCİRİ" notu. "gemini-pro-latest" İLK
        // denemede varsayılan olarak seçilmişti ("farklı model ailesi" mantığıyla) ama canlı
        // testte (2026-08-10) bu hesapta ÜCRETSİZ KOTASI SIFIR çıktı (HTTP 429 "limit: 0") —
        // Pro katmanı genelde faturalandırma gerektiriyor, Flash gibi ücretsiz kotası yok.
        // "gemini-flash-lite-latest" (hâlâ Flash ailesinde ama `gemini-flash-latest`ten FARKLI
        // bir takma ad) canlı testte doğru çalıştı VE ücretsiz kaldı — pratik varsayılan bu.
        _fallbackModel = string.IsNullOrWhiteSpace(fallbackModel) ? "gemini-flash-lite-latest" : fallbackModel.Trim();
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

        if (_apiKey.Length == 0)
            _logger.LogInformation("Gemini görü tespiti (marka + ürün kodu) KAPALI (GeminiApiKey ayarlanmamış).");
        else
            _logger.LogInformation("Gemini görü tespiti (marka + ürün kodu) AÇIK (model: {Model}, yedek: {Fallback}).", _model, _fallbackModel);
    }

    /// <summary>OCR'ın hiç bulamadığı bir klasör için son çare marka tespiti. Görseller TEK TEK,
    /// sıralı denenir (bkz. dosya başı "KOTA KORUMA" notu) — ilk başarılı sonuçta durulur.
    /// KALICI hata alınırsa (kota, config, vb. — bkz. <see cref="LabelResult.IsTransientFailure"/>)
    /// HEMEN durulur, kalan görseller denenmez (aynı duvara çarpar, boşuna harcanır), <c>ApiFailed
    /// =true</c> döner (Worker.cs'in çoklu-sağlayıcı zincirinde SIRADAKİ sağlayıcıya geçme sinyali
    /// — bkz. IBrandClassifier.cs); GEÇİCİ hata (503/timeout/ağ — 2026-08-10, gerçek vaka: bir
    /// "model şu anda yoğun" 503'ü tüm klasörün kalan görsellerini gereksiz yere atlatmıştı)
    /// alınırsa sıradaki görsel yine denenir; "BULUNAMADI" alınırsa da sıradaki görsel denenir.
    /// Sonuçta hiçbiri bulamazsa <c>(null, null, false)</c> döner — çağıran taraf bunu sıradaki
    /// sağlayıcıya (varsa) ya da WhatsApp sorusuna düşmesi gerektiği şeklinde okur.
    ///
    /// <paramref name="ocrHint"/> (2026-08-24, "OCR İPUCU ENJEKSİYONU" — bkz. IBrandClassifier.cs
    /// dosya başı yorumu): null/boşsa prompt hiç değişmez.</summary>
    public async Task<(BrandMultiplier? Brand, string? RawLabel, bool ApiFailed)> ClassifyBrandAsync(
        IReadOnlyList<string> imagePaths,
        IReadOnlyList<BrandMultiplier> candidates,
        string? ocrHint,
        CancellationToken ct)
    {
        if (_apiKey.Length == 0 || imagePaths.Count == 0 || candidates.Count == 0)
            return (null, null, false);

        var candidateNames = BuildDistinctCandidateNames(candidates);
        if (candidateNames.Count == 0) return (null, null, false);

        var userPrompt = BuildBrandUserPrompt(BrandUserPrompt, ocrHint);
        foreach (var path in imagePaths.Take(MaxImages))
        {
            _logger.LogInformation("Gemini görü tespiti: marka için soruluyor -> {File}", Path.GetFileName(path));
            var result = await ClassifyLabelAsync([path], candidateNames, userPrompt, BrandSystemPrompt, ct);
            if (result.Label is null)
            {
                if (result.IsTransientFailure)
                {
                    _logger.LogInformation("Gemini görü tespiti: '{File}' için geçici bir hata alındı, sıradaki görsel deneniyor.", Path.GetFileName(path));
                    continue; // geçici hata (503/timeout/ağ) — sıradaki görseli dene
                }
                return (null, null, true); // kalıcı hata (kota/config) — kalan görselleri deneme
            }
            if (result.Label == NotFoundLabel)
            {
                _logger.LogInformation("Gemini görü tespiti: '{File}' için marka BULUNAMADI yanıtı geldi, sıradaki görsel deneniyor.", Path.GetFileName(path));
                continue; // bu görselde marka yok, sıradakini dene
            }

            var brand = ResolveLabelToBrand(result.Label, candidates);
            if (brand is not null) return (brand, result.Label, false);

            // Teorik olarak olmamalı (enum zorlaması candidateNames dışında bir değere izin
            // vermiyor) — yine de savunmacı: beklenmeyen etiketle sıradaki görseli dene.
            _logger.LogWarning("Gemini görü marka tespiti: dönen etiket ('{Label}') aday listesiyle eşleşmedi.", result.Label);
        }

        return (null, null, false);
    }

    /// <summary>Geçici bir Gemini hatasından sonra AYNI görsel için tek seferlik tekrar deneme
    /// öncesi beklenecek süre (2026-08-11). Sabit/kısa tutuldu — amaç sunucu tarafındaki anlık
    /// "şu an yoğun" durumunun geçmesine küçük bir şans tanımak, uzun bir backoff stratejisi değil
    /// (kota koruma ruhuyla tutarlı: tek deneme, sonsuz döngü yok).</summary>
    private static readonly TimeSpan CodeRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>OCR'ın (kod taraması) tek bir görselde Excel kodlarından hiçbiriyle eşleşen bir
    /// aday bulamadığı durumda son çare ürün kodu tespiti (2026-08-10). Marka tespitinden farklı
    /// olarak TEK görsel + TEK istektir — kod görsele özgüdür, marka gibi klasör genelinde sabit
    /// değil, "tekrar deneyecek aynı kodlu başka görsel" kavramı yok. TAM bu yüzden geçici bir
    /// hatada (503/timeout/ağ) "sıradaki görsele geç" stratejisi (ClassifyBrandAsync'in yaptığı)
    /// burada işe yaramaz — kod klasör genelinde sabit olmadığı için sıradaki görsel bu görselin
    /// kodunu bulamaz. Bu yüzden 2026-08-11'de eklenen ÜÇ aşamalı deneme zinciri (gerçek vaka:
    /// DECO 57-013 kodlu bir katalog görseli, klasördeki EŞLEŞMEYEN SON görseldi — devre kesicinin
    /// "sonraki görseller için Gemini yine denenecek" güvencesinin hiçbir faydası olmadı, çünkü
    /// tekrar denenecek başka görsel yoktu; üstelik OCR'ın okuduğu adaylar da kod ile hiç
    /// örtüşmüyordu, yani Gemini tek gerçek şanstı):
    /// 1) birincil model (<c>_model</c>);
    /// 2) O geçici hatayla başarısız olursa, kısa bir bekleme (<see cref="CodeRetryDelay"/>)
    ///    sonrası AYNI modele AYNI görsel için tekrar deneme (anlık "şu an yoğun" dalgalanmasına
    ///    karşı);
    /// 3) O DA geçici hatayla başarısız olursa, son çare olarak yedek modele (<c>_fallbackModel</c>)
    ///    TEK seferlik geçiş (503 sıkça bir modelin sunucu havuzuna ÖZGÜ olabiliyor — farklı model
    ///    farklı havuzda çalıştığı için birincil hâlâ tıkalıyken bile yanıt verebilir; kullanıcı
    ///    onayıyla, dosya başı "YEDEK MODEL ZİNCİRİ" notundaki "geçici hatada yedek denenmez"
    ///    kuralının SADECE bu üçüncü basamak için bilinçli istisnası).
    /// <c>ApiFailed=true</c> döndüğünde çağıran taraf (Worker.cs) o klasördeki KALAN görseller
    /// için Gemini kod tespitini atlamalı (devre kesici). SADECE gerçekten kalıcı hatalarda
    /// (400/404 config, içerik engeli) true döner. <c>RetryAfter</c> (2026-08-13, kullanıcı
    /// isteği — bkz. IProductCodeClassifier.cs dokümantasyonu) 429 KOTA aşımında dönen ÜÇÜNCÜ bir
    /// durum: SENKRON beklemek yerine Worker.cs'e "şimdilik değil, bu görseli erteleyip diğerlerini
    /// işledikten sonra bir kez daha dene" sinyali gönderir — devre kesici TETİKLENMEZ (kota,
    /// diğer görseller işlenirken geçecek gerçek süre içinde kendiliğinden açılabilir).
    ///
    /// Deneme sırası: (1) birincil model — <see cref="ClassifyLabelAsync"/> zaten 429/400/404'te
    /// OTOMATİK olarak yedek modele TEK seferlik, SIFIR-bekleme ile geçiyor (bkz. o metodun
    /// yorumu) — bu adım burada TEKRARLANMIYOR. (2) O ikisi de başarısız olup hata GERÇEKTEN
    /// geçici ise (5xx/ağ — kota DEĞİL, 2026-08-13'te kota bu adımdan bilinçli olarak ÇIKARILDI,
    /// çünkü kota onlarca saniye sürebilir ve bloklamaya değmez) KISA (<see cref="CodeRetryDelay"/>)
    /// bir senkron bekleme + AYNI modele tekrar deneme (503 genelde saniyeler içinde geçen anlık
    /// bir yoğunluk, bloklamaya değer kadar kısa). (3) O DA geçici hatayla başarısız olursa, son
    /// çare yedek modele TEK seferlik geçiş (2026-08-11, DECO 57-013 vakası — bkz. dosya başı
    /// "YEDEK MODEL ZİNCİRİ" notu, bu istisna SADECE gerçek geçici hatalar için, kota için
    /// DEĞİL).</summary>
    public async Task<(string? Code, bool ApiFailed, TimeSpan? RetryAfter)> ClassifyCodeAsync(
        string imagePath,
        IReadOnlyCollection<string> candidateCodes,
        CancellationToken ct)
    {
        if (_apiKey.Length == 0 || candidateCodes.Count == 0)
            return (null, false, null);

        var codes = candidateCodes.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        _logger.LogInformation("Gemini görü tespiti: ürün kodu için soruluyor -> {File}", Path.GetFileName(imagePath));
        var result = await ClassifyLabelAsync([imagePath], codes, CodeUserPrompt, CodeSystemPrompt, ct);

        if (result.Label is null && result.IsTransientFailure && !result.IsQuotaExceeded)
        {
            _logger.LogInformation("Gemini görü tespiti: '{File}' için geçici bir hata alındı, {Delay:N1} sn sonra AYNI modele ({Model}) tekrar denenecek.",
                Path.GetFileName(imagePath), CodeRetryDelay.TotalSeconds, _model);
            await Task.Delay(CodeRetryDelay, ct);
            result = await ClassifyLabelAsync([imagePath], codes, CodeUserPrompt, CodeSystemPrompt, ct, modelOverride: _model);
        }

        if (result.Label is null && result.IsTransientFailure && !result.IsQuotaExceeded && _fallbackModel != _model)
        {
            _logger.LogInformation("Gemini görü tespiti: '{File}' için ikinci deneme de geçici hatayla başarısız oldu, son çare olarak yedek modele ({Fallback}) geçiliyor.",
                Path.GetFileName(imagePath), _fallbackModel);
            result = await ClassifyLabelAsync([imagePath], codes, CodeUserPrompt, CodeSystemPrompt, ct, modelOverride: _fallbackModel);
        }

        if (result.Label is null)
        {
            if (result.IsQuotaExceeded)
            {
                // Birincil model VE onun otomatik yedek-model denemesi (ClassifyLabelAsync
                // içinde, sıfır-bekleme) İKİSİ de kota aşımı verdi — burada BEKLEMİYORUZ, Worker.cs
                // bu görseli erteleyip diğerlerini işledikten sonra bir kez daha deneyecek.
                var delay = result.RetryDelay ?? CodeRetryDelay;
                _logger.LogInformation("Gemini görü tespiti: '{File}' için kota aşımı (429) — bu görsel ertelenip diğer görseller işlendikten sonra (~{Delay:N0} sn) tekrar denenecek.",
                    Path.GetFileName(imagePath), delay.TotalSeconds);
                return (null, ApiFailed: false, RetryAfter: delay);
            }
            if (result.IsTransientFailure)
            {
                _logger.LogInformation("Gemini görü tespiti: '{File}' için tüm denemelerden sonra da geçici bir hata alındı, bu görsel atlanacak ama klasördeki sonraki görseller için Gemini yine denenecek.", Path.GetFileName(imagePath));
                return (null, ApiFailed: false, null);
            }
            return (null, ApiFailed: true, null); // kalıcı hata (config/içerik engeli) — devre kesiciyi tetikle
        }
        if (result.Label == NotFoundLabel)
        {
            _logger.LogInformation("Gemini görü tespiti: '{File}' için ürün kodu BULUNAMADI yanıtı geldi.", Path.GetFileName(imagePath));
            return (null, false, null);
        }
        return (result.Label, false, null);
    }

    /// <summary>Bir <see cref="ClassifyLabelAsync"/> çağrısının sonucu. <paramref name="Label"/>
    /// null ise başarısızlık nedenini <see cref="IsTransientFailure"/>/<see cref="IsQuotaExceeded"/>
    /// ayırt eder: <c>IsTransientFailure</c>=true → geçici (503/5xx/timeout/ağ hatası — aynı istek
    /// büyük ihtimalle bir sonraki denemede başarılı olur, çağıranlar KALAN görseller için
    /// denemeye devam etmeli); <c>IsQuotaExceeded</c>=true → 429 kota aşımı (2026-08-13'ten beri
    /// AYRI bir kategori — bkz. GeminiVisionClassifierLabelResolver.cs <c>IsQuotaError</c>
    /// dokümantasyonu — ne 5xx gibi rastgele geçici ne 400/404 gibi tamamen kalıcı, kendi
    /// bekle-ve-tekrar-dene mantığı var, <see cref="RetryDelay"/> Google'ın önerdiği süreyi taşır);
    /// ikisi de false → gerçekten kalıcı (400/404 config, içerik engeli, bozuk görsel dosyası —
    /// tekrar denemek aynı sonucu verir, çağıranlar durmalı). 2026-08-10: eskiden bu ayrım yoktu,
    /// TÜM hatalar kalıcı sayılıp devre kesici/erken dönüş tetikleniyordu — gerçek vaka: bir geçici
    /// 503, aynı klasördeki 2 sağlam görselin de Gemini'ye hiç sorulmadan atlanmasına yol açmıştı.</summary>
    private readonly record struct LabelResult(
        string? Label, bool IsTransientFailure = false, bool IsQuotaExceeded = false, TimeSpan? RetryDelay = null);

    /// <summary>Ortak çekirdek (2026-08-10) — hem <see cref="ClassifyBrandAsync"/> hem
    /// <see cref="ClassifyCodeAsync"/> bunu kullanır, kod tekrarı yok. Görsel(ler)i hazırlar,
    /// kapalı-liste (enum) zorlamalı isteği kurar, gönderir, `{"value": "..."}` etiketini
    /// ayrıştırır. Dönüş: gerçek bir aday (enum zorlaması sayesinde <paramref name="candidateLabels"/>
    /// içinde birebir), "BULUNAMADI", ya da <c>Label=null</c> (görsel hazırlanamadı / API-ağ hatası /
    /// engellendi / parse hatası — bkz. <see cref="LabelResult"/>).
    /// <paramref name="modelOverride"/> (2026-08-11, sadece <see cref="ClassifyCodeAsync"/>'in
    /// üçüncü/son denemesi kullanır): verilirse birincil model (<c>_model</c>) ve onun 400/404
    /// konfigürasyon-hatası yedek-model mantığı TAMAMEN atlanır, istek DOĞRUDAN bu modele gider —
    /// çağıran taraf zaten hangi modeli istediğine kendi karar vermiştir.</summary>
    private async Task<LabelResult> ClassifyLabelAsync(
        List<string> imagePaths, List<string> candidateLabels,
        string userPrompt, string systemPrompt, CancellationToken ct,
        string? modelOverride = null)
    {
        List<GeminiPart> imageParts;
        try
        {
            imageParts = imagePaths.Select(BuildInlineImagePart).ToList();
        }
        catch (Exception ex)
        {
            // Görsel decode/resize hatası (bozuk dosya vb.) — sınıflandırma dışı bırak,
            // çağıran taraf mevcut (WhatsApp sorusu / görsel atlama) akışına düşsün. OCR tarafı
            // zaten aynı görseli işlemiş olduğu için bu son derece nadir olmalı. Kalıcı bir hata —
            // aynı dosyayı tekrar denemek aynı sonucu verir.
            _logger.LogWarning(ex, "Gemini görü tespiti: görsel hazırlanamadı, atlanıyor.");
            return new LabelResult(null, IsTransientFailure: false);
        }

        var requestBody = BuildRequest(imageParts, candidateLabels, userPrompt, systemPrompt);
        var json = JsonSerializer.Serialize(requestBody, GeminiJsonContext.Default.GeminiRequest);

        var result = await SendClassifyRequestAsync(modelOverride ?? _model, json, ct);
        if (modelOverride is null && !result.Success && (result.IsModelConfigError || result.IsQuotaExceeded) && _fallbackModel != _model)
        {
            // Birincil model KALICI bir konfigürasyon hatası (400/404 — "model yok"/"artık
            // desteklenmiyor") YA DA kota aşımı (429) verdi — bkz. dosya başı "YEDEK MODEL
            // ZİNCİRİ" notu. 429 2026-08-13'ten beri de buraya dahil: Google'ın kota metriği
            // modele özgü (`model: gemini-3.6-flash` gibi), bu yüzden farklı bir modelin ayrı/
            // dolmamış bir kotası olması muhtemel — denemenin maliyeti (tek ekstra istek) düşük.
            // AYNI istek (aynı görseller, aynı aday listesi) TEK seferlik yedek modele tekrar
            // denenir.
            _logger.LogWarning("Gemini görü tespiti: birincil model ('{Model}') {Reason} verdi (HTTP {Status}) — yedek modele ('{Fallback}') geçiliyor.",
                _model, result.IsQuotaExceeded ? "kota aşımı (429)" : "kalıcı bir yapılandırma hatası", result.StatusCode, _fallbackModel);
            result = await SendClassifyRequestAsync(_fallbackModel, json, ct);
        }
        if (!result.Success) return new LabelResult(null, result.IsTransient, result.IsQuotaExceeded, result.RetryDelay);

        var label = ExtractLabel(result.ResponseText!, out var blockReason);
        if (blockReason is not null)
        {
            // İçerik engeli kalıcıdır (aynı görsel + prompt tekrar denense de aynı sonucu verir).
            _logger.LogWarning("Gemini görü tespiti: yanıt engellendi/tamamlanmadı ({Reason}).", blockReason);
            return new LabelResult(null, IsTransientFailure: false);
        }
        return new LabelResult(label);
    }

    private readonly record struct GeminiHttpResult(
        bool Success, bool IsModelConfigError, bool IsTransient, bool IsQuotaExceeded, TimeSpan? RetryDelay,
        int StatusCode, string? ResponseText);

    /// <summary>Tek bir generateContent isteğini belirtilen modele gönderir — <see
    /// cref="ClassifyLabelAsync"/>'in hem birincil hem (gerekirse) yedek model denemesi için
    /// kullandığı ortak adım. <see cref="GeminiHttpResult.IsModelConfigError"/>, çağıranın yedek
    /// modele geçip geçmeyeceğine karar vermesi için: SADECE HTTP 400/404 (kalıcı, model'e özgü
    /// hata) true. <see cref="GeminiHttpResult.IsQuotaExceeded"/> (2026-08-13) 429'u AYRI ele alır
    /// — çağıran taraf hem yedek modeli hem bekle-ve-tekrar-dene'yi dener (bkz. dosya başı ve
    /// GeminiVisionClassifierLabelResolver.cs <c>IsQuotaError</c> notu); <see
    /// cref="GeminiHttpResult.RetryDelay"/> 429 yanıtından ayrıştırılan (varsa) önerilen bekleme.
    /// <see cref="GeminiHttpResult.IsTransient"/> ise çağıranların (Worker.cs'teki devre kesici
    /// dahil) KALAN görseller/adaylar için denemeye devam edip etmeyeceğine karar vermesi için
    /// (2026-08-10): 5xx (sunucu tarafı, "usually temporary") ve ağ/timeout istisnaları true — 429
    /// (kendi ayrı IsQuotaExceeded yolu var) ve diğer 4xx (401/403 kimlik/izin gibi kalıcı
    /// sorunlar) false.</summary>
    private async Task<GeminiHttpResult> SendClassifyRequestAsync(string model, string json, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{ApiBase}/{Uri.EscapeDataString(model)}:generateContent";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.Add("x-goog-api-key", _apiKey);

            using var resp = await _http.SendAsync(req, ct);
            var responseText = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // Diğer tüm HTTP hataları çağıran tarafın mevcut fallback akışına (WhatsApp
                // sorusu / görsel atlama) sessizce düşer, kullanıcı hiçbir şey fark etmez.
                var isConfigError = IsModelConfigError(resp.StatusCode);
                var isTransient = IsTransientError(resp.StatusCode);
                var isQuotaExceeded = IsQuotaError(resp.StatusCode);
                var retryDelay = isQuotaExceeded ? ParseRetryDelay(responseText) : null;
                _logger.LogWarning("Gemini görü tespiti ({Model}): HTTP {Status} — {Body}", model, (int)resp.StatusCode, Truncate(responseText, 500));
                return new GeminiHttpResult(false, isConfigError, isTransient, isQuotaExceeded, retryDelay, (int)resp.StatusCode, responseText);
            }

            return new GeminiHttpResult(true, false, false, false, null, (int)resp.StatusCode, responseText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ağ hatası/timeout/DNS vb. — her zaman geçici sayılır (bkz. IsTransient dokümantasyonu).
            _logger.LogWarning(ex, "Gemini görü tespiti ({Model}): istek hatası.", model);
            return new GeminiHttpResult(false, false, IsTransient: true, false, null, 0, null);
        }
    }

    /// <summary>Görseli PriceStamper'ın kullandığı resize matematiğiyle (uzun kenar sınırı,
    /// oranı koruyan ölçekleme) küçültüp JPEG olarak BELLEKTE encode eder — disk yazmadan,
    /// sadece istek boyutunu/token maliyetini kaynak çözünürlükten bağımsız sınırlı tutmak
    /// için. PriceStamper'dan farklı olarak fiyat damgalamaz, sadece boyut küçültür.</summary>
    private static GeminiPart BuildInlineImagePart(string path)
    {
        using var src = SKBitmap.Decode(path) ?? throw new InvalidDataException($"Görsel açılamadı: {path}");
        var scale = Math.Min(1.0, MaxLongEdgePixels / (double)Math.Max(src.Width, src.Height));
        var info = new SKImageInfo(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)));

        using var resized = new SKBitmap(info);
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(src, new SKRect(0, 0, src.Width, src.Height), new SKRect(0, 0, info.Width, info.Height));
        }

        using var img = SKImage.FromBitmap(resized);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        var base64 = Convert.ToBase64String(data.ToArray());

        return new GeminiPart(Text: null, InlineData: new GeminiInlineData(MimeType: "image/jpeg", Data: base64));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
