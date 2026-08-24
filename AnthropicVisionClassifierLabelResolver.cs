using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PriceBotPipeline;

/// <summary>AnthropicVisionClassifier'ın SADECE saf/deterministik kısmı — SkiaSharp (görsel
/// küçültme) veya Microsoft.Extensions.Logging/HttpClient'a bağımlı DEĞİL, bilinçli olarak ayrı
/// bir dosyada (bkz. `partial class`) — GeminiVisionClassifierLabelResolver.cs'teki AYNI gerekçeyle
/// (bkz. o dosyanın başlığı): test projesi NPOI üzerinden SkiaSharp 3.x'e bağımlı, ana proje ise
/// 2.88.8 kullanıyor; AnthropicVisionClassifier.cs'in TAMAMI test derlemesine dahil edilseydi bu
/// iki sürüm çakışırdı (NU1605).</summary>
public sealed partial class AnthropicVisionClassifier
{
    // Kod ve marka tespiti AYRI araç adları kullanır (2026-08-24, IBrandClassifier eklenmesiyle) —
    // GroqVisionClassifierLabelResolver.cs'teki AYNI gerekçe.
    private const string CodeToolName = "report_code";
    private const string BrandToolName = "report_brand";
    private const string NotFoundLabel = "BULUNAMADI";

    /// <summary>SADECE HTTP 400/404 kalıcı (model'e özgü) konfigürasyon hatası sayılır —
    /// GeminiVisionClassifierLabelResolver.cs'teki IsModelConfigError ile AYNI sınıflandırma
    /// mantığı, Anthropic'in hata kodlarına uyarlanmış (bkz. https://docs.anthropic.com/en/api/errors
    /// — invalid_request_error=400, not_found_error=404).</summary>
    internal static bool IsModelConfigError(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

    /// <summary>429 (rate_limit_error) — Gemini/Groq'taki IsQuotaError'ın karşılığı. 2026-08-13'ten
    /// beri (kullanıcı isteği) Gemini/Groq ile AYNI şekilde ele alınır: SENKRON beklemek yerine
    /// RetryAfter ile Worker.cs'e "diğer görselleri işledikten sonra bir kez daha dene" sinyali
    /// gönderilir (bkz. IProductCodeClassifier.cs). Claude zaten Gemini'nin İKİ key'i + Groq'un da
    /// tükendiği son çare basamağı olduğu için buraya düşen hacim çok düşük (klasör başına tipik
    /// olarak 0-1) — yine de aynı RetryAfter deseninin (Gemini'nin ayrıntılı bekle+yedek-model
    /// zincirinden FARKLI olarak burada TEK adım: HTTP `Retry-After` başlığı, bkz.
    /// AnthropicVisionClassifier.SendClassifyRequestAsync) uygulanması ek maliyetsiz olduğu için
    /// tutarlılık adına eklendi.</summary>
    internal static bool IsQuotaError(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests;

    /// <summary>GEÇİCİ sayılan HTTP durumları — 5xx (sunucu tarafı). Anthropic'in kendine özgü 529
    /// (overloaded_error, "sunucu şu an aşırı yüklü") de standart 5xx aralığına girdiği için ayrı
    /// bir kontrol gerekmiyor.</summary>
    internal static bool IsTransientError(HttpStatusCode statusCode) =>
        (int)statusCode is >= 500 and < 600;

    /// <summary>429 yanıt GÖVDESİNDEN (HTTP `Retry-After` başlığından DEĞİL — o, response header'a
    /// erişimi olan network dosyasında ayrıca okunur, bkz. AnthropicVisionClassifier.
    /// SendClassifyRequestAsync) önerilen bekleme süresini dener — Anthropic'in resmi belgelerinde
    /// (https://docs.anthropic.com/en/api/rate-limits) asıl mekanizma HTTP başlığı olsa da, mesaj
    /// metninde de Gemini/Groq'takine benzer bir ifade olması ihtimaline karşı savunmacı bir
    /// yedek — bu ÜÇÜNCÜ sağlayıcı henüz canlı bir 429 ile test edilmedi (kullanıcı henüz
    /// AnthropicApiKey eklemedi), bu yüzden ihtiyatlı: hem başlık hem metin denenir, ikisi de
    /// bulunamazsa çağıran taraf kendi sabit varsayılanına düşer.</summary>
    internal static TimeSpan? ParseRetryDelay(string? responseText) => RetryDelayParser.ParseFromText(responseText);

    /// <summary>Claude'un Messages API'sine (v1/messages) zorunlu araç çağrısı (tool_choice) ile
    /// kapalı-liste (enum) tespiti isteği kurar — Gemini'nin responseSchema.enum zorlamasının
    /// Claude API'deki karşılığı: <paramref name="candidateCodes"/> + BULUNAMADI, tool'un
    /// input_schema'sındaki "value" alanının JSON Schema `enum`'una konur, `tool_choice` bu aracın
    /// ZORUNLU çağrılmasını sağlar (serbest metne bırakılsa model listede olmayan bir kod
    /// uydurabilirdi — bkz. GeminiVisionClassifier.cs "HALÜSİNASYON ENGELLEME" notu, aynı
    /// gerekçe).</summary>
    internal static AnthropicRequest BuildRequest(
        string model, int maxTokens, string systemPrompt, string userPrompt,
        string imageBase64, string imageMediaType, List<string> candidateLabels,
        string toolName, string toolDescription)
    {
        var enumValues = candidateLabels.Append(NotFoundLabel).ToList();

        return new AnthropicRequest(
            Model: model,
            MaxTokens: maxTokens,
            System: systemPrompt,
            Messages:
            [
                new AnthropicMessage("user",
                [
                    new AnthropicContent(Type: "image", Text: null,
                        Source: new AnthropicImageSource("base64", imageMediaType, imageBase64)),
                    new AnthropicContent(Type: "text", Text: userPrompt, Source: null),
                ])
            ],
            Tools:
            [
                new AnthropicTool(
                    Name: toolName,
                    Description: toolDescription,
                    InputSchema: new AnthropicInputSchema(
                        Type: "object",
                        Properties: new Dictionary<string, AnthropicPropertySchema>
                        {
                            ["value"] = new AnthropicPropertySchema(Type: "string", Enum: enumValues),
                        },
                        Required: ["value"]))
            ],
            ToolChoice: new AnthropicToolChoice(Type: "tool", Name: toolName));
    }

    /// <summary>Yanıttan zorunlu araç çağrısının `value` girdisini çıkarır. Claude bir hata
    /// döndürdüyse (`"type": "error"`) ya da beklenen `tool_use` bloğu yoksa (ör. içerik engeli,
    /// max_tokens'a takılma) etiket yerine sebep döner, çağıran taraf null saymalı — Gemini'nin
    /// ExtractLabel'iyle AYNI sözleşme.</summary>
    internal static string? ExtractLabel(string responseJson, string expectedToolName, out string? blockReason)
    {
        blockReason = null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "error")
            {
                blockReason = root.TryGetProperty("error", out var errEl) && errEl.TryGetProperty("type", out var errTypeEl)
                    ? errTypeEl.GetString() ?? "ERROR"
                    : "ERROR";
                return null;
            }

            if (!root.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.Array)
            {
                blockReason = "NO_CONTENT";
                return null;
            }

            foreach (var block in contentEl.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var blockTypeEl) && blockTypeEl.GetString() == "tool_use" &&
                    block.TryGetProperty("name", out var nameEl) && nameEl.GetString() == expectedToolName &&
                    block.TryGetProperty("input", out var inputEl) &&
                    inputEl.TryGetProperty("value", out var valueEl))
                {
                    return valueEl.GetString();
                }
            }

            // stop_reason "max_tokens"/"end_turn" gibi bir sebeple araç hiç çağrılmamış olabilir.
            blockReason = root.TryGetProperty("stop_reason", out var stopEl) ? stopEl.GetString() ?? "NO_TOOL_USE" : "NO_TOOL_USE";
            return null;
        }
        catch (JsonException)
        {
            blockReason = "PARSE_ERROR";
            return null;
        }
    }

    /// <summary>GeminiVisionClassifierLabelResolver.BuildDistinctCandidateNames ile AYNI.</summary>
    internal static List<string> BuildDistinctCandidateNames(IReadOnlyList<BrandMultiplier> candidates) =>
        candidates
            .Select(b => b.FullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>GeminiVisionClassifierLabelResolver.ResolveLabelToBrand ile AYNI.</summary>
    internal static BrandMultiplier? ResolveLabelToBrand(string label, IReadOnlyList<BrandMultiplier> candidates) =>
        candidates.FirstOrDefault(b => string.Equals(b.FullName, label, StringComparison.Ordinal));

    /// <summary>GeminiVisionClassifierLabelResolver.BuildBrandUserPrompt ile AYNI desen — bkz. o
    /// metodun dokümantasyonu (IBrandClassifier.cs "OCR İPUCU ENJEKSİYONU" notu).</summary>
    internal static string BuildBrandUserPrompt(string baseBrandPrompt, string? ocrHint) =>
        string.IsNullOrWhiteSpace(ocrHint)
            ? baseBrandPrompt
            : $"{baseBrandPrompt} EK İPUCU (kesin değil — OCR'ın bulanık/yaklaşık okuması): bu " +
              $"görsellerde şuna benzer harfler görüldü: {ocrHint}. Bu ipucu markayı listeden " +
              "seçerken yardımcı olabilir ama listedeki isimle TAM örtüşmüyorsa yine de görsele " +
              "bakarak en uygun markayı seç; hiçbiri uymuyorsa 'BULUNAMADI' de.";
}

// --- Anthropic Messages API istek DTO'ları (camelCase değil, Anthropic'in JSON şeması snake_case).
// Yanıt tarafı (GeminiVisionClassifierLabelResolver.cs'teki ExtractLabel deseniyle aynı) record'suz,
// System.Text.Json.JsonDocument ile serbestçe okunuyor (bkz. ExtractLabel) çünkü sadece birkaç
// alanına ihtiyaç var.

internal sealed record AnthropicRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("messages")] List<AnthropicMessage> Messages,
    [property: JsonPropertyName("tools")] List<AnthropicTool> Tools,
    [property: JsonPropertyName("tool_choice")] AnthropicToolChoice ToolChoice);

internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] List<AnthropicContent> Content);

// 2026-08-13 canlı testte bulundu: Claude'un content-block şeması "image" tipi bir blokta "text"
// alanının (null bile olsa) HİÇ bulunmasına izin vermiyor — "Extra inputs are not permitted" (400).
// System.Text.Json varsayılan olarak null alanları da serileştirdiği için (explicit "text": null),
// JsonIgnoreCondition.WhenWritingNull ŞART — bu, Groq'un content-part'ları için zaten TEK biçim
// (her zaman dizi) kullanılarak kaçınılan AYNI union-type tuzağı, burada gözden kaçmıştı.
internal sealed record AnthropicContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonPropertyName("source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnthropicImageSource? Source);

internal sealed record AnthropicImageSource(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("data")] string Data);

internal sealed record AnthropicTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input_schema")] AnthropicInputSchema InputSchema);

internal sealed record AnthropicInputSchema(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] Dictionary<string, AnthropicPropertySchema> Properties,
    [property: JsonPropertyName("required")] List<string> Required);

internal sealed record AnthropicPropertySchema(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("enum")] List<string> Enum);

internal sealed record AnthropicToolChoice(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name);

[JsonSerializable(typeof(AnthropicRequest))]
internal partial class AnthropicJsonContext : JsonSerializerContext;
