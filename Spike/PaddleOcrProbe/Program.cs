using System.Diagnostics;
using PriceBotPipeline;

// Fix doğrulama script'i (2026-08-10): Worker.cs'in Aşama 1 (OCR tara) + Aşama 1.4 (kod sütunu
// oylaması) + Aşama 4 (kod->fiyat eşleştirme) mantığını BİREBİR taklit eder ama hiçbir dosya
// yazmaz, hiçbir yere göndermez — sadece gerçek klasörler + gerçek görsellerle fix'in gerçekten
// çalışıp çalışmadığını raporlar. Argüman: incelenecek Gonderim klasörünün tam yolu.

if (args.Length == 0)
{
    Console.WriteLine("Kullanım: probe <Gonderim klasörü yolu>");
    return 1;
}

var folder = args[0];
var excelFiles = Directory.EnumerateFiles(folder, "*.xlsx")
    .Where(f => !Path.GetFileName(f).StartsWith("~$"))
    .ToList();

if (excelFiles.Count != 1)
{
    Console.WriteLine($"UYARI: klasörde {excelFiles.Count} xlsx var, script tek-Excel klasörleri için yazıldı.");
    return 1;
}

var excelFile = excelFiles[0];
Console.WriteLine($"Excel: {Path.GetFileName(excelFile)}");

var codeColumnCandidates = ExcelPriceReader.LoadCandidateCodeColumns(excelFile);
if (codeColumnCandidates.Count == 0)
{
    Console.WriteLine("HATA: Excel'de olası bir ürün kodu sütunu bulunamadı.");
    return 1;
}
Console.WriteLine($"{codeColumnCandidates.Count} olası kod sütunu: " +
    string.Join(" | ", codeColumnCandidates.Select(c => $"'{c.HeaderName}' (öncelik={c.HeaderPriority}, {c.Prices.Count} ürün)")));

var excelCodesUnion = new HashSet<string>(
    codeColumnCandidates.SelectMany(c => c.Prices.Keys), StringComparer.OrdinalIgnoreCase);
var descriptionsUnion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var col in codeColumnCandidates)
    foreach (var (code, desc) in col.Descriptions)
        descriptionsUnion[code] = desc;

var images = Directory.EnumerateFiles(folder)
    .Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
    .OrderBy(f => f)
    .ToList();
Console.WriteLine($"{images.Count} görsel bulundu.\n");

Console.WriteLine("PaddleOCR motoru kuruluyor (ilk kurulum birkaç saniye sürebilir)...");
var setupTimer = Stopwatch.StartNew();
using var pool = OcrEngineFactory.Create(parallelism: 1);
Console.WriteLine($"Motor hazır ({setupTimer.Elapsed.TotalSeconds:N1} sn).\n");

var scanResults = new ScanResult[images.Count];
for (int i = 0; i < images.Count; i++)
{
    var t = Stopwatch.StartNew();
    scanResults[i] = pool.Run(o => o.FindProductCodes(images[i], excelCodesUnion, descriptionsUnion));
    Console.WriteLine($"  OCR {Path.GetFileName(images[i])} -> {scanResults[i].Matches.Count} eşleşme, " +
        $"{scanResults[i].Candidates.Count} aday ({t.Elapsed.TotalSeconds:N1} sn)");
}
var scans = images.Select((file, i) => (File: file, Scan: scanResults[i])).ToList();

// Aşama 1.4: kod sütunu oylaması (Worker.cs ile birebir aynı).
var columnVotes = codeColumnCandidates.ToDictionary(c => c, _ => 0);
foreach (var (_, scan) in scans)
    foreach (var match in scan.Matches)
        foreach (var col in codeColumnCandidates)
            if (col.Prices.ContainsKey(match.Code))
                columnVotes[col]++;

var codeColumn = columnVotes
    .OrderByDescending(kv => kv.Value)
    .ThenByDescending(kv => kv.Key.HeaderPriority)
    .ThenByDescending(kv => kv.Key.Prices.Count)
    .First().Key;

Console.WriteLine($"\nKazanan kod sütunu: '{codeColumn.HeaderName}' ({string.Join(", ", columnVotes.Select(kv => $"'{kv.Key.HeaderName}'={kv.Value} oy"))})\n");

var excelPrices = codeColumn.Prices;
var excelCodes = new HashSet<string>(excelPrices.Keys, StringComparer.OrdinalIgnoreCase);
Console.WriteLine($"Excel kodları ({excelCodes.Count}): {string.Join(", ", excelCodes)}\n");

int stamped = 0, skipped = 0;
foreach (var (file, scan) in scans)
{
    var matches = scan.Matches.Where(m => excelCodes.Contains(m.Code)).ToList();
    if (matches.Count == 0)
    {
        skipped++;
        Console.WriteLine($"[ATLANDI] {Path.GetFileName(file)} -> eşleşme yok. Adaylar: {string.Join(", ", scan.Candidates.Take(12))}");
    }
    else
    {
        stamped++;
        foreach (var m in matches)
        {
            var price = excelPrices[m.Code];
            var fuzzy = m.IsFuzzy ? " FUZZY" : "";
            Console.WriteLine($"[DAMGALANDI{fuzzy}] {Path.GetFileName(file)} -> kod {m.Code} (güven {m.Confidence:N0}) -> {price} TL. Adaylar: {string.Join(", ", scan.Candidates.Take(12))}");
        }
    }
}

Console.WriteLine($"\n=== ÖZET === Toplam: {images.Count}, Damgalanan: {stamped}, Atlanan: {skipped} ({100.0 * stamped / Math.Max(1, images.Count):N1}%)");
return 0;
