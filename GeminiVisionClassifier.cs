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
/// testte doğru çalıştı hem ücretsiz kaldı. 429 (kota)/5xx/timeout/güvenlik engeli gibi GEÇİCİ ya
/// da içerik-özel hatalarda yedek DENENMEZ — bunlar ikinci modelde de aynı şekilde başarısız
/// olur ya da modelden bağımsızdır, yedek denemek sadece kotayı ikiye katlar.
///
/// RESMİ/TOPLULUK BİR .NET SDK'SI KULLANILMIYOR — ham HTTP POST + System.Text.Json,
/// Worker.cs'in bot'un kendi API'sine karşı zaten kullandığı desenle aynı (bkz.
/// TrySendTextAsync/TrySendAsync). Yeni NuGet paketi eklenmedi (dependency-hafifliği
/// önceliğiyle tutarlı — bkz. CLAUDE.md Tesseract kaldırma notu).</summary>
public sealed partial class GeminiVisionClassifier
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
    /// API/ağ hatası alınırsa (kota, timeout, vb.) HEMEN durulur, kalan görseller denenmez (aynı
    /// duvara çarpar, boşuna harcanır); "BULUNAMADI" alınırsa sıradaki görsel denenir. Sonuçta
    /// hiçbiri bulamazsa null döner — çağıran taraf bunu WhatsApp sorusuna düşmesi gerektiği
    /// şeklinde okur.</summary>
    public async Task<(BrandMultiplier Brand, string RawLabel)?> ClassifyBrandAsync(
        IReadOnlyList<string> imagePaths,
        IReadOnlyList<BrandMultiplier> candidates,
        CancellationToken ct)
    {
        if (_apiKey.Length == 0 || imagePaths.Count == 0 || candidates.Count == 0)
            return null;

        var candidateNames = BuildDistinctCandidateNames(candidates);
        if (candidateNames.Count == 0) return null;

        foreach (var path in imagePaths.Take(MaxImages))
        {
            var label = await ClassifyLabelAsync([path], candidateNames, BrandUserPrompt, BrandSystemPrompt, ct);
            if (label is null) return null; // API/ağ hatası — kalan görselleri deneme
            if (label == NotFoundLabel) continue; // bu görselde marka yok, sıradakini dene

            var brand = ResolveLabelToBrand(label, candidates);
            if (brand is not null) return (brand, label);

            // Teorik olarak olmamalı (enum zorlaması candidateNames dışında bir değere izin
            // vermiyor) — yine de savunmacı: beklenmeyen etiketle sıradaki görseli dene.
            _logger.LogWarning("Gemini görü marka tespiti: dönen etiket ('{Label}') aday listesiyle eşleşmedi.", label);
        }

        return null;
    }

    /// <summary>OCR'ın (kod taraması) tek bir görselde Excel kodlarından hiçbiriyle eşleşen bir
    /// aday bulamadığı durumda son çare ürün kodu tespiti (2026-08-10). Marka tespitinden farklı
    /// olarak TEK görsel + TEK istektir — kod görsele özgüdür, marka gibi klasör genelinde sabit
    /// değil, "tekrar deneyecek aynı kodlu başka görsel" kavramı yok.
    /// <c>ApiFailed=true</c> döndüğünde çağıran taraf (Worker.cs) o klasördeki KALAN görseller
    /// için Gemini kod tespitini atlamalı (devre kesici — aynı kota/ağ duvarına klasördeki her
    /// görsel için ayrı ayrı çarpmamak için).</summary>
    public async Task<(string? Code, bool ApiFailed)> ClassifyCodeAsync(
        string imagePath,
        IReadOnlyCollection<string> candidateCodes,
        CancellationToken ct)
    {
        if (_apiKey.Length == 0 || candidateCodes.Count == 0)
            return (null, false);

        var codes = candidateCodes.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var label = await ClassifyLabelAsync([imagePath], codes, CodeUserPrompt, CodeSystemPrompt, ct);

        if (label is null) return (null, true); // API/ağ hatası — devre kesiciyi tetikle
        return label == NotFoundLabel ? (null, false) : (label, false);
    }

    /// <summary>Ortak çekirdek (2026-08-10) — hem <see cref="ClassifyBrandAsync"/> hem
    /// <see cref="ClassifyCodeAsync"/> bunu kullanır, kod tekrarı yok. Görsel(ler)i hazırlar,
    /// kapalı-liste (enum) zorlamalı isteği kurar, gönderir, `{"value": "..."}` etiketini
    /// ayrıştırır. Dönüş: gerçek bir aday (enum zorlaması sayesinde <paramref name="candidateLabels"/>
    /// içinde birebir), "BULUNAMADI", ya da <c>null</c> (görsel hazırlanamadı / API-ağ hatası /
    /// engellendi / parse hatası — çağıran taraf bunu "daha fazla deneme, güvenli tarafta kal"
    /// olarak okumalı).</summary>
    private async Task<string?> ClassifyLabelAsync(
        List<string> imagePaths, List<string> candidateLabels,
        string userPrompt, string systemPrompt, CancellationToken ct)
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
            // zaten aynı görseli işlemiş olduğu için bu son derece nadir olmalı.
            _logger.LogWarning(ex, "Gemini görü tespiti: görsel hazırlanamadı, atlanıyor.");
            return null;
        }

        var requestBody = BuildRequest(imageParts, candidateLabels, userPrompt, systemPrompt);
        var json = JsonSerializer.Serialize(requestBody, GeminiJsonContext.Default.GeminiRequest);

        var result = await SendClassifyRequestAsync(_model, json, ct);
        if (!result.Success && result.IsModelConfigError && _fallbackModel != _model)
        {
            // Birincil model KALICI bir konfigürasyon hatası verdi (400/404 — "model yok"/
            // "artık desteklenmiyor" gibi, geçici bir kota/ağ sorunu DEĞİL) — bkz. dosya başı
            // "YEDEK MODEL ZİNCİRİ" notu. AYNI istek (aynı görseller, aynı aday listesi) TEK
            // seferlik yedek modele tekrar denenir.
            _logger.LogWarning("Gemini görü tespiti: birincil model ('{Model}') kalıcı bir yapılandırma hatası verdi (HTTP {Status}) — yedek modele ('{Fallback}') geçiliyor.",
                _model, result.StatusCode, _fallbackModel);
            result = await SendClassifyRequestAsync(_fallbackModel, json, ct);
        }
        if (!result.Success) return null;

        var label = ExtractLabel(result.ResponseText!, out var blockReason);
        if (blockReason is not null)
        {
            _logger.LogWarning("Gemini görü tespiti: yanıt engellendi/tamamlanmadı ({Reason}).", blockReason);
            return null;
        }
        return label;
    }

    private readonly record struct GeminiHttpResult(bool Success, bool IsModelConfigError, int StatusCode, string? ResponseText);

    /// <summary>Tek bir generateContent isteğini belirtilen modele gönderir — <see
    /// cref="ClassifyLabelAsync"/>'in hem birincil hem (gerekirse) yedek model denemesi için
    /// kullandığı ortak adım. <see cref="GeminiHttpResult.IsModelConfigError"/>, çağıranın yedek
    /// modele geçip geçmeyeceğine karar vermesi için: SADECE HTTP 400/404 (kalıcı, model'e özgü
    /// hata) true — 429/5xx/timeout gibi geçici ya da modelden bağımsız hatalarda false (yedek
    /// denemek boşuna kotayı ikiye katlar, bkz. dosya başı notu).</summary>
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
                // Diğer tüm HTTP hataları (429 kota dahil) çağıran tarafın mevcut fallback akışına
                // (WhatsApp sorusu / görsel atlama) sessizce düşer, kullanıcı hiçbir şey fark etmez.
                var isConfigError = IsModelConfigError(resp.StatusCode);
                _logger.LogWarning("Gemini görü tespiti ({Model}): HTTP {Status} — {Body}", model, (int)resp.StatusCode, Truncate(responseText, 500));
                return new GeminiHttpResult(false, isConfigError, (int)resp.StatusCode, responseText);
            }

            return new GeminiHttpResult(true, false, (int)resp.StatusCode, responseText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Gemini görü tespiti ({Model}): istek hatası.", model);
            return new GeminiHttpResult(false, false, 0, null);
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
