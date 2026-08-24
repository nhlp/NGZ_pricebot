using System.Net;
using System.Text.Json;
using PriceBotPipeline;
using Xunit;

/// <summary>AnthropicVisionClassifier'ın saf/network'süz yardımcıları test edilir —
/// GeminiVisionClassifierTests.cs ile AYNI desen/gerekçe (gerçek ağ isteği atan ClassifyCodeAsync
/// canlı key gerektirir, bkz. proje hafızası "Dev makine ≠ production"). Vakalar Anthropic'in
/// gerçek Messages API (v1/messages, zorunlu tool_choice) sözleşmesini, ağa hiç çıkmadan kilitler.</summary>
public class AnthropicVisionClassifierIsModelConfigErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void KaliciKonfigurasyonHatalariDogruSiniflandirilir(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, AnthropicVisionClassifier.IsModelConfigError(status));
    }
}

public class AnthropicVisionClassifierIsQuotaErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void SadeceHttp429KotaSayilir(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, AnthropicVisionClassifier.IsQuotaError(status));
    }
}

public class AnthropicVisionClassifierIsTransientErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData((HttpStatusCode)529, true)] // Anthropic overloaded_error — 5xx aralığına girer, özel kontrol gerekmez
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void SadeceSunucuTarafiHatalariGeciciSayilir(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, AnthropicVisionClassifier.IsTransientError(status));
    }
}

/// <summary>BuildRequest, Claude'un gerçek v1/messages + zorunlu tool_choice sözleşmesini kurar —
/// GeminiVisionClassifierBuildRequestTests ile aynı gerekçe/desen.</summary>
public class AnthropicVisionClassifierBuildRequestTests
{
    [Fact]
    public void EnumDegerleri_AdaylarArtiBulunamadiIcerir()
    {
        var request = AnthropicVisionClassifier.BuildRequest(
            "claude-haiku-4-5-20251001", 512, "sistem", "kullanıcı",
            "AAAA", "image/jpeg", ["1270", "1282", "1291"], "report_code", "kod bildir");

        var enumValues = request.Tools[0].InputSchema.Properties["value"].Enum;
        Assert.Equal(["1270", "1282", "1291", "BULUNAMADI"], enumValues);
    }

    [Fact]
    public void ToolChoiceZorunluAraciIsaretEder()
    {
        var request = AnthropicVisionClassifier.BuildRequest(
            "model", 512, "s", "u", "AAAA", "image/jpeg", ["1291"], "report_code", "kod bildir");

        Assert.Equal("tool", request.ToolChoice.Type);
        Assert.Equal(request.Tools[0].Name, request.ToolChoice.Name);
    }

    /// <summary>2026-08-24: BuildRequest artık toolName/toolDescription parametre alıyor (kod ve
    /// marka tespiti AYRI araç adları kullanıyor) — GroqVisionClassifierBuildRequestTests'teki
    /// AYNI kilit.</summary>
    [Fact]
    public void OzelAracAdiToolsVeToolChoiceYaYansir()
    {
        var request = AnthropicVisionClassifier.BuildRequest(
            "model", 512, "s", "u", "AAAA", "image/jpeg", ["MARKA"], "report_brand", "marka bildir");

        Assert.Equal("report_brand", request.Tools[0].Name);
        Assert.Equal("marka bildir", request.Tools[0].Description);
        Assert.Equal("report_brand", request.ToolChoice.Name);
    }

    [Fact]
    public void GorselVeMetinDogruSiraylaMesajaYerlesir()
    {
        var request = AnthropicVisionClassifier.BuildRequest(
            "model", 512, "sistem", "kullanıcı metni", "BASE64VERI", "image/jpeg", ["1291"], "report_code", "kod bildir");

        Assert.Equal("sistem", request.System);
        var content = request.Messages[0].Content;
        Assert.Equal(2, content.Count);
        Assert.Equal("image", content[0].Type);
        Assert.Equal("BASE64VERI", content[0].Source!.Data);
        Assert.Equal("image/jpeg", content[0].Source!.MediaType);
        Assert.Equal("text", content[1].Type);
        Assert.Equal("kullanıcı metni", content[1].Text);
    }

    [Fact]
    public void GercekApiSemasiylaSerilestirilebilir()
    {
        // BuildRequest'in çıktısı, ClassifyCodeAsync'in gerçekten Claude'a gönderdiği
        // JsonSerializerContext (AnthropicJsonContext) ile serileştirilebilmeli.
        var request = AnthropicVisionClassifier.BuildRequest(
            "claude-haiku-4-5-20251001", 512, "s", "u", "AAAA", "image/jpeg", ["1291"], "report_code", "kod bildir");

        var json = JsonSerializer.Serialize(request, AnthropicJsonContext.Default.AnthropicRequest);

        Assert.Contains("\"tool_choice\"", json);
        Assert.Contains("\"input_schema\"", json);
        Assert.Contains("\"1291\"", json);
        Assert.Contains("\"BULUNAMADI\"", json);
        Assert.Contains("\"max_tokens\"", json);
    }

    /// <summary>GERÇEK VAKA (2026-08-13, canlı test — kullanıcının kendi Claude key'iyle): ilk
    /// gerçek istek HTTP 400 "messages.0.content.0.image.text: Extra inputs are not permitted"
    /// ile başarısız oldu. Sebep: System.Text.Json varsayılan olarak null alanları da serileştirir
    /// ("text": null), ama Claude'un content-block şeması "image" tipi bir blokta "text" alanının
    /// HİÇ bulunmasına (null bile olsa) izin vermiyor. Bu test, "image" bloğunun gövdesinde "text"
    /// anahtarının kesinlikle YER ALMADIĞINI (JsonIgnoreCondition.WhenWritingNull'ın gerçekten
    /// çalıştığını) serileştirilmiş JSON üzerinden doğrudan doğrular — bir önceki test
    /// (GercekApiSemasiylaSerilestirilebilir) sadece VAR OLMASI gerekenleri kontrol ediyordu, bu
    /// regresyonu YAKALAMAMIŞTI.</summary>
    [Fact]
    public void ImageBlogundaTextAlaniHicSerilestirilmez()
    {
        var request = AnthropicVisionClassifier.BuildRequest(
            "model", 512, "s", "u", "AAAA", "image/jpeg", ["1291"], "report_code", "kod bildir");

        var json = JsonSerializer.Serialize(request, AnthropicJsonContext.Default.AnthropicRequest);
        using var doc = JsonDocument.Parse(json);
        var imageBlock = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0];

        Assert.Equal("image", imageBlock.GetProperty("type").GetString());
        Assert.False(imageBlock.TryGetProperty("text", out _), "\"image\" tipi blokta \"text\" alanı hiç bulunmamalı (null olsa bile) — Claude bunu 400 ile reddediyor.");
    }

    /// <summary>Simetrik kontrol: "text" tipi blokta da "source" alanı hiç serileştirilmemeli.</summary>
    [Fact]
    public void TextBlogundaSourceAlaniHicSerilestirilmez()
    {
        var request = AnthropicVisionClassifier.BuildRequest(
            "model", 512, "s", "u", "AAAA", "image/jpeg", ["1291"], "report_code", "kod bildir");

        var json = JsonSerializer.Serialize(request, AnthropicJsonContext.Default.AnthropicRequest);
        using var doc = JsonDocument.Parse(json);
        var textBlock = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[1];

        Assert.Equal("text", textBlock.GetProperty("type").GetString());
        Assert.False(textBlock.TryGetProperty("source", out _), "\"text\" tipi blokta \"source\" alanı hiç bulunmamalı.");
    }
}

/// <summary>ExtractLabel, gerçek Claude Messages API yanıt şeklini (zorunlu tool_use bloğu)
/// ayrıştırır — GeminiVisionClassifierExtractLabelTests ile aynı sözleşme/desen.</summary>
public class AnthropicVisionClassifierExtractLabelTests
{
    private static string ToolUseResponse(string value) => $$"""
        {
          "id": "msg_01",
          "type": "message",
          "role": "assistant",
          "content": [
            { "type": "tool_use", "id": "tu_01", "name": "report_code", "input": { "value": "{{value}}" } }
          ],
          "stop_reason": "tool_use"
        }
        """;

    [Fact]
    public void BasariliYanittaEtiketCikarilir()
    {
        var label = AnthropicVisionClassifier.ExtractLabel(ToolUseResponse("1291"), "report_code", out var blockReason);

        Assert.Equal("1291", label);
        Assert.Null(blockReason);
    }

    [Fact]
    public void BasariliYanittaBulunamadiEtiketiDeAynenCikarilir()
    {
        var label = AnthropicVisionClassifier.ExtractLabel(ToolUseResponse("BULUNAMADI"), "report_code", out var blockReason);

        Assert.Equal("BULUNAMADI", label);
        Assert.Null(blockReason);
    }

    [Fact]
    public void HataYanitindaEtiketYeriniHataTuruAlir()
    {
        const string json = """
            {
              "type": "error",
              "error": { "type": "rate_limit_error", "message": "..." }
            }
            """;

        var label = AnthropicVisionClassifier.ExtractLabel(json, "report_code", out var blockReason);

        Assert.Null(label);
        Assert.Equal("rate_limit_error", blockReason);
    }

    [Fact]
    public void ToolUseBlokuYoksaStopReasonSebepOlarakDoner()
    {
        // Model, max_tokens'a takılıp aracı hiç çağırmadan bitirmiş olabilir.
        const string json = """
            {
              "content": [ { "type": "text", "text": "..." } ],
              "stop_reason": "max_tokens"
            }
            """;

        var label = AnthropicVisionClassifier.ExtractLabel(json, "report_code", out var blockReason);

        Assert.Null(label);
        Assert.Equal("max_tokens", blockReason);
    }

    [Fact]
    public void FarkliAracAdiylaGelenToolUseYokSayilir()
    {
        const string json = """
            {
              "content": [ { "type": "tool_use", "name": "baska_arac", "input": { "value": "1291" } } ],
              "stop_reason": "tool_use"
            }
            """;

        var label = AnthropicVisionClassifier.ExtractLabel(json, "report_code", out var blockReason);

        Assert.Null(label);
        Assert.Equal("tool_use", blockReason);
    }

    [Fact]
    public void BozukJsonParseErrorDoner()
    {
        var label = AnthropicVisionClassifier.ExtractLabel("{ bozuk json", "report_code", out var blockReason);

        Assert.Null(label);
        Assert.Equal("PARSE_ERROR", blockReason);
    }
}

/// <summary>GeminiVisionClassifierBuildBrandUserPromptTests ile AYNI desen/sözleşme.</summary>
public class AnthropicVisionClassifierBuildBrandUserPromptTests
{
    [Fact]
    public void IpucuYoksaPromptDegismez()
    {
        Assert.Equal("temel prompt", AnthropicVisionClassifier.BuildBrandUserPrompt("temel prompt", null));
    }

    [Fact]
    public void IpucuVarsaEkBaglamOlarakEklenir()
    {
        var result = AnthropicVisionClassifier.BuildBrandUserPrompt("temel prompt", "JOJOMINI");

        Assert.StartsWith("temel prompt", result);
        Assert.Contains("JOJOMINI", result);
    }
}
