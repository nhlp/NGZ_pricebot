using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PriceBotPipeline;

/// <summary>GeminiVisionClassifier'ın SADECE saf/deterministik kısmı — SkiaSharp (görsel
/// küçültme) veya Microsoft.Extensions.Logging/HttpClient'a bağımlı DEĞİL, bilinçli olarak
/// ayrı bir dosyada (bkz. `partial class`): test projesi Gemini'nin görsel işleme/ağ
/// tarafını hiç derlemeden bu kısmı test edebilsin diye (BrandMatcher.cs'in "tamamen saf/
/// deterministik" olma gerekçesiyle aynı — bkz. BrandMatcher.cs dosya başı yorumu). Ayrı
/// dosya tutmanın asıl nedeni: test projesi NPOI üzerinden zaten SkiaSharp 3.x'e bağımlı,
/// ana proje ise 2.88.8 kullanıyor — GeminiVisionClassifier.cs'in TAMAMI test derlemesine
/// dahil edilseydi bu iki sürüm çakışırdı (NU1605).
///
/// 2026-08-10 (canlı Gemini testi sonrası): istek/yanıt DTO'ları + <see cref="BuildRequest"/>/
/// <see cref="ExtractLabel"/>/<see cref="IsModelConfigError"/> de buraya taşındı — hiçbiri
/// SkiaSharp'a bağımlı değil (sadece System.Text.Json), bu yüzden Gemini'nin gerçek v1beta
/// generateContent istek şeklini/yanıt ayrıştırmasını (BuildRequestTests/ExtractLabelTests) ağa
/// hiç çıkmadan, "kullandığımız API sürümünün" (appsettings'teki model adları, enum-zorlamalı
/// şema) gerçek sözleşmesine karşı test etmeyi mümkün kılıyor.</summary>
public sealed partial class GeminiVisionClassifier
{
    // BuildRequest/ExtractLabel'in ortak kullandığı iki sabit buraya taşındı (GeminiVisionClassifier.cs
    // yerine) — SkiaSharp'a bağımlı DEĞİLLER, burada olmaları bu dosyanın Tests projesine SkiaSharp
    // olmadan derlenebilmesini sağlıyor (partial class'ın diğer parçası, ApiBase/MaxImages gibi ağ/
    // görsel özel sabitleri barındırmaya devam ediyor).
    private const string NotFoundLabel = "BULUNAMADI";
    // Gemini response schema'sındaki tek alan adı — marka VE kod cevabı için ORTAK; iki ayrı
    // schema şekli tutmaya gerek yok, ikisi de "aday listesinden bir string" istiyor.
    private const string ResponseFieldName = "value";

    /// <summary>Nebim view'inde aynı marka adı birden fazla satırda olabiliyor (HIPP, TABU
    /// gibi — bkz. BrandMatcher.cs başındaki aynı gözlem, NetCarpan'ları aynı olduğu için
    /// zararsız kabul ediliyor). Enum'a her ismi bir kez koymak için tekilleştirilir.</summary>
    internal static List<string> BuildDistinctCandidateNames(IReadOnlyList<BrandMultiplier> candidates) =>
        candidates
            .Select(b => b.FullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Gemini'nin döndürdüğü (enum zorlaması sayesinde adaylardan biriyle BİREBİR
    /// eşleşmesi garanti) etiketi BrandMultiplier'a çözer. Aynı isimde birden fazla satır
    /// varsa ilkini döner (MatchFromUserText'teki aynı varsayımla tutarlı).</summary>
    internal static BrandMultiplier? ResolveLabelToBrand(string label, IReadOnlyList<BrandMultiplier> candidates) =>
        candidates.FirstOrDefault(b => string.Equals(b.FullName, label, StringComparison.Ordinal));

    /// <summary>SADECE HTTP 400/404 kalıcı (model'e özgü) konfigürasyon hatası sayılır — bkz.
    /// dosya başı "YEDEK MODEL ZİNCİRİ" notu (GeminiVisionClassifier.cs). 5xx/timeout gibi
    /// geçici hatalarda false. 429 (kota) ARTIK burada değil — bkz. <see cref="IsQuotaError"/>,
    /// kendi ayrı ele alışı var (2026-08-13).</summary>
    internal static bool IsModelConfigError(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

    /// <summary>GEÇİCİ (aynı istek büyük ihtimalle bir sonraki denemede başarılı olur) sayılan
    /// HTTP durumları — sadece 5xx (sunucu tarafı, "usually temporary"; gerçek vaka 2026-08-10:
    /// "model şu anda yoğun talep görüyor" 503'ü). Diğer 4xx'ler (401/403 kimlik/izin gibi kalıcı
    /// sorunlar) KALICI sayılır, false döner. 429 (kota) bu fonksiyonun DIŞINDA — bkz.
    /// <see cref="IsQuotaError"/>: eskiden (2026-08-10) 429 burada da "kalıcı" (false) sayılıp
    /// diğer kalıcı hatalarla aynı işlem görüyordu (hiç tekrar denemeden devre kesiciyi tetikle),
    /// ama gerçek vaka (2026-08-13, NGZ/MİNİCE klasörü: `generate_content_free_tier_requests`
    /// limit:5 — bkz. GeminiVisionClassifier.cs dosya başı notu) 429'un 400/404'ten (kalıcı
    /// config hatası) ve 5xx'ten (rastgele geçici sunucu hatası) FARKLI bir üçüncü kategori
    /// olduğunu gösterdi: kısa bir bekleme sonrası neredeyse kesin başarılı olacak (RPM
    /// penceresi dolar) VE Google'ın kota metriği MODELE ÖZGÜ (`model: gemini-3.6-flash` gibi)
    /// olduğu için farklı bir modelin ayrı bir kotası olması muhtemel — bu yüzden artık kendi
    /// ayrı bekle-ve-tekrar-dene + yedek-model mantığına sahip (bkz. GeminiVisionClassifier.cs
    /// "devre kesici" kullanımı, Worker.cs geminiCodeApiHealthy).</summary>
    internal static bool IsTransientError(HttpStatusCode statusCode) =>
        (int)statusCode is >= 500 and < 600;

    /// <summary>HTTP 429 (RESOURCE_EXHAUSTED / kota aşımı) — bkz. <see cref="IsTransientError"/>
    /// dokümantasyonundaki 2026-08-13 notu. Kendi ayrı bayrağı var çünkü davranışı ne
    /// <see cref="IsModelConfigError"/> (400/404, tekrar denemek anlamsız) ne de
    /// <see cref="IsTransientError"/> (5xx, sabit kısa bekleme yeterli) ile aynı: 429'da Google
    /// genelde makul bir bekleme süresi öneriyor (bkz. <see cref="ParseRetryDelay"/>), o süre
    /// kadar beklemek başarı ihtimalini gerçek anlamda artırıyor (rastgele bir 5xx'te olduğu
    /// gibi "belki düzelir" değil, "RPM penceresi kesinlikle dolacak").</summary>
    internal static bool IsQuotaError(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests;

    private static readonly Regex RetryDelayTextPattern =
        new(@"retry in\s+([\d.]+)\s*s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Üst sınır bilinçli olarak kısa tutuldu (2026-08-13): bu, ana 10 sn'lik tarama
    /// döngüsünü bloklayan senkron bir bekleme (Worker.cs görsel-başına damgalama akışının
    /// içinde) — Google'ın önerdiği süre bu sınırı aşarsa (ör. günlük/RPD kotası tükenmişse çok
    /// daha uzun bir süre önerebilir) beklemeye değmez, doğrudan kalıcı hata gibi ele alınıp
    /// devre kesiciye düşülmesi daha sağlıklı.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>429 yanıt gövdesinden Google'ın önerdiği bekleme süresini çıkarır. Önce
    /// yapılandırılmış <c>google.rpc.RetryInfo</c> alanını dener (<c>error.details[].retryDelay</c>,
    /// protobuf Duration string'i, ör. <c>"6.276786240s"</c>) — bulunursa bu en güvenilir kaynak.
    /// Yoksa/parse edilemezse insan-okunur mesaj metnindeki ifadeyi dener (gerçek Gemini 429
    /// yanıtında görülen biçim, 2026-08-13: <c>"Please retry in 6.27678624s."</c>). İkisi de
    /// bulunamazsa null döner — çağıran taraf kendi sabit varsayılanına (<c>CodeRetryDelay</c>)
    /// düşer. Sonuç her zaman <see cref="MinRetryDelay"/>/<see cref="MaxRetryDelay"/> arasına
    /// kelepçelenir.</summary>
    internal static TimeSpan? ParseRetryDelay(string? responseText)
    {
        if (string.IsNullOrEmpty(responseText)) return null;

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("details", out var details) &&
                details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("retryDelay", out var retryDelayEl) &&
                        retryDelayEl.ValueKind == JsonValueKind.String &&
                        TryParseDurationSeconds(retryDelayEl.GetString(), out var seconds))
                    {
                        return Clamp(TimeSpan.FromSeconds(seconds));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Beklenmeyen/bozuk gövde — metin geri düşüşüne bırak.
        }

        var textMatch = RetryDelayTextPattern.Match(responseText);
        if (textMatch.Success &&
            double.TryParse(textMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var textSeconds))
        {
            return Clamp(TimeSpan.FromSeconds(textSeconds));
        }

        return null;

        static TimeSpan Clamp(TimeSpan delay) =>
            delay < MinRetryDelay ? MinRetryDelay : delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private static bool TryParseDurationSeconds(string? duration, out double seconds)
    {
        seconds = 0;
        return !string.IsNullOrEmpty(duration) && duration.EndsWith('s') &&
            double.TryParse(duration[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>Gemini v1beta generateContent isteğini kurar — kapalı-liste (enum) zorlaması
    /// <paramref name="candidateLabels"/> + <see cref="NotFoundLabel"/>'i responseSchema.enum'a
    /// koyarak sağlanır (bkz. dosya başı GeminiVisionClassifier.cs "HALÜSİNASYON ENGELLEME"
    /// notu).</summary>
    internal static GeminiRequest BuildRequest(List<GeminiPart> imageParts, List<string> candidateLabels, string userPrompt, string systemPrompt)
    {
        var enumValues = candidateLabels.Append(NotFoundLabel).ToList();
        var parts = new List<GeminiPart>(imageParts) { new GeminiPart(Text: userPrompt, InlineData: null) };

        return new GeminiRequest(
            SystemInstruction: new GeminiContent(Role: null, Parts: [new GeminiPart(Text: systemPrompt, InlineData: null)]),
            Contents: [new GeminiContent(Role: "user", Parts: parts)],
            GenerationConfig: new GeminiGenerationConfig(
                ResponseMimeType: "application/json",
                ResponseSchema: new GeminiSchema(
                    Type: "OBJECT",
                    Properties: new Dictionary<string, GeminiSchema>
                    {
                        [ResponseFieldName] = new GeminiSchema(Type: "STRING", Properties: null, Required: null, Enum: enumValues),
                    },
                    Required: [ResponseFieldName],
                    Enum: null),
                // maxOutputTokens 200'ken Gemini 2.5/3 Flash ailesinde canlı testte MAX_TOKENS ile
                // BOŞ yanıt görüldü (2026-08-10) — bu modeller varsayılan olarak "thinking" (iç
                // muhakeme) tokenleri üretiyor ve bunlar da maxOutputTokens'tan düşülüyor; küçük bir
                // limit asıl JSON çıktısına hiç sıra gelmeden kesiyordu (bilinen bir Gemini davranışı,
                // bkz. https://github.com/valentinfrlch/ha-llmvision/issues/609). `thinkingConfig.
                // thinkingBudget=0` ile iç muhakemeyi kapatmayı denedik ama "gemini-flash-latest"in
                // çözüldüğü modelde 400 (Invalid Argument) döndü — bazı Gemini 3.x Flash modelleri
                // thinking'i tam kapatmaya izin vermiyor (bkz. Google dokümantasyonu). Model-bağımsız
                // ve sağlam kalması için thinkingConfig'i ZORLAMIYORUZ, sadece maxOutputTokens'ı hem
                // thinking'e hem asıl JSON çıktısına yetecek kadar cömert tutuyoruz.
                MaxOutputTokens: 2048));
    }

    /// <summary>Yanıttan `{"value": "..."}` etiketini çıkarır. Engellenmiş/yarım kalmış bir
    /// yanıtsa (blockReason doluysa) etiket yerine sebep döner, çağıran taraf null saymalı.</summary>
    internal static string? ExtractLabel(string responseJson, out string? blockReason)
    {
        blockReason = null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("promptFeedback", out var feedback) &&
                feedback.TryGetProperty("blockReason", out var reasonEl))
            {
                blockReason = reasonEl.GetString() ?? "BLOCKED";
                return null;
            }

            if (!root.TryGetProperty("candidates", out var candidatesEl) || candidatesEl.GetArrayLength() == 0)
            {
                blockReason = "NO_CANDIDATES";
                return null;
            }

            var first = candidatesEl[0];
            if (first.TryGetProperty("finishReason", out var finishEl) &&
                !string.Equals(finishEl.GetString(), "STOP", StringComparison.Ordinal))
            {
                blockReason = finishEl.GetString() ?? "NOT_STOP";
                return null;
            }

            var text = first.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            using var valueDoc = JsonDocument.Parse(text);
            return valueDoc.RootElement.TryGetProperty(ResponseFieldName, out var valueEl) ? valueEl.GetString() : null;
        }
        catch (JsonException)
        {
            blockReason = "PARSE_ERROR";
            return null;
        }
    }
}

// --- Gemini REST API istek/yanıt DTO'ları (camelCase — Google'ın JSON şeması). Anonim
// nesne yerine record kullanılıyor ki alan adları derleme zamanında sabitlensin. Sadece
// istek tarafı burada modelleniyor; yanıt System.Text.Json.JsonDocument ile serbestçe
// (record'suz) okunuyor çünkü sadece birkaç alanına ihtiyaç var (bkz. ExtractLabel).

internal sealed record GeminiRequest(
    [property: JsonPropertyName("systemInstruction")] GeminiContent SystemInstruction,
    [property: JsonPropertyName("contents")] List<GeminiContent> Contents,
    [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

internal sealed record GeminiContent(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("parts")] List<GeminiPart> Parts);

internal sealed record GeminiPart(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("inlineData")] GeminiInlineData? InlineData);

internal sealed record GeminiInlineData(
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("data")] string Data);

internal sealed record GeminiGenerationConfig(
    [property: JsonPropertyName("responseMimeType")] string ResponseMimeType,
    [property: JsonPropertyName("responseSchema")] GeminiSchema ResponseSchema,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);

internal sealed record GeminiSchema(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] Dictionary<string, GeminiSchema>? Properties,
    [property: JsonPropertyName("required")] List<string>? Required,
    [property: JsonPropertyName("enum")] List<string>? Enum);

[JsonSerializable(typeof(GeminiRequest))]
internal partial class GeminiJsonContext : JsonSerializerContext;
