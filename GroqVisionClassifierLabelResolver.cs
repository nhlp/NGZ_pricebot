using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PriceBotPipeline;

/// <summary>GroqVisionClassifier'ın SADECE saf/deterministik kısmı — SkiaSharp/HttpClient'a
/// bağımlı DEĞİL, GeminiVisionClassifierLabelResolver.cs/AnthropicVisionClassifierLabelResolver.cs
/// ile AYNI gerekçeyle ayrı dosyada (bkz. o dosyaların başlığı — SkiaSharp 2.88.8/3.x çakışması).</summary>
public sealed partial class GroqVisionClassifier
{
    private const string ToolName = "report_code";
    private const string NotFoundLabel = "BULUNAMADI";

    internal static bool IsModelConfigError(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

    /// <summary>2026-08-13 canlı testte gözlenen gerçek Groq davranışı: model bazen zorunlu araç
    /// çağrısını doğru biçimlendiremiyor — HTTP 400, gövdede <c>"code":"tool_use_failed"</c>
    /// ("Failed to call a function. Please adjust your prompt."). Bu, <see cref="IsModelConfigError"/>
    /// (yanlış model adı/kalıcı yapılandırma hatası, tekrar denemek anlamsız) İLE AYNI HTTP durumunu
    /// (400) paylaşıyor ama anlamı TAMAMEN FARKLI: görsele/istek anına özgü, muhtemelen geçici bir
    /// üretim hatası (aynı görseli tekrar sormak farklı bir sonuç verebilir) — kalıcı bir
    /// yapılandırma sorunu DEĞİL. Ayrıştırılmadan <see cref="IsModelConfigError"/>'a dahil edilirse
    /// (eski davranış) TEK bir görseldeki geçici bir üretim hatası, Groq'u o klasördeki TÜM kalan
    /// görseller için (devre kesici tetiklenerek) kapatıyordu — oysa sıradaki görsel/tekrar deneme
    /// gayet başarılı olabilir. Bu yüzden HTTP durumundan BAĞIMSIZ, gövde içeriğine bakarak ayrı
    /// tespit edilir ve GEÇİCİ (transient) sayılır — devre kesiciyi TETİKLEMEZ, kısa bir tekrar
    /// denemeye tabidir (bkz. GroqVisionClassifier.ClassifyCodeAsync).</summary>
    internal static bool IsToolUseFailure(string? responseText)
    {
        if (string.IsNullOrEmpty(responseText)) return false;
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            return doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var codeEl) &&
                codeEl.GetString() == "tool_use_failed";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsQuotaError(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests;

    internal static bool IsTransientError(HttpStatusCode statusCode) =>
        (int)statusCode is >= 500 and < 600;

    /// <summary>429 yanıt gövdesinden Groq'un önerdiği bekleme süresini çıkarır — Gemini'nin
    /// AKSİNE Groq'ta yapılandırılmış bir retry-delay alanı GÖZLENMEDİ (2026-08-13 canlı test:
    /// hem RPM hem TPM türü 429 yanıtlarında SADECE insan-okunur mesaj metni vardı, ör.
    /// <c>"Please try again in 22.3875s."</c>), bu yüzden doğrudan <see cref="RetryDelayParser"/>'ın
    /// paylaşılan metin ayrıştırıcısını kullanır (Gemini'nin "retry in Xs" ifadesiyle AYNI regex,
    /// sadece fiil farklı — bkz. RetryDelayParser.cs).</summary>
    internal static TimeSpan? ParseRetryDelay(string? responseText) => RetryDelayParser.ParseFromText(responseText);

    /// <summary>Groq'un OpenAI-uyumlu chat/completions + zorunlu tool_choice isteğini kurar —
    /// GeminiVisionClassifierLabelResolver.BuildRequest / AnthropicVisionClassifierLabelResolver.
    /// BuildRequest ile AYNI "kapalı liste (enum) + zorunlu araç çağrısı" deseni, Groq'un OpenAI
    /// function-calling şemasına uyarlanmış. Görsel `data:` URI olarak `image_url` içeriğine
    /// gömülür (2026-08-13 canlı doğrulanan Groq belgelerine göre — bkz. https://console.groq.com/docs/vision:
    /// base64 yerel dosya desteği "data:image/jpeg;base64,..." biçiminde).</summary>
    internal static GroqRequest BuildRequest(
        string model, int maxTokens, string systemPrompt, string userPrompt,
        string imageBase64, string imageMediaType, List<string> candidateCodes)
    {
        var enumValues = candidateCodes.Append(NotFoundLabel).ToList();
        var dataUri = $"data:{imageMediaType};base64,{imageBase64}";

        return new GroqRequest(
            Model: model,
            MaxTokens: maxTokens,
            // Content her zaman parça DİZİSİ olarak gönderilir (system mesajı için tek bir "text"
            // parçası dahil) — OpenAI-uyumlu chat/completions şemasında content'in "düz string YA
            // DA parça dizisi" ikili biçimi (union type) System.Text.Json'ın kaynak-üretimli
            // serileştiricisiyle temiz modellenemiyor; dizi biçimi her rol için de kabul edilen,
            // stabil bir alternatif olduğu için TEK biçim kullanılıp union'dan kaçınılıyor.
            Messages:
            [
                new GroqMessage("system", [new GroqContentPart(Type: "text", Text: systemPrompt, ImageUrl: null)]),
                new GroqMessage("user",
                [
                    new GroqContentPart(Type: "text", Text: userPrompt, ImageUrl: null),
                    new GroqContentPart(Type: "image_url", Text: null, ImageUrl: new GroqImageUrl(dataUri)),
                ]),
            ],
            Tools:
            [
                new GroqTool("function", new GroqFunctionDef(
                    Name: ToolName,
                    Description: "Görselde bulunan ürün kodunu (ya da BULUNAMADI) verilen kapalı listeden bildirir.",
                    Parameters: new GroqParametersSchema(
                        Type: "object",
                        Properties: new Dictionary<string, GroqPropertySchema>
                        {
                            ["value"] = new GroqPropertySchema(Type: "string", Enum: enumValues),
                        },
                        Required: ["value"])))
            ],
            ToolChoice: new GroqToolChoice("function", new GroqToolChoiceFunction(ToolName)));
    }

    /// <summary>Yanıttan zorunlu fonksiyon çağrısının `value` argümanını çıkarır — OpenAI-uyumlu
    /// function-calling sözleşmesinde `function.arguments` iç içe bir JSON OBJESİ DEĞİL, JSON-
    /// KODLANMIŞ BİR STRING'dir (Gemini/Claude'un doğrudan nesne döndürmesinden farklı) — bu yüzden
    /// İKİ AŞAMALI parse gerekir: dış gövde + arguments string'inin kendisi.</summary>
    internal static string? ExtractLabel(string responseJson, out string? blockReason)
    {
        blockReason = null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl))
            {
                blockReason = errEl.TryGetProperty("type", out var errTypeEl) ? errTypeEl.GetString() ?? "ERROR" : "ERROR";
                return null;
            }

            if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.GetArrayLength() == 0)
            {
                blockReason = "NO_CHOICES";
                return null;
            }

            var message = choicesEl[0].GetProperty("message");
            if (!message.TryGetProperty("tool_calls", out var toolCallsEl) || toolCallsEl.ValueKind != JsonValueKind.Array)
            {
                // Model, aracı hiç çağırmadan serbest metinle (ya da hiç) bitirmiş olabilir.
                blockReason = choicesEl[0].TryGetProperty("finish_reason", out var finishEl)
                    ? finishEl.GetString() ?? "NO_TOOL_CALLS" : "NO_TOOL_CALLS";
                return null;
            }

            foreach (var call in toolCallsEl.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var funcEl) ||
                    !funcEl.TryGetProperty("name", out var nameEl) || nameEl.GetString() != ToolName ||
                    !funcEl.TryGetProperty("arguments", out var argsEl))
                    continue;

                var argsJson = argsEl.GetString();
                if (string.IsNullOrEmpty(argsJson)) continue;

                using var argsDoc = JsonDocument.Parse(argsJson);
                if (argsDoc.RootElement.TryGetProperty("value", out var valueEl))
                    return valueEl.GetString();
            }

            blockReason = "NO_MATCHING_TOOL_CALL";
            return null;
        }
        catch (JsonException)
        {
            blockReason = "PARSE_ERROR";
            return null;
        }
    }
}

// --- Groq (OpenAI-uyumlu) chat/completions istek DTO'ları.

internal sealed record GroqRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")] List<GroqMessage> Messages,
    [property: JsonPropertyName("tools")] List<GroqTool> Tools,
    [property: JsonPropertyName("tool_choice")] GroqToolChoice ToolChoice);

internal sealed record GroqMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] List<GroqContentPart> Content);

internal sealed record GroqContentPart(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("image_url")] GroqImageUrl? ImageUrl);

internal sealed record GroqImageUrl([property: JsonPropertyName("url")] string Url);

internal sealed record GroqTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] GroqFunctionDef Function);

internal sealed record GroqFunctionDef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] GroqParametersSchema Parameters);

internal sealed record GroqParametersSchema(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] Dictionary<string, GroqPropertySchema> Properties,
    [property: JsonPropertyName("required")] List<string> Required);

internal sealed record GroqPropertySchema(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("enum")] List<string> Enum);

internal sealed record GroqToolChoice(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] GroqToolChoiceFunction Function);

internal sealed record GroqToolChoiceFunction([property: JsonPropertyName("name")] string Name);

[JsonSerializable(typeof(GroqRequest))]
internal partial class GroqJsonContext : JsonSerializerContext;
