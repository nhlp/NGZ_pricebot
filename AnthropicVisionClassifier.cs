using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace PriceBotPipeline;

/// <summary>Görü tabanlı ürün kodu tespiti için ÜÇÜNCÜ (son çare) sağlayıcı — Anthropic Claude
/// (2026-08-13, kullanıcı isteği: "gemini sınırını başka bir api kullanarak aşabilir miyiz").
/// Gerçek vaka: NGZ "NET MEVSİMLİK FİYAT LİSTESİ 2026.xls" / MİNİCE klasöründe Google'ın ücretsiz
/// katman kotası (`generate_content_free_tier_requests`, `limit: 5`) tek bir klasörde 6. istekten
/// sonra tükenmişti (bkz. GeminiVisionClassifier.cs dosya başı "429 (KOTA)" notu — o zincir artık
/// bekle+yedek-model+ikinci-key dener, ama HEPSİ tükenirse hâlâ bir duvar var). Claude, o duvarın
/// arkasındaki SON basamak: Worker.cs'in codeClassifiers zincirinde Gemini birincil+ikincil key'in
/// İKİSİ de tükendiğinde devreye giriyor.
///
/// Claude'un ücretsiz bir katmanı YOK (Gemini'nin aksine) — bu yüzden Anthropic BİLİNÇLİ olarak
/// EN SONA (en pahalı, en güvenilir) kondu, Gemini'nin iki (ücretsiz) key'i denendikten SONRA.
/// Ama bu noktaya düşen görsel sayısı klasör başına tipik olarak 0-1 (alias fix + Gemini'nin kendi
/// iki-key zinciri sonrası, bkz. CLAUDE.md ExcelPriceReader.AddSpacedSuffixAliases notu) — beklenen
/// maliyet ayda kuruşlar mertebesinde.
///
/// HALÜSİNASYON ENGELLEME: GeminiVisionClassifier'daki AYNI desen — model'e her zaman o klasörün
/// Excel kod listesi KAPALI liste olarak veriliyor, Claude'un zorunlu araç çağrısı (tool_choice) +
/// aracın input_schema'sındaki JSON Schema `enum` kısıtlamasıyla (bkz.
/// AnthropicVisionClassifierLabelResolver.cs BuildRequest) SADECE bu listeden bir değer ya da
/// "BULUNAMADI" döndürmesi zorlanıyor. Downstream'de (Worker.cs) dönen kod yine de
/// `excelPrices.ContainsKey(code)` ile ayrıca doğrulanıyor — enum zorlaması %100 garantili
/// olmasa bile (Gemini'nin responseSchema'sı kadar sıkı belgelenmiş değil), halüsinasyon riski iki
/// katmanlı savunmayla kapatılmış oluyor.
///
/// OPSİYONEL/EK: <c>apiKey</c> boşsa (appsettings.json "AnthropicApiKey", varsayılan boş) hiçbir
/// ağ isteği atılmaz, <see cref="ClassifyCodeAsync"/> her zaman (null,false) döner —
/// GeminiVisionClassifier'daki AYNI opsiyonellik sözleşmesi, Worker.cs bu sınıfı hep (null kontrolü
/// olmadan) parametre olarak geçirebilir.
///
/// BİLİNÇLİ BASİTLEŞTİRME: Gemini'nin 429'da (bekle-Google'ın-önerdiği-süre + aynı modele tekrar +
/// yedek modele tekrar) yaptığı ayrıntılı zincir burada YOK — sadece geçici (5xx) bir hatada TEK
/// bir kısa bekleme + tekrar deneme var, 429/400/404/içerik-engeli doğrudan kalıcı sayılıp devre
/// kesiciye (bkz. IProductCodeClassifier.cs, Worker.cs codeClassifierHealthy) düşülüyor. Gerekçe:
/// bu zaten Gemini'nin TAMAMEN tükendiği son basamak, buraya düşen hacim çok düşük olduğu için
/// ayrıntılı bir kota-bekleme mantığına şu an gerek görülmedi — ileride gerçek bir 429 deseni
/// gözlenirse GeminiVisionClassifier'daki ParseRetryDelay/wait-and-retry deseni buraya da
/// taşınabilir.
///
/// RESMİ/TOPLULUK BİR .NET SDK'SI KULLANILMIYOR — GeminiVisionClassifier'daki AYNI gerekçeyle
/// (dependency-hafifliği) ham HTTP POST + System.Text.Json.</summary>
public sealed partial class AnthropicVisionClassifier : IProductCodeClassifier
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
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

    /// <summary>Geçici bir hatadan sonra tek seferlik tekrar deneme öncesi bekleme —
    /// GeminiVisionClassifier.CodeRetryDelay ile aynı ruh (kısa, sabit; amaç sunucu tarafındaki
    /// anlık bir yoğunluğun geçmesine küçük bir şans tanımak).</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public AnthropicVisionClassifier(string apiKey, string model, ILogger logger)
    {
        _apiKey = apiKey?.Trim() ?? "";
        _model = string.IsNullOrWhiteSpace(model) ? "claude-haiku-4-5-20251001" : model.Trim();
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

        if (_apiKey.Length == 0)
            _logger.LogInformation("Claude görü tespiti (ürün kodu, son çare) KAPALI (AnthropicApiKey ayarlanmamış).");
        else
            _logger.LogInformation("Claude görü tespiti (ürün kodu, son çare) AÇIK (model: {Model}).", _model);
    }

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
            // Görsel decode/resize hatası — OCR tarafı zaten aynı görseli işlemiş olduğu için son
            // derece nadir olmalı. Kalıcı bir hata — aynı dosyayı tekrar denemek aynı sonucu verir,
            // ama bu görsele ÖZGÜ, devre kesiciyi tetiklemeye gerek yok.
            _logger.LogWarning(ex, "Claude görü tespiti: görsel hazırlanamadı, atlanıyor.");
            return (null, false, null);
        }

        _logger.LogInformation("Claude görü tespiti: ürün kodu için soruluyor -> {File}", Path.GetFileName(imagePath));
        var request = BuildRequest(_model, MaxTokens, SystemPrompt, UserPrompt, base64, "image/jpeg", codes);
        var json = JsonSerializer.Serialize(request, AnthropicJsonContext.Default.AnthropicRequest);

        var result = await SendClassifyRequestAsync(json, ct);

        if (result.Label is null && result.IsTransient && !result.IsQuotaExceeded)
        {
            _logger.LogInformation("Claude görü tespiti: '{File}' için geçici bir hata alındı, {Delay} sn sonra tekrar denenecek.",
                Path.GetFileName(imagePath), RetryDelay.TotalSeconds);
            await Task.Delay(RetryDelay, ct);
            result = await SendClassifyRequestAsync(json, ct);
        }

        if (result.Label is null)
        {
            if (result.IsQuotaExceeded)
            {
                // 2026-08-13 (kullanıcı isteği, Gemini/Groq ile tutarlılık): kota/rate-limit'te
                // SENKRON beklemiyoruz — Worker.cs bu görseli erteleyip diğerlerini işledikten
                // sonra bir kez daha deneyecek.
                var delay = result.RetryDelay ?? RetryDelay;
                _logger.LogInformation("Claude görü tespiti: '{File}' için kota/rate-limit (429) — bu görsel ertelenip diğer görseller işlendikten sonra (~{Delay:N0} sn) tekrar denenecek.",
                    Path.GetFileName(imagePath), delay.TotalSeconds);
                return (null, ApiFailed: false, RetryAfter: delay);
            }
            if (result.IsTransient)
            {
                _logger.LogInformation("Claude görü tespiti: '{File}' için tüm denemelerden sonra da geçici bir hata alındı, bu görsel atlanacak ama klasördeki sonraki görseller için Claude yine denenecek.", Path.GetFileName(imagePath));
                return (null, ApiFailed: false, null);
            }
            return (null, ApiFailed: true, null); // kalıcı hata (config/içerik engeli)
        }
        if (result.Label == NotFoundLabel)
        {
            _logger.LogInformation("Claude görü tespiti: '{File}' için ürün kodu BULUNAMADI yanıtı geldi.", Path.GetFileName(imagePath));
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
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", ApiVersion);

            using var resp = await _http.SendAsync(req, ct);
            var responseText = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var isTransient = IsTransientError(resp.StatusCode);
                var isQuotaExceeded = IsQuotaError(resp.StatusCode);
                // Anthropic'in resmi belgelerine göre asıl mekanizma HTTP `Retry-After` başlığı
                // (https://docs.anthropic.com/en/api/rate-limits) — gövde metnindeki ifadeye
                // (ParseRetryDelay, savunmacı yedek) ÖNCELİKLİDİR. Bu ÜÇÜNCÜ sağlayıcı henüz canlı
                // bir 429 ile test edilmedi (bkz. LabelResolver dosya başı notu).
                TimeSpan? retryDelay = null;
                if (isQuotaExceeded)
                {
                    retryDelay = resp.Headers.RetryAfter?.Delta is { } headerDelta
                        ? RetryDelayParser.Clamp(headerDelta)
                        : ParseRetryDelay(responseText);
                }
                _logger.LogWarning("Claude görü tespiti ({Model}): HTTP {Status} — {Body}", _model, (int)resp.StatusCode, Truncate(responseText, 500));
                return new LabelResult(null, isTransient, isQuotaExceeded, retryDelay);
            }

            var label = ExtractLabel(responseText, out var blockReason);
            if (blockReason is not null)
            {
                // Beklenen tool_use bloğu yoksa (içerik engeli, max_tokens vb.) kalıcı sayılır —
                // aynı görsel/istekle tekrar denense de aynı sonucu verir.
                _logger.LogWarning("Claude görü tespiti: yanıt beklenmedik biçimde geldi ({Reason}).", blockReason);
                return new LabelResult(null, IsTransient: false);
            }
            return new LabelResult(label);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Claude görü tespiti: istek hatası.");
            return new LabelResult(null, IsTransient: true);
        }
    }

    /// <summary>Görseli PriceStamper'ın kullandığı resize matematiğiyle (uzun kenar sınırı, oranı
    /// koruyan ölçekleme) küçültüp JPEG olarak BELLEKTE encode eder — GeminiVisionClassifier.
    /// BuildInlineImagePart ile aynı desen, disk yazmadan sadece istek boyutunu/token maliyetini
    /// kaynak çözünürlükten bağımsız sınırlı tutmak için.</summary>
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
