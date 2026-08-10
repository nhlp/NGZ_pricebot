using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// dosya başı "YEDEK MODEL ZİNCİRİ" notu (GeminiVisionClassifier.cs). 429/5xx/timeout gibi
    /// geçici ya da modelden bağımsız hatalarda false: yedek modele geçmek boşuna kotayı
    /// ikiye katlar.</summary>
    internal static bool IsModelConfigError(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

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
