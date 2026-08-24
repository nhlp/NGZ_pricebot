using Microsoft.Extensions.Logging;
using PriceBotPipeline;

// 13:04:22 üretim log'unda görülen "Gemini HTTP 400 (INVALID_ARGUMENT)" hatasını (hem birincil hem
// yedek modelde, aynı görsel için, marka tespiti sırasında) yeniden üretme/teşhis script'i
// (2026-08-24, BrandSearchProbe/NebimConnectionProbe ile aynı desen — bağlantı dizesi/API key'ler
// KOMUT SATIRINA YAZILMAZ, appsettings.json'dan okunur, gerçek Worker akışıyla AYNI kaynak).
// Worker.cs'e/gerçek Incoming klasörüne HİÇBİR ŞEY YAZMAZ, sadece okur + görü sağlayıcılarına istek
// atar. Sonuç (bulgular GeminiVisionClassifier.cs'teki MaxBrandCandidatesForSchema dokümantasyonuna
// işlendi): Gemini'nin responseSchema.enum'ı GERÇEK 322 markalık listede ~310-314 arasında bir yerde
// sert bir üst sınıra çarpıyor (310 başarılı, 315+ hep 400) — Groq VE Claude AYNI tam liste + AYNI
// görselle sorunsuz doğru markayı (MİNİ PAKEL) buluyor, yani sorun sadece Gemini'nin şemasında.
//
//   dotnet run --project Spike\GeminiBrandProbe\geminibrandprobe.csproj -- <görsel-yolu>

Console.OutputEncoding = System.Text.Encoding.UTF8;

var imagePath = args.Length > 0
    ? args[0]
    : @"C:\Users\glnhl\OneDrive\Masaüstü\NGZ\2408\905462547278\Gonderim_20260824_104609_79516562\gorsel_20260824_104609064_26899b.jpg";

if (!File.Exists(imagePath))
{
    Console.WriteLine($"HATA: görsel bulunamadı: {imagePath}");
    return 1;
}

var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "appsettings.json");
appsettingsPath = Path.GetFullPath(appsettingsPath);
if (!File.Exists(appsettingsPath))
{
    Console.WriteLine($"HATA: appsettings.json bulunamadı: {appsettingsPath}");
    return 1;
}

var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(appsettingsPath))!;
var nebimConn = config["ConnectionStrings"]?["Nebim"]?.GetValue<string>() ?? "";
var geminiKey = config["GeminiApiKey"]?.GetValue<string>() ?? "";
var geminiModel = config["GeminiBrandModel"]?.GetValue<string>() ?? "gemini-flash-latest";
var geminiFallback = config["GeminiBrandModelFallback"]?.GetValue<string>() ?? "gemini-flash-lite-latest";
var groqKey = config["GroqApiKey"]?.GetValue<string>() ?? "";
var groqModel = config["GroqModel"]?.GetValue<string>() ?? "qwen/qwen3.6-27b";
var anthropicKey = config["AnthropicApiKey"]?.GetValue<string>() ?? "";
var anthropicModel = config["AnthropicModel"]?.GetValue<string>() ?? "claude-haiku-4-5-20251001";

if (string.IsNullOrWhiteSpace(nebimConn) || string.IsNullOrWhiteSpace(geminiKey))
{
    Console.WriteLine("HATA: appsettings.json'da ConnectionStrings:Nebim / GeminiApiKey eksik.");
    return 1;
}

ILogger logger = new ConsoleLogger();

Console.WriteLine("Nebim'den gerçek marka listesi çekiliyor...");
var brandLoad = await new NebimBrandProvider(nebimConn, logger).GetBrandMultipliersAsync();
var brandList = brandLoad.Brands;
Console.WriteLine($"{brandList.Count} geçerli marka yüklendi ({brandLoad.Excluded.Count} NetCarpan<=0 elendi).\n");

async Task RunVariant(string title, IBrandClassifier classifier, IReadOnlyList<BrandMultiplier> candidates, string? ocrHint)
{
    Console.WriteLine($"=== {title} (aday sayısı={candidates.Count}, ocrHint={(ocrHint is null ? "(yok)" : $"'{ocrHint}'")}) ===");
    var (brand, rawLabel, apiFailed) = await classifier.ClassifyBrandAsync([imagePath], candidates, ocrHint, CancellationToken.None);
    Console.WriteLine($"  Sonuç: Brand={(brand is null ? "(null)" : brand.FullName)} RawLabel={rawLabel ?? "(null)"} ApiFailed={apiFailed}\n");
}

var geminiClassifier = new GeminiVisionClassifier(geminiKey, geminiModel, geminiFallback, logger);
var groqClassifier = new GroqVisionClassifier(groqKey, groqModel, logger);
var anthropicClassifier = new AnthropicVisionClassifier(anthropicKey, anthropicModel, logger);

Console.WriteLine("\n########## GROQ — tam liste (322), ocrHint yok ##########");
await RunVariant("Groq tam liste", groqClassifier, brandList, null);

Console.WriteLine("\n########## CLAUDE — tam liste (322), ocrHint yok ##########");
await RunVariant("Claude tam liste", anthropicClassifier, brandList, null);

Console.WriteLine("\n########## GEMINI — boyut bisection ##########");
// 322 (tam liste) 400 veriyor, 50 vermiyordu (bkz. önceki A/C koşusu) — eşiği kabaca bulmak için
// birkaç ara boyut. Aynı görsel + prompt, sadece aday sayısı değişiyor.
foreach (var n in new[] { 305, 310, 315, 320 })
{
    var subset = brandList.Take(n).ToList();
    await RunVariant($"Gemini boyut={n}", geminiClassifier, subset, null);
}

return 0;

sealed class ConsoleLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Console.WriteLine($"  [{logLevel}] {formatter(state, exception)}");
        if (exception is not null) Console.WriteLine($"    {exception.GetType().Name}: {exception.Message}");
    }
}
