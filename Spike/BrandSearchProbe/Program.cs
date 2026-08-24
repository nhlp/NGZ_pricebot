using Microsoft.Extensions.Logging.Abstractions;
using PriceBotPipeline;

// JOJOMİNİ tanı script'i (2026-08-21): AS_PWB_MarkaCarpan'da bu adla (veya benzer bir yazımla)
// bir marka olup olmadığını doğrudan sorgular — Worker.cs'in GERÇEK NebimBrandProvider'ını
// kullanır (NebimConnectionProbe ile aynı desen). Bağlantı dizesi KOMUT SATIRINA/KAYNAĞA
// YAZILMAZ, ortam değişkeninden okunur:
//   $env:NEBIM_CONN = "Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True;"
//   dotnet run --project Spike\BrandSearchProbe\brandsearchprobe.csproj -- <arama-terimi>

var conn = Environment.GetEnvironmentVariable("NEBIM_CONN");
if (string.IsNullOrWhiteSpace(conn))
{
    Console.WriteLine("HATA: NEBIM_CONN ortam değişkeni gerekli.");
    return 1;
}

var term = args.Length > 0 ? args[0] : "JOJO";

Console.OutputEncoding = System.Text.Encoding.UTF8;
var logger = NullLogger.Instance;

var result = await new NebimBrandProvider(conn, logger).GetBrandMultipliersAsync();
Console.WriteLine($"Toplam {result.Brands.Count} geçerli marka, {result.Excluded.Count} NetCarpan<=0 elendi.\n");

Console.WriteLine($"=== '{term}' içeren markalar (geçerli liste) ===");
var hits = result.Brands.Where(b => b.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) || b.OnEk.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
if (hits.Count == 0) Console.WriteLine("  (yok)");
foreach (var b in hits) Console.WriteLine($"  OnEk='{b.OnEk}' FullName='{b.FullName}' NetCarpan={b.NetCarpan}");

Console.WriteLine($"\n=== '{term}' içeren markalar (elenmiş, NetCarpan<=0) ===");
var excludedHits = result.Excluded.Where(b => b.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) || b.OnEk.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
if (excludedHits.Count == 0) Console.WriteLine("  (yok)");
foreach (var b in excludedHits) Console.WriteLine($"  OnEk='{b.OnEk}' FullName='{b.FullName}' NetCarpan={b.NetCarpan}");

return 0;
