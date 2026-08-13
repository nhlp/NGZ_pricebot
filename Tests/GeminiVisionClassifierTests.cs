using System.Net;
using System.Text.Json;
using PriceBotPipeline;
using Xunit;

/// <summary>GeminiVisionClassifier'ın saf/network'süz yardımcıları test edilir — gerçek ağ isteği
/// atan ClassifyBrandAsync/ClassifyCodeAsync/SendClassifyRequestAsync otomatik testlerle
/// doğrulanmıyor (canlı key gerektirir, bkz. proje hafızası "Dev makine ≠ production"). Bunun
/// yerine 2026-08-10'da BuildRequest/ExtractLabel/IsModelConfigError SkiaSharp'a bağımlı olmayan
/// GeminiVisionClassifierLabelResolver.cs'e taşındı (bkz. o dosyanın başlığı) — bu da "kullandığımız
/// API sürümünün" (appsettings'teki model adları ile konuşulan gerçek v1beta generateContent
/// isteği/yanıtı) sözleşmesini, ağa hiç çıkmadan, canlı testte gözlemlenen gerçek yanıt şekilleriyle
/// (aynı gün 5/5 gerçek görselle doğrulanan Gonderim_20260810_164321_af706cab vakası) kilitler.
/// BrandMatcherTests.cs'teki gibi gerçek Nebim verisine benzer desenler (mükerrer satır, Türkçe
/// karakter) kullanılır.</summary>
public class GeminiVisionClassifierTests
{
    private static readonly List<BrandMultiplier> Brands =
    [
        new("LILA",  "LİLAX",         1149.5375m),
        new("PEPE",  "PEPE",          124.4865m),
        new("HIPP",  "HİPPIL",        125.608m),
        new("HIPP",  "HİPPIL",        125.608m),  // view'de gerçekten mükerrer (BrandMatcherTests ile aynı desen)
        new("FLAM",  "BABY FLAMİNDO", 117.7575m),
    ];

    [Fact]
    public void BuildDistinctCandidateNames_MukerrerSatirlariTekillestirir()
    {
        var names = GeminiVisionClassifier.BuildDistinctCandidateNames(Brands);

        Assert.Equal(4, names.Count); // HİPPIL bir kez
        Assert.Contains("HİPPIL", names);
        Assert.Contains("LİLAX", names);
        Assert.Contains("PEPE", names);
        Assert.Contains("BABY FLAMİNDO", names);
    }

    [Fact]
    public void BuildDistinctCandidateNames_BosListeBosDoner()
    {
        Assert.Empty(GeminiVisionClassifier.BuildDistinctCandidateNames([]));
    }

    [Fact]
    public void BuildDistinctCandidateNames_BosAdliSatirlarElenir()
    {
        var withBlank = new List<BrandMultiplier>(Brands) { new("XX", "", 100m), new("YY", "   ", 100m) };
        var names = GeminiVisionClassifier.BuildDistinctCandidateNames(withBlank);

        Assert.Equal(4, names.Count);
    }

    [Fact]
    public void ResolveLabelToBrand_BirebirEslesenIsimBulunur()
    {
        var brand = GeminiVisionClassifier.ResolveLabelToBrand("LİLAX", Brands);

        Assert.NotNull(brand);
        Assert.Equal("LILA", brand!.OnEk);
    }

    [Fact]
    public void ResolveLabelToBrand_MukerrerSatirdaIlkiniDoner()
    {
        // Enum zorlaması sayesinde her zaman TEK bir isim döner; aynı isimde birden fazla
        // satır varsa (HIPP, HIPP) hangisinin döndüğü önemsiz çünkü NetCarpan'ları aynı
        // (bkz. BrandMatcher.cs'teki aynı varsayım) — burada sadece ilkinin döndüğü doğrulanır.
        var brand = GeminiVisionClassifier.ResolveLabelToBrand("HİPPIL", Brands);

        Assert.NotNull(brand);
        Assert.Equal(125.608m, brand!.NetCarpan);
    }

    [Theory]
    [InlineData("BULUNAMADI")]
    [InlineData("LISTEDE OLMAYAN MARKA")]
    [InlineData("")]
    public void ResolveLabelToBrand_EslesmeyenEtiketNullDoner(string label)
    {
        Assert.Null(GeminiVisionClassifier.ResolveLabelToBrand(label, Brands));
    }

    [Fact]
    public void ResolveLabelToBrand_BuyukKucukHarfDuyarli()
    {
        // Enum'dan dönen değer birebir listedeki string olmalı (Gemini enum zorlaması bunu
        // garanti eder) — bu yüzden eşleştirme kasıtlı olarak case-insensitive DEĞİL
        // (MatchFromUserText'in aksine, o kullanıcı serbest metnini normalize ediyor).
        Assert.Null(GeminiVisionClassifier.ResolveLabelToBrand("lilax", Brands));
    }
}

/// <summary>Yedek-model zincirinin karar mantığı: SADECE HTTP 400/404 <see cref="IsModelConfigError"/>
/// sayılır (bkz. CLAUDE.md "YEDEK MODEL ZİNCİRİ" — 2026-08-10 canlı testte `gemini-2.5-flash` sabit
/// adının gerçekten 404 verdiği doğrulandı). 429 burada hâlâ false — ama 2026-08-13'ten beri bu
/// "yedek denenmez" anlamına GELMİYOR, kendi ayrı <see cref="IsQuotaError"/> yolundan (aşağıda)
/// yedek deniyor (bkz. GeminiVisionClassifier.cs "429 (KOTA) ARTIK..." notu, gerçek vaka: NGZ/
/// MİNİCE `limit: 5`). Sadece 5xx hâlâ "ne config-hatası ne kota, kendi IsTransientError yolundan
/// sabit bekleme ile tekrar dene" kategorisinde.</summary>
public class GeminiVisionClassifierIsModelConfigErrorTests
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
        Assert.Equal(expected, GeminiVisionClassifier.IsModelConfigError(status));
    }
}

/// <summary>GERÇEK VAKA (2026-08-13, NGZ "NET MEVSİMLİK FİYAT LİSTESİ 2026.xls" / MİNİCE): 38
/// görsellik bir klasörde OCR'ın marka-önekli-kod bug'ı yüzünden (bkz. AddSpacedSuffixAliases,
/// ExcelPriceReader.cs) ~32 görsel Gemini kod tespitine düştü; 6. istekten sonra Gemini
/// `generate_content_free_tier_requests` metriğinde `limit: 5` ile 429 döndü, devre kesici (Worker.cs
/// geminiCodeApiHealthy) TEK bu 429'da tetiklenip kalan ~31 görsel için Gemini'yi hiç denemeden
/// atlattı. Çözüm: 429 artık kendi ayrı <see cref="GeminiVisionClassifier.IsQuotaError"/> kategorisi
/// — <see cref="GeminiVisionClassifier.IsModelConfigError"/>'dan (400/404) ayrı tutulur.</summary>
public class GeminiVisionClassifierIsQuotaErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void SadeceHttp429KotaSayilir(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, GeminiVisionClassifier.IsQuotaError(status));
    }
}

/// <summary>ParseRetryDelay: Google'ın 429 yanıtından önerilen bekleme süresini çıkarır — iki
/// kaynak da gerçek Gemini yanıtlarında görülen biçimler (2026-08-13, NGZ/MİNİCE canlı log'u:
/// "Please retry in 6.27678624s." insan-okunur metni). Yapılandırılmış google.rpc.RetryInfo
/// (error.details[].retryDelay) önceliklidir, metin ikinci sıradadır.</summary>
public class GeminiVisionClassifierParseRetryDelayTests
{
    [Fact]
    public void YapilandirilmisRetryInfoAlaniOncelikli()
    {
        const string json = """
            {
              "error": {
                "code": 429,
                "details": [
                  { "@type": "type.googleapis.com/google.rpc.QuotaFailure" },
                  { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "6.276786240s" }
                ]
              }
            }
            """;

        var delay = GeminiVisionClassifier.ParseRetryDelay(json);

        Assert.NotNull(delay);
        Assert.Equal(6.28, delay!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void YapilandirilmisAlanYoksaMesajMetniDenenir()
    {
        // Gerçek NGZ/MİNİCE log'unda görülen tam metin (2026-08-13).
        const string json = """
            {
              "error": {
                "code": 429,
                "message": "You exceeded your current quota... Please retry in 6.27678624s.",
                "status": "RESOURCE_EXHAUSTED"
              }
            }
            """;

        var delay = GeminiVisionClassifier.ParseRetryDelay(json);

        Assert.NotNull(delay);
        Assert.Equal(6.28, delay!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void NeYapilandirilmisAlanNeMetinVarsaNullDoner()
    {
        const string json = """{ "error": { "code": 429, "message": "quota exceeded" } }""";

        Assert.Null(GeminiVisionClassifier.ParseRetryDelay(json));
    }

    [Fact]
    public void BozukJsonMetinYoluylaYineDeCozulur()
    {
        // JSON parse başarısız olsa bile ("bozuk gövde"), insan-okunur metin fallback'i çalışmalı.
        var delay = GeminiVisionClassifier.ParseRetryDelay("{ bozuk json ama retry in 3.5s yaziyor");

        Assert.NotNull(delay);
        Assert.Equal(3.5, delay!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void BosVeyaNullMetinNullDoner()
    {
        Assert.Null(GeminiVisionClassifier.ParseRetryDelay(null));
        Assert.Null(GeminiVisionClassifier.ParseRetryDelay(""));
    }

    [Fact]
    public void CokKisaOnerilenSureAsgariSinirinAltinaDusmez()
    {
        const string json = """{ "error": { "message": "Please retry in 0.05s." } }""";

        var delay = GeminiVisionClassifier.ParseRetryDelay(json);

        Assert.NotNull(delay);
        Assert.True(delay!.Value.TotalSeconds >= 1.0, "Aşırı kısa bir öneri (ör. RPM penceresinin son anı) hemen art arda ikinci bir 429'a yol açabilir.");
    }

    [Fact]
    public void CokUzunOnerilenSureUstSinirinUstuneCikmaz()
    {
        // Ana 10 sn'lik tarama döngüsünü bloklayan senkron bir bekleme olduğu için (bkz.
        // ParseRetryDelay dosya-içi yorumu) çok uzun bir öneriye (ör. günlük kota) körü körüne
        // uyulmamalı.
        const string json = """{ "error": { "message": "Please retry in 3600s." } }""";

        var delay = GeminiVisionClassifier.ParseRetryDelay(json);

        Assert.NotNull(delay);
        Assert.True(delay!.Value.TotalSeconds <= 30.0);
    }
}

/// <summary>BuildRequest, Gemini'nin gerçek v1beta generateContent + responseSchema/enum
/// sözleşmesini kurar (appsettings'teki model adıyla konuşulan API sürümü budur — bkz.
/// GeminiVisionClassifier.cs dosya başı "HALÜSİNASYON ENGELLEME" notu). Testler, Google'ın enum
/// zorlamasının fiilen kurulduğunu ve BULUNAMADI'nın her zaman ek bir seçenek olarak eklendiğini
/// serileştirilmiş JSON üzerinden doğrular — canlı isteğe hiç çıkmadan.</summary>
public class GeminiVisionClassifierBuildRequestTests
{
    [Fact]
    public void EnumDegerleri_AdaylarArtiBulunamadiIcerir()
    {
        var request = GeminiVisionClassifier.BuildRequest(
            imageParts: [new GeminiPart(Text: null, InlineData: new GeminiInlineData("image/jpeg", "AAAA"))],
            candidateLabels: ["1270", "1282", "1291"],
            userPrompt: "kullanıcı istemi",
            systemPrompt: "sistem istemi");

        var enumValues = request.GenerationConfig.ResponseSchema.Properties!["value"].Enum!;
        Assert.Equal(["1270", "1282", "1291", "BULUNAMADI"], enumValues);
    }

    [Fact]
    public void SistemVeKullaniciIstemleriDogruAlanlaraYerlesir()
    {
        var request = GeminiVisionClassifier.BuildRequest(
            imageParts: [],
            candidateLabels: ["1291"],
            userPrompt: "kullanıcı istemi",
            systemPrompt: "sistem istemi");

        Assert.Equal("sistem istemi", request.SystemInstruction.Parts[0].Text);
        Assert.Equal("user", request.Contents[0].Role);
        // Görsel parçası yoksa tek parça, prompt metni — sona eklenir (bkz. BuildRequest).
        Assert.Equal("kullanıcı istemi", Assert.Single(request.Contents[0].Parts).Text);
    }

    [Fact]
    public void GorselParcalari_KullaniciPromptundanOnceKorunur()
    {
        var img1 = new GeminiPart(Text: null, InlineData: new GeminiInlineData("image/jpeg", "AAA"));
        var img2 = new GeminiPart(Text: null, InlineData: new GeminiInlineData("image/jpeg", "BBB"));

        var request = GeminiVisionClassifier.BuildRequest(
            imageParts: [img1, img2],
            candidateLabels: ["1291"],
            userPrompt: "prompt",
            systemPrompt: "sistem");

        var parts = request.Contents[0].Parts;
        Assert.Equal(3, parts.Count);
        Assert.Same(img1, parts[0]);
        Assert.Same(img2, parts[1]);
        Assert.Equal("prompt", parts[2].Text);
    }

    [Fact]
    public void SonucAlaniZorunluTekAlandirVeStringTipindedir()
    {
        var request = GeminiVisionClassifier.BuildRequest([], ["X"], "u", "s");
        var schema = request.GenerationConfig.ResponseSchema;

        Assert.Equal(["value"], schema.Required);
        Assert.Equal("STRING", schema.Properties!["value"].Type);
        Assert.Equal("application/json", request.GenerationConfig.ResponseMimeType);
    }

    [Fact]
    public void GercekApiSemasiylaSerilestirilebilir()
    {
        // BuildRequest'in çıktısı, ClassifyLabelAsync'in gerçekten Gemini'ye gönderdiği
        // JsonSerializerContext (GeminiJsonContext) ile serileştirilebilmeli — kaynak-üretimli
        // (source-generated) serileştiricinin tip grafiğinde bir alan eksikse derleme zamanında
        // değil, çalışma zamanında patlar; bu test onu erken yakalar.
        var request = GeminiVisionClassifier.BuildRequest(
            [new GeminiPart(Text: null, InlineData: new GeminiInlineData("image/jpeg", "AAAA"))],
            ["1291"], "u", "s");

        var json = JsonSerializer.Serialize(request, GeminiJsonContext.Default.GeminiRequest);

        Assert.Contains("\"systemInstruction\"", json);
        Assert.Contains("\"responseSchema\"", json);
        Assert.Contains("\"1291\"", json);
        Assert.Contains("\"BULUNAMADI\"", json);
    }
}

/// <summary>ExtractLabel, gerçek Gemini v1beta yanıt şeklini ayrıştırır — vakalar 2026-08-10 canlı
/// testinde (bkz. CLAUDE.md "Kod tespiti canlı doğrulandı") gözlemlenen gerçek durumları (başarılı
/// ayrıştırma, engellenmiş/kesilmiş yanıt, kota/model hatası sonrası boş gövde) yansıtır.</summary>
public class GeminiVisionClassifierExtractLabelTests
{
    private static string SuccessResponse(string value) => $$"""
        {
          "candidates": [
            {
              "content": { "role": "model", "parts": [ { "text": "{\"value\": \"{{value}}\"}" } ] },
              "finishReason": "STOP"
            }
          ]
        }
        """;

    [Fact]
    public void BasariliYanittaEtiketCikarilir()
    {
        var label = GeminiVisionClassifier.ExtractLabel(SuccessResponse("1291"), out var blockReason);

        Assert.Equal("1291", label);
        Assert.Null(blockReason);
    }

    [Fact]
    public void BasariliYanittaBulunamadiEtiketiDeAynenCikarilir()
    {
        var label = GeminiVisionClassifier.ExtractLabel(SuccessResponse("BULUNAMADI"), out var blockReason);

        Assert.Equal("BULUNAMADI", label);
        Assert.Null(blockReason);
    }

    [Fact]
    public void PromptFeedbackBlockReasonVarsaEtiketYeriniSebepAlir()
    {
        const string json = """
            {
              "promptFeedback": { "blockReason": "SAFETY" },
              "candidates": []
            }
            """;

        var label = GeminiVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("SAFETY", blockReason);
    }

    [Fact]
    public void AdaySayisiSifirsaNoCandidatesDoner()
    {
        const string json = """{ "candidates": [] }""";

        var label = GeminiVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("NO_CANDIDATES", blockReason);
    }

    [Fact]
    public void FinishReasonStopDegilseOSebepDoner()
    {
        // Canlı testte gözlemlenen gerçek vaka (2026-08-10): maxOutputTokens çok düşükken
        // "thinking" tokenleri asıl JSON çıktısına hiç sıra bırakmadan MAX_TOKENS ile kesiyordu.
        const string json = """
            {
              "candidates": [
                { "content": { "role": "model", "parts": [] }, "finishReason": "MAX_TOKENS" }
              ]
            }
            """;

        var label = GeminiVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("MAX_TOKENS", blockReason);
    }

    [Fact]
    public void BozukJsonParseErrorDoner()
    {
        var label = GeminiVisionClassifier.ExtractLabel("{ bozuk json", out var blockReason);

        Assert.Null(label);
        Assert.Equal("PARSE_ERROR", blockReason);
    }

    [Fact]
    public void IcTextGecerliJsonDegilseParseErrorDoner()
    {
        const string json = """
            {
              "candidates": [
                { "content": { "role": "model", "parts": [ { "text": "bu json degil" } ] }, "finishReason": "STOP" }
              ]
            }
            """;

        var label = GeminiVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Equal("PARSE_ERROR", blockReason);
    }

    [Fact]
    public void IcTextDeValueAlaniYoksaNullDonerAmaBlockReasonSetlenmez()
    {
        const string json = """
            {
              "candidates": [
                { "content": { "role": "model", "parts": [ { "text": "{\"baska_alan\": \"x\"}" } ] }, "finishReason": "STOP" }
              ]
            }
            """;

        var label = GeminiVisionClassifier.ExtractLabel(json, out var blockReason);

        Assert.Null(label);
        Assert.Null(blockReason);
    }
}
