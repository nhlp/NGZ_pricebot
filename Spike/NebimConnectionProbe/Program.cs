using Microsoft.Extensions.Logging.Abstractions;
using PriceBotPipeline;

// Eski (asistyazilim.pakabulut.com,9023 / AsistWP_V3) ile yeni (46.221.62.174,9034 / NGZTekstil)
// Nebim bağlantısının AS_PWB_MarkaCarpan (marka->NetCarpan) ve AllExchangeRates (USD kuru)
// view'lerinden GERÇEKTEN aynı şekilde veri çekilip çekilemediğini doğrulayan, iki bağlantıyı
// karşılaştıran tanı script'i (2026-08-20). Worker.cs'in kullandığı GERÇEK NebimBrandProvider/
// NebimRateProvider sınıflarını kopyalamadan doğrudan kullanır (BrandProbe ile aynı desen).
//
// Bağlantı dizeleri KOMUT SATIRINA/KAYNAĞA YAZILMAZ — appsettings.json'daki Nebim şifresi gibi
// bunlar da paylaşılmamalı; ortam değişkeninden okunur:
//   $env:NEBIM_OLD_CONN = "Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True;"
//   $env:NEBIM_NEW_CONN = "Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True;"
//   dotnet run --project Spike\NebimConnectionProbe\nebimconnectionprobe.csproj

var oldConn = Environment.GetEnvironmentVariable("NEBIM_OLD_CONN");
var newConn = Environment.GetEnvironmentVariable("NEBIM_NEW_CONN");

if (string.IsNullOrWhiteSpace(oldConn) || string.IsNullOrWhiteSpace(newConn))
{
    Console.WriteLine("HATA: NEBIM_OLD_CONN ve NEBIM_NEW_CONN ortam değişkenleri gerekli.");
    return 1;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var logger = NullLogger.Instance;

Console.WriteLine("=== AS_PWB_MarkaCarpan (marka -> NetCarpan) ===\n");

BrandLoadResult? oldBrands = null, newBrands = null;
try { oldBrands = await new NebimBrandProvider(oldConn, logger).GetBrandMultipliersAsync(); }
catch (Exception ex) { Console.WriteLine($"ESKİ bağlantı HATA: {ex.Message}"); }

try { newBrands = await new NebimBrandProvider(newConn, logger).GetBrandMultipliersAsync(); }
catch (Exception ex) { Console.WriteLine($"YENİ bağlantı HATA: {ex.Message}"); }

if (oldBrands is null || newBrands is null)
{
    Console.WriteLine("\nİki bağlantıdan biri başarısız olduğu için marka karşılaştırması atlanıyor.");
}
else
{
    Console.WriteLine($"ESKİ: {oldBrands.Brands.Count} geçerli marka, {oldBrands.Excluded.Count} NetCarpan<=0 elendi (toplam {oldBrands.Brands.Count + oldBrands.Excluded.Count}).");
    Console.WriteLine($"YENİ: {newBrands.Brands.Count} geçerli marka, {newBrands.Excluded.Count} NetCarpan<=0 elendi (toplam {newBrands.Brands.Count + newBrands.Excluded.Count}).\n");

    // FullName (OnEkAciklamasi) üzerinden eşleştir — BrandMatcher'ın da eşleşme anahtarı budur.
    var oldByName = Dedup(oldBrands.Brands);
    var newByName = Dedup(newBrands.Brands);

    var onlyOld = oldByName.Keys.Except(newByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
    var onlyNew = newByName.Keys.Except(oldByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
    var common = oldByName.Keys.Intersect(newByName.Keys, StringComparer.OrdinalIgnoreCase).ToList();

    var differing = new List<(string Name, decimal Old, decimal New)>();
    var same = 0;
    foreach (var name in common)
    {
        var o = oldByName[name].NetCarpan;
        var n = newByName[name].NetCarpan;
        if (Math.Abs(o - n) > 0.0001m) differing.Add((name, o, n));
        else same++;
    }

    Console.WriteLine($"Ortak marka adı: {common.Count} (NetCarpan aynı: {same}, farklı: {differing.Count})");
    Console.WriteLine($"Sadece ESKİ'de: {onlyOld.Count}");
    Console.WriteLine($"Sadece YENİ'de: {onlyNew.Count}\n");

    if (differing.Count > 0)
    {
        Console.WriteLine("-- NetCarpan farklı olan ortak markalar --");
        foreach (var (name, o, n) in differing.OrderBy(x => x.Name))
            Console.WriteLine($"  {name}: eski={o}  yeni={n}");
        Console.WriteLine();
    }

    if (onlyOld.Count > 0)
    {
        Console.WriteLine("-- Sadece ESKİ'de olan markalar --");
        foreach (var name in onlyOld) Console.WriteLine($"  {name} (NetCarpan={oldByName[name].NetCarpan})");
        Console.WriteLine();
    }

    if (onlyNew.Count > 0)
    {
        Console.WriteLine("-- Sadece YENİ'de olan markalar --");
        foreach (var name in onlyNew) Console.WriteLine($"  {name} (NetCarpan={newByName[name].NetCarpan})");
        Console.WriteLine();
    }
}

Console.WriteLine("=== AllExchangeRates (USD kuru) ===\n");

(decimal Rate, DateTime RateDate)? oldRate = null, newRate = null;
try { oldRate = await new NebimRateProvider(oldConn, logger).GetUsdRateAsync(DateTime.Today); }
catch (Exception ex) { Console.WriteLine($"ESKİ kur sorgusu HATA: {ex.Message}"); }

try { newRate = await new NebimRateProvider(newConn, logger).GetUsdRateAsync(DateTime.Today); }
catch (Exception ex) { Console.WriteLine($"YENİ kur sorgusu HATA: {ex.Message}"); }

Console.WriteLine($"ESKİ: {(oldRate is null ? "bulunamadı" : $"{oldRate.Value.Rate} ({oldRate.Value.RateDate:yyyy-MM-dd})")}");
Console.WriteLine($"YENİ: {(newRate is null ? "bulunamadı" : $"{newRate.Value.Rate} ({newRate.Value.RateDate:yyyy-MM-dd})")}");
if (oldRate is not null && newRate is not null)
    Console.WriteLine($"Fark: {Math.Abs(oldRate.Value.Rate - newRate.Value.Rate)} (kur tarihleri {(oldRate.Value.RateDate == newRate.Value.RateDate ? "aynı" : "FARKLI")})");

return 0;

static Dictionary<string, BrandMultiplier> Dedup(List<BrandMultiplier> brands)
{
    // Aynı FullName view'de birden fazla satırda olabilir (bilinen, zararsız — CLAUDE.md).
    // Karşılaştırma için ilkini tutmak yeterli.
    var dict = new Dictionary<string, BrandMultiplier>(StringComparer.OrdinalIgnoreCase);
    foreach (var b in brands)
        dict.TryAdd(b.FullName, b);
    return dict;
}
