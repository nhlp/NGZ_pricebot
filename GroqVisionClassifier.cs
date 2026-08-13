using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace PriceBotPipeline;

/// <summary>Görü tabanlı ürün kodu tespiti için DÖRDÜNCÜ sağlayıcı — Groq (2026-08-13, kullanıcı
/// isteği: Gemini'nin iki key'i + Claude'un yanına, TAMAMEN ücretsiz bir dördüncü katman). Google
/// hesabına hiç bağlı olmayan AYRI bir kota havuzu olduğu için Gemini'nin iki key'i de tükense
/// dahi bağımsız çalışır; Claude'dan (paralı) FARKLI olarak ücretsiz — bu yüzden Worker.cs'in
/// codeClassifiers zincirinde Claude'DAN ÖNCE (ucuz/ücretsiz sıralaması) denenir.
///
/// MODEL SEÇİMİ CANLI DOĞRULANDI (2026-08-13, WebSearch+WebFetch ile
/// https://console.groq.com/docs/vision ve https://console.groq.com/docs/rate-limits): kullanıcının
/// önerdiği "Llama 3.2 Vision" ARTIK GÜNCEL DEĞİL (Groq'un Llama 4 Scout görü modelini bile
/// 2026-06-17'de kaldırdığı doğrulandı) — Groq'un resmi belgelerinde şu an listelenen TEK görü
/// modeli <c>qwen/qwen3.6-27b</c>. Ücretsiz katman: 30 RPM / 1.000 RPD / 8.000 TPM / 200.000 TPD
/// (org bazında, kullanıcı bazında değil). Bu proje kendi tarihinde defalarca (Gemini model
/// adlandırması, "limit 15 sanılan 5 çıktı" vakası) ÖĞRENDİĞİ ders burada da geçerli: bu rakamlar/
/// model adı GÜVENCELİ DEĞİL, appsettings.json'dan (rebuild gerekmeden) değiştirilebilir tutuldu —
/// bkz. <c>GroqModel</c> varsayılanı. Kullanıcıyla açıkça konuşuldu: bu sağlayıcı GERÇEK
/// görsellerle CANLI TEST edilmeden üretime güvenilmemeli (Gemini'nin 2026-08-10'da yapılan smoke
/// testiyle AYNI disiplin).
///
/// HALÜSİNASYON ENGELLEME: GeminiVisionClassifier/AnthropicVisionClassifier'daki AYNI desen —
/// model'e her zaman o klasörün Excel kod listesi KAPALI liste olarak veriliyor, Groq'un OpenAI-
/// uyumlu ZORUNLU fonksiyon çağrısı (tool_choice) + fonksiyon parametre şemasındaki JSON Schema
/// `enum` kısıtlamasıyla SADECE bu listeden bir değer ya da "BULUNAMADI" döndürmesi zorlanıyor.
/// Downstream'de (Worker.cs) dönen kod yine de `excelPrices.ContainsKey(code)` ile AYRICA
/// doğrulanıyor (iki katmanlı savunma, enum zorlamasının tek başına ne kadar sıkı olduğunu
/// bilmediğimiz için).
///
/// OPSİYONEL/EK: <c>apiKey</c> boşsa (appsettings.json "GroqApiKey", varsayılan boş) hiçbir ağ
/// isteği atılmaz — GeminiVisionClassifier/AnthropicVisionClassifier'daki AYNI opsiyonellik
/// sözleşmesi.
///
/// BİLİNÇLİ BASİTLEŞTİRME: AnthropicVisionClassifier'daki AYNI gerekçeyle (bkz. o dosyanın
/// başlığı) — sadece TEK bir kısa bekleme + tekrar deneme (geçici/5xx hatada), 429/400/404/içerik
/// engeli doğrudan kalıcı sayılıp devre kesiciye düşülüyor; Gemini'nin ayrıntılı kota-bekleme
/// zincirine burada gerek görülmedi.
///
/// RESMİ/TOPLULUK BİR .NET SDK'SI KULLANILMIYOR — diğer sınıflandırıcılarla AYNI gerekçeyle
/// (dependency-hafifliği) ham HTTP POST + System.Text.Json.</summary>
public sealed partial class GroqVisionClassifier : IProductCodeClassifier
{
    private const string ApiUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const int MaxLongEdgePixels = 1024;
    private const int JpegQuality = 85;
    private const int MaxTokens = 512;

    private const string SystemPrompt =
        "Sen bir toptan çocuk giyim ürün fotoğraflarındaki/etiketlerindeki ürün kodlarını (SKU) " +
        "okuyan bir görsel sınıflandırma asistanısın. Cevabın SADECE verilen kapalı listeden bir " +
        "ürün kodu ya da 'BULUNAMADI' olmalı.";
    private const string UserPrompt =
        "Bu bir toptan ürün fotoğrafı/etiketidir; üzerinde küçük yazılı bir ürün kodu (SKU) " +
        "olabilir — etikette, köşede, kenarda ya da ürünün üzerinde basılı/yazılı sayısal ya da " +
        "alfanümerik bir kod arayın (beden/yaş numaralarıyla KARIŞTIRMAYIN — kod genelde daha uzun " +
        "veya 'code:'/'kod:' gibi bir önekle birlikte gelir). Görünen kodu report_code aracıyla " +
        "bildir. Kod bulanık, küçük ya da kısmen kapalı olabilir — dikkatlice bakın. Listede olmayan " +
        "bir kod görüyorsan veya emin değilsen kesinlikle 'BULUNAMADI' de; listede olmayan bir kod UYDURMA.";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public GroqVisionClassifier(string apiKey, string model, ILogger logger)
    {
        _apiKey = apiKey?.Trim() ?? "";
        // "qwen/qwen3.6-27b" (2026-08-13 canlı doğrulandı, bkz. dosya başı yorumu) — Groq'un şu an
        // resmi belgelerinde listelenen TEK görü modeli. appsettings.json'dan override edilebilir.
        _model = string.IsNullOrWhiteSpace(model) ? "qwen/qwen3.6-27b" : model.Trim();
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

        if (_apiKey.Length == 0)
            _logger.LogInformation("Groq görü tespiti (ürün kodu) KAPALI (GroqApiKey ayarlanmamış).");
        else
            _logger.LogInformation("Groq görü tespiti (ürün kodu) AÇIK (model: {Model}).", _model);
    }

    /// <summary>2026-08-13 canlı testte gözlenen gerçek Groq davranışı (kullanıcı isteği: kota'da
    /// SENKRON beklemek yerine RetryAfter ile Worker.cs'e "diğer görselleri işledikten sonra bir
    /// kez daha dene" sinyali gönder): Groq'un ücretsiz katmanı RPM'den ÖNCE TPM'e (dakikada token)
    /// takılabiliyor — 134 kodluk bir aday listesi (enum şeması) + görsel TEK isteği 8000 TPM
    /// bütçesinin çoğunu tüketiyor, ardışık istekler saniyeler içinde 429 alıyor. Bu yüzden kota
    /// (429) burada da (Gemini'deki gibi) GERÇEK geçici hatadan (5xx/ağ) AYRI ele alınır: kota'da
    /// hiç senkron bekleme yapılmadan RetryAfter döner; sadece gerçek 5xx'te kısa
    /// (<see cref="RetryDelay"/>) bir bekleme + tek seferlik tekrar deneme yapılır.</summary>
    public async Task<(string? Code, bool ApiFailed, TimeSpan? RetryAfter)> ClassifyCodeAsync(
        string imagePath, IReadOnlyCollection<string> candidateCodes, CancellationToken ct)
    {
        if (_apiKey.Length == 0 || candidateCodes.Count == 0)
            return (null, false, null);

        var codes = candidateCodes.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        string base64;
        try
        {
            base64 = BuildInlineImageBase64(imagePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Groq görü tespiti: görsel hazırlanamadı, atlanıyor.");
            return (null, false, null);
        }

        _logger.LogInformation("Groq görü tespiti: ürün kodu için soruluyor -> {File}", Path.GetFileName(imagePath));
        var request = BuildRequest(_model, MaxTokens, SystemPrompt, UserPrompt, base64, "image/jpeg", codes);
        var json = JsonSerializer.Serialize(request, GroqJsonContext.Default.GroqRequest);

        var result = await SendClassifyRequestAsync(json, ct);

        if (result.Label is null && result.IsTransient && !result.IsQuotaExceeded)
        {
            _logger.LogInformation("Groq görü tespiti: '{File}' için geçici bir hata alındı, {Delay} sn sonra tekrar denenecek.",
                Path.GetFileName(imagePath), RetryDelay.TotalSeconds);
            await Task.Delay(RetryDelay, ct);
            result = await SendClassifyRequestAsync(json, ct);
        }

        if (result.Label is null)
        {
            if (result.IsQuotaExceeded)
            {
                var delay = result.RetryDelay ?? RetryDelay;
                _logger.LogInformation("Groq görü tespiti: '{File}' için kota/rate-limit (429) — bu görsel ertelenip diğer görseller işlendikten sonra (~{Delay:N0} sn) tekrar denenecek.",
                    Path.GetFileName(imagePath), delay.TotalSeconds);
                return (null, ApiFailed: false, RetryAfter: delay);
            }
            if (result.IsTransient)
            {
                _logger.LogInformation("Groq görü tespiti: '{File}' için tüm denemelerden sonra da geçici bir hata alındı, bu görsel atlanacak ama klasördeki sonraki görseller için Groq yine denenecek.", Path.GetFileName(imagePath));
                return (null, ApiFailed: false, null);
            }
            return (null, ApiFailed: true, null); // kalıcı hata (config/içerik engeli)
        }
        if (result.Label == NotFoundLabel)
        {
            _logger.LogInformation("Groq görü tespiti: '{File}' için ürün kodu BULUNAMADI yanıtı geldi.", Path.GetFileName(imagePath));
            return (null, false, null);
        }
        return (result.Label, false, null);
    }

    private readonly record struct LabelResult(string? Label, bool IsTransient = false, bool IsQuotaExceeded = false, TimeSpan? RetryDelay = null);

    private async Task<LabelResult> SendClassifyRequestAsync(string json, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = content };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req, ct);
            var responseText = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // tool_use_failed (bkz. IsToolUseFailure dokümantasyonu) HTTP 400 olsa da GEÇİCİ
                // sayılır — görsele/istek anına özgü bir üretim hatası, IsModelConfigError'daki
                // "kalıcı yapılandırma sorunu" anlamına GELMİYOR.
                var isToolUseFailure = resp.StatusCode == HttpStatusCode.BadRequest && IsToolUseFailure(responseText);
                var isTransient = isToolUseFailure || IsTransientError(resp.StatusCode);
                var isQuotaExceeded = IsQuotaError(resp.StatusCode);
                var retryDelay = isQuotaExceeded ? ParseRetryDelay(responseText) : null;
                _logger.LogWarning("Groq görü tespiti ({Model}): HTTP {Status} — {Body}", _model, (int)resp.StatusCode, Truncate(responseText, 500));
                return new LabelResult(null, isTransient, isQuotaExceeded, retryDelay);
            }

            var label = ExtractLabel(responseText, out var blockReason);
            if (blockReason is not null)
            {
                _logger.LogWarning("Groq görü tespiti: yanıt beklenmedik biçimde geldi ({Reason}).", blockReason);
                return new LabelResult(null, IsTransient: false);
            }
            return new LabelResult(label);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Groq görü tespiti: istek hatası.");
            return new LabelResult(null, IsTransient: true);
        }
    }

    /// <summary>GeminiVisionClassifier.BuildInlineImagePart / AnthropicVisionClassifier.
    /// BuildInlineImageBase64 ile AYNI desen — PriceStamper'ın resize matematiğiyle (uzun kenar
    /// sınırı, oranı koruyan ölçekleme) küçültüp JPEG olarak BELLEKTE encode eder.</summary>
    private static string BuildInlineImageBase64(string path)
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
        return Convert.ToBase64String(data.ToArray());
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
