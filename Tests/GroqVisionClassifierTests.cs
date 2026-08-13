using System.Net;
using System.Text.Json;
using PriceBotPipeline;
using Xunit;

/// <summary>GroqVisionClassifier'ın saf/network'süz yardımcıları test edilir — Gemini/Anthropic
/// test dosyalarıyla AYNI desen/gerekçe. Vakalar Groq'un gerçek OpenAI-uyumlu chat/completions +
/// zorunlu tool_choice sözleşmesini, ağa hiç çıkmadan kilitler (2026-08-13 canlı doğrulandı — bkz.
/// GroqVisionClassifier.cs dosya başı yorumu).</summary>
public class GroqVisionClassifierIsModelConfigErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void KaliciKonfigurasyonHatalariDogruSiniflandirilir(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, GroqVisionClassifier.IsModelConfigError(status));
    }
}

public class GroqVisionClassifierIsQuotaErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void SadeceHttp429KotaSayilir(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, GroqVisionClassifier.IsQuotaError(status));
    }
}

public class GroqVisionClassifierIsTransientErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void SadeceSunucuTarafiHatalariGeciciSayilir(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, GroqVisionClassifier.IsTransientError(status));
    }
}

/// <summary>2026-08-13 canlı testte gözlenen ikinci gerçek Groq hata modu: model zorunlu araç
/// çağrısını doğru biçimlendiremiyor (HTTP 400, "code":"tool_use_failed") — bu, IsModelConfigError
/// (kalıcı, yanlış model adı) İLE AYNI HTTP durumunu paylaşsa da GEÇİCİ/görsele-özgü sayılmalı,
/// aksi halde tek bir görseldeki üretim hatası tüm klasör için Groq'u kapatıyordu (canlıda
/// gözlendi: ilk görselde bu hatayı aldı, devre kesici tetiklendi, kalan 6 görsel hiç denenmedi).</summary>
public class GroqVisionClassifierIsToolUseFailureTests
{
    [Fact]
    public void GercekToolUseFailedYanitiTespitEdilir()
    {
        const string body = """
            {"error":{"message":"Failed to call a function. Please adjust your prompt. See 'failed_generation' for more details.","type":"invalid_request_error","code":"tool_use_failed","failed_generation":""}}
            """;

        Assert.True(GroqVisionClassifier.IsToolUseFailure(body));
    }

    [Theory]
    [InlineData("""{"error":{"message":"model not found","code":"model_not_found"}}""")]
    [InlineData("""{"error":{"message":"bad request"}}""")] // code alanı hiç yok
    [InlineData("bozuk json")]
    [InlineData(null)]
    [InlineData("")]
    public void BaskaHatalarToolUseFailureSayilmaz(string? body)
    {
        Assert.False(GroqVisionClassifier.IsToolUseFailure(body));
    }
}

/// <summary>2026-08-13 canlı testte gözlenen gerçek Groq TPM 429 mesaj metniyle doğrulanır:
/// "Rate limit reached ... Please try again in 22.3875s.".</summary>
public class GroqVisionClassifierParseRetryDelayTests
{
    [Fact]
    public void GercekGroqMesajMetniDogruAyristirilir()
    {
        const string body = """
            {"error":{"message":"Rate limit reached for model `qwen/qwen3.6-27b` in organization `org_x` service tier `on_demand` on tokens per minute (TPM): Limit 8000, Used 6403, Requested 4582. Please try again in 22.3875s. Need more tokens? Upgrade to Dev Tier today at https://console.groq.com/settings/billing","type":"tokens","code":"rate_limit_exceeded"}}
            """;

        var delay = GroqVisionClassifier.ParseRetryDelay(body);

        Assert.NotNull(delay);
        Assert.Equal(22.39, delay!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void MetinYoksaNullDoner()
    {
        Assert.Null(GroqVisionClassifier.ParseRetryDelay("""{"error":{"message":"quota exceeded"}}"""));
    }
}

/// <summary>BuildRequest, Groq'un OpenAI-uyumlu function-calling sözleşmesini kurar —
/// GeminiVisionClassifierBuildRequestTests/AnthropicVisionClassifierBuildRequestTests ile aynı
/// gerekçe/desen.</summary>
public class GroqVisionClassifierBuildRequestTests
{
    [Fact]
    public void EnumDegerleri_AdaylarArtiBulunamadiIcerir()
    {
        var request = GroqVisionClassifier.BuildRequest(
            "qwen/qwen3.6-27b", 512, "sistem", "kullanıcı",
            "AAAA", "image/jpeg", ["1270", "1282", "1291"]);

        var enumValues = request.Tools[0].Function.Parameters.Properties["value"].Enum;
        Assert.Equal(["1270", "1282", "1291", "BULUNAMADI"], enumValues);
    }

    [Fact]
    public void ToolChoiceZorunluFonksiyonuIsaretEder()
    {
        var request = GroqVisionClassifier.BuildRequest(
            "model", 512, "s", "u", "AAAA", "image/jpeg", ["1291"]);

        Assert.Equal("function", request.ToolChoice.Type);
        Assert.Equal(request.Tools[0].Function.Name, request.ToolChoice.Function.Name);
    }

    [Fact]
    public void SistemMesajiTekMetinParcasiOlarakGelir()
    {
        var request = GroqVisionClassifier.BuildRequest(
            "model", 512, "sistem istemi", "kullanıcı", "AAAA", "image/jpeg", ["1291"]);

        var systemMsg = request.Messages[0];
        Assert.Equal("system", systemMsg.Role);
        Assert.Equal("sistem istemi", Assert.Single(systemMsg.Content).Text);
    }

    [Fact]
    public void KullaniciMesajiMetinVeGorselParcasiIcerir()
    {
        var request = GroqVisionClassifier.BuildRequest(
            "model", 512, "s", "kullanıcı istemi", "BASE64VERI", "image/jpeg", ["1291"]);

        var userMsg = request.Messages[1];
        Assert.Equal("user", userMsg.Role);
        Assert.Equal(2, userMsg.Content.Count);
        Assert.Equal("text", userMsg.Content[0].Type);
        Assert.Equal("kullanıcı istemi", userMsg.Content[0].Text);
        Assert.Equal("image_url", userMsg.Content[1].Type);
        Assert.Equal("data:image/jpeg;base64,BASE64VERI", userMsg.Content[1].ImageUrl!.Url);
    }

    [Fact]
    public void GercekApiSemasiylaSerilestirilebilir()
    {
        var request = GroqVisionClassifier.BuildRequest(
            "qwen/qwen3.6-27b", 512, "s", "u", "AAAA", "image/jpeg", ["1291"]);

        var json = JsonSerializer.Serialize(request, GroqJsonContext.Default.GroqRequest);

        Assert.Contains("\"tool_choice\"", json);
        Assert.Contains("\"parameters\"", json);
        Assert.Contains("\"1291\"", json);
        Assert.Contains("\"BULUNAMADI\"", json);
        Assert.Contains("data:image/jpeg;base64,AAAA", json);
    }
}

/// <summary>ExtractLabel, gerçek Groq (OpenAI-uyumlu) chat/completions yanıt şeklini ayrıştırır —
/// GeminiVisionClassifierExtractLabelTests/AnthropicVisionClassifierExtractLabelTests ile aynı
/// sözleşme/desen. En önemli fark: function.arguments iç içe bir OBJE DEĞİL, JSON-KODLANMIŞ bir
/// STRING'dir (OpenAI function-calling sözleşmesi) — bu yüzden iki aşamalı parse.</summary>
public class GroqVisionClassifierExtractLabelTests
{
    private static string ToolCallResponse(string value) => $$"""
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": null,
                "tool_calls": [
                  { "id": "call_1", "type": "function", "function": { "name": "report_code", "arguments": "{\"value\": \"{{value}}\"}" } }
                ]
              },
              "finish_reason": "tool_calls"
            }
          ]
        }
        """;

    [Fact]
    public void BasariliYanittaEtiketCikarilir()
    {
        var label = GroqVisionClassifier.ExtractLabel(ToolCallResponse("1291"), out var blockReason);

        Assert.Equal("1291", label);
        Assert.Null(blockReason);
    }

    [Fact]
    public void BasariliYanittaBulunamadiEtiketiDeAynenCikarilir()
    {
        var label = GroqVisionClassifier.ExtractLabel(ToolCallResponse("BULUNAMADI"), out var blockReason);

        Assert.Equal("BULUNAMADI", label);
        Assert.Null(blockReason);
    }

    [Fact]
    public void HataYanitindaEtiketYeriniHataTuruAlir()
    {
        const string json = """
            { "error": { "message": "Rate limit reached", "type": "rate_limit_exceeded", "code": "rate_limit_exceeded" } }
            """;

        var label = GroqVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("rate_limit_exceeded", blockReason);
    }

    [Fact]
    public void ToolCallsYoksaFinishReasonSebepOlarakDoner()
    {
        const string json = """
            {
              "choices": [
                { "message": { "role": "assistant", "content": "serbest metin cevap" }, "finish_reason": "stop" }
              ]
            }
            """;

        var label = GroqVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("stop", blockReason);
    }

    [Fact]
    public void FarkliFonksiyonAdiylaGelenToolCallYokSayilir()
    {
        const string json = """
            {
              "choices": [
                {
                  "message": { "tool_calls": [ { "function": { "name": "baska_fonksiyon", "arguments": "{\"value\": \"1291\"}" } } ] },
                  "finish_reason": "tool_calls"
                }
              ]
            }
            """;

        var label = GroqVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("NO_MATCHING_TOOL_CALL", blockReason);
    }

    [Fact]
    public void BosChoicesDizisiNoChoicesDoner()
    {
        const string json = """{ "choices": [] }""";

        var label = GroqVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("NO_CHOICES", blockReason);
    }

    [Fact]
    public void BozukJsonParseErrorDoner()
    {
        var label = GroqVisionClassifier.ExtractLabel("{ bozuk json", out var blockReason);

        Assert.Null(label);
        Assert.Equal("PARSE_ERROR", blockReason);
    }

    [Fact]
    public void ArgumentsIcindekiBozukJsonDaParseErrorDoner()
    {
        // arguments'ın kendisi (iç içe JSON string) bozuksa da güvenli şekilde ele alınmalı.
        const string json = """
            {
              "choices": [
                {
                  "message": { "tool_calls": [ { "function": { "name": "report_code", "arguments": "{ bozuk" } } ] },
                  "finish_reason": "tool_calls"
                }
              ]
            }
            """;

        var label = GroqVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("PARSE_ERROR", blockReason);
    }
}
