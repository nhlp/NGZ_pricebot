using System.Diagnostics;
using PriceBotPipeline;

// DECO SPORT / MESSINHO / ARD KİDSWEAR yanlış-marka-sorusu vakasını teşhis script'i (2026-08-11).
// Worker.ResolveFolderBrandAsync'in marka tespiti kısmını BİREBİR taklit eder (WhatsApp gönderimi,
// Nebim DB erişimi, Gemini çağrısı YOK) — gerçek görseller + kullanıcının verdiği GERÇEK
// AS_PWB_MarkaCarpan listesiyle (brands.tsv) neden ambiguous/yanlış sonuç çıktığını gösterir.

var folder = args.Length > 0 ? args[0] : @"C:\Users\glnhl\OneDrive\Masaüstü\NGZ\testFto3";
var brandsPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "brands.tsv");

Console.OutputEncoding = System.Text.Encoding.UTF8;

// --- Marka listesi yükle (NebimBrandProvider'ın gerçek DB sorgusu yerine TSV) ---
var allBrands = new List<BrandMultiplier>();
foreach (var line in File.ReadAllLines(brandsPath, System.Text.Encoding.UTF8).Skip(1))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var parts = line.Split('\t');
    if (parts.Length < 3) continue;
    var onEk = parts[0].Trim();
    var fullName = parts[1].Trim();
    if (!decimal.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var netCarpan))
        continue;
    allBrands.Add(new BrandMultiplier(onEk, fullName, netCarpan));
}
// NebimBrandProvider: NetCarpan <= 0 satırlar veri hatasıdır, eşleştirme dışı bırakılır.
var brandList = allBrands.Where(b => b.NetCarpan > 0).ToList();
var excludedBrands = allBrands.Where(b => b.NetCarpan <= 0).ToList();
Console.WriteLine($"Marka listesi: {allBrands.Count} satır ({brandList.Count} geçerli, {excludedBrands.Count} NetCarpan<=0 elendi).\n");

// --- Excel oku (kod sütunu + kodlar; Tokens yan-ürünü için gerekli) ---
var excelFiles = Directory.EnumerateFiles(folder, "*.xlsx")
    .Where(f => !Path.GetFileName(f).StartsWith("~$"))
    .ToList();
if (excelFiles.Count != 1)
{
    Console.WriteLine($"UYARI: klasörde {excelFiles.Count} xlsx var.");
    return 1;
}
Console.WriteLine($"Excel: {Path.GetFileName(excelFiles[0])}");
var codeColumnCandidates = ExcelPriceReader.LoadCandidateCodeColumns(excelFiles[0]);
var excelCodesUnion = new HashSet<string>(
    codeColumnCandidates.SelectMany(c => c.Prices.Keys), StringComparer.OrdinalIgnoreCase);

var images = Directory.EnumerateFiles(folder)
    .Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
    .OrderBy(f => f)
    .ToList();
Console.WriteLine($"{images.Count} görsel bulundu.\n");

Console.WriteLine("PaddleOCR motoru kuruluyor...");
var setupTimer = Stopwatch.StartNew();
// parallelism=1 bilinçli: bu dev makinede parallelism>1 (Parallel.For + adanmış Paddle
// thread'leri) 2026-08-11'de PaddleOcrClassifier.ShouldRotate180 içinde 0xC0000005 (native
// access violation) ile segfault etti (Worker.cs'in kendisi production'da bu parallelism
// seviyesinde çalışıyor, muhtemelen bu spike/dev-makine'ye özgü bir flakiness) — teşhis
// script'i için hız değil doğruluk önemli, tek thread'de bu risk yok.
using var pool = OcrEngineFactory.Create(parallelism: 1);
Console.WriteLine($"Motor hazır ({setupTimer.Elapsed.TotalSeconds:N1} sn).\n");

// --- Aşama 1: kod taraması (Tokens yan ürünü marka tespiti için de kullanılır, Worker.cs ile aynı) ---
var scanResults = new ScanResult[images.Count];
var scanOptions = new ParallelOptions { MaxDegreeOfParallelism = pool.Size };
var timer = Stopwatch.StartNew();
Parallel.For(0, images.Count, scanOptions,
    i => scanResults[i] = pool.Run(o => o.FindProductCodes(images[i], excelCodesUnion)));
Console.WriteLine($"Kod taraması bitti ({timer.Elapsed.TotalSeconds:N1} sn).\n");
var scans = images.Select((file, i) => (File: file, Scan: scanResults[i])).ToList();

var unionTokens = new Dictionary<string, float>();
void MergeTokens(IReadOnlyDictionary<string, float> tokens)
{
    foreach (var (word, conf) in tokens)
        if (!unionTokens.TryGetValue(word, out var best) || conf > best)
            unionTokens[word] = conf;
}
foreach (var (_, scan) in scans) MergeTokens(scan.Tokens);

Console.WriteLine($"Birleşik token sayısı (kod taramasından): {unionTokens.Count}\n");

var ocrOutcome = BrandMatcher.MatchFromOcrTokens(unionTokens, brandList);
ReportOutcome("Kod-taraması token'larıyla ilk MatchFromOcrTokens", ocrOutcome);

if (ocrOutcome.Brand is null && ocrOutcome.AmbiguousNames.Count == 0)
{
    Console.WriteLine("Marka bulunamadı ve ambiguous da yok -> Worker.cs gibi renk-duyarlı kurtarma taraması deneniyor (ilk 4 görsel)...");
    var recoveryFiles = scans.Take(4).Select(s => s.File).ToList();
    var recoveredTokens = new Dictionary<string, float>[recoveryFiles.Count];
    var recoveryOptions = new ParallelOptions { MaxDegreeOfParallelism = pool.Size };
    Parallel.For(0, recoveryFiles.Count, recoveryOptions,
        i => recoveredTokens[i] = pool.Run(o => o.CollectBrandTokens(recoveryFiles[i])));
    foreach (var t in recoveredTokens) MergeTokens(t);
    ocrOutcome = BrandMatcher.MatchFromOcrTokens(unionTokens, brandList);
    Console.WriteLine($"Kurtarma sonrası birleşik token sayısı: {unionTokens.Count}\n");
    ReportOutcome("Kurtarma taraması SONRASI MatchFromOcrTokens", ocrOutcome);
}

if (excludedBrands.Count > 0)
{
    var excludedOutcome = BrandMatcher.MatchFromOcrTokens(unionTokens, excludedBrands);
    if (excludedOutcome.Brand is not null)
        Console.WriteLine($"NOT: NetCarpan<=0 elenmiş '{excludedOutcome.Brand.FullName}' de OCR token'larıyla eşleşiyordu (bilgi amaçlı).\n");
}

var suggestions = BrandMatcher.SuggestBrandsFromOcrTokens(unionTokens, brandList);
Console.WriteLine($"SuggestBrandsFromOcrTokens (yakınlık tahmini, sadece Brand==null && Ambiguous==0 ise kullanılır): {(suggestions.Count == 0 ? "(boş)" : string.Join(", ", suggestions))}\n");

var candidateOptions = ocrOutcome.AmbiguousNames.Count > 0 ? ocrOutcome.AmbiguousNames : suggestions;
Console.WriteLine("=== Worker.cs'in WhatsApp sorusuna koyacağı seçenekler ===");
Console.WriteLine(candidateOptions.Count == 0 ? "(seçeneksiz genel soru)" : string.Join(", ", candidateOptions));
Console.WriteLine($"(kaynak: {(ocrOutcome.AmbiguousNames.Count > 0 ? "çelişkili OCR eşleşmesi (AmbiguousNames)" : "yakınlık tahmini (SuggestBrandsFromOcrTokens)")})\n");

// --- Teşhis: DECO SPORT / MESSINHO / ARD KİDSWEAR ile ilgili ham token'ları göster ---
Console.WriteLine("=== İlgilenilen markaların kelimeleri için ham token eşleşmeleri ===");
foreach (var name in new[] { "DECO SPORT", "MESSINHO", "ARD KİDSWEAR" })
{
    var brand = allBrands.FirstOrDefault(b => b.FullName == name);
    if (brand is null) { Console.WriteLine($"{name}: listede yok!"); continue; }
    var words = BrandMatcher.NormalizeToWords(brand.FullName);
    Console.WriteLine($"{name} (NetCarpan={brand.NetCarpan}) kelimeleri: {string.Join(" + ", words)}");
    foreach (var w in words)
    {
        var hits = unionTokens
            .SelectMany(kv => BrandMatcher.NormalizeToWords(kv.Key).Select(nw => (Word: nw, Raw: kv.Key, Conf: kv.Value)))
            .Where(x => x.Word == w)
            .OrderByDescending(x => x.Conf)
            .Take(5)
            .ToList();
        Console.WriteLine($"  '{w}' -> {(hits.Count == 0 ? "(hiç token'da yok)" : string.Join(", ", hits.Select(h => $"'{h.Raw}'(conf={h.Conf:N0})")))}");
    }
}

Console.WriteLine("\n=== En yüksek güvenli 40 ham token (referans) ===");
foreach (var kv in unionTokens.OrderByDescending(kv => kv.Value).Take(40))
    Console.WriteLine($"  {kv.Key} (conf={kv.Value:N0})");

Console.WriteLine("\n=== Hangi görsel(ler) 'MESSINHO' / 'KIDSWEAR' / 'ARD' token'ını ürettі ===");
foreach (var word in new[] { "MESSINHO", "KIDSWEAR", "ARD" })
{
    Console.WriteLine($"'{word}':");
    foreach (var (file, scan) in scans)
    {
        var hit = scan.Tokens
            .Where(kv => BrandMatcher.NormalizeToWords(kv.Key).Contains(word))
            .OrderByDescending(kv => kv.Value)
            .FirstOrDefault();
        if (hit.Key is not null)
            Console.WriteLine($"    {Path.GetFileName(file)} -> '{hit.Key}' (conf={hit.Value:N0})");
    }
}

return 0;

void ReportOutcome(string label, OcrBrandOutcome outcome)
{
    Console.WriteLine($"--- {label} ---");
    if (outcome.Brand is not null)
        Console.WriteLine($"  SONUÇ: Brand='{outcome.Brand.FullName}' NetCarpan={outcome.Brand.NetCarpan} Approximate={outcome.Approximate} Evidence=[{string.Join(", ", outcome.MatchedWords)}]");
    else if (outcome.AmbiguousNames.Count > 0)
        Console.WriteLine($"  SONUÇ: AMBIGUOUS -> {string.Join(", ", outcome.AmbiguousNames)}");
    else
        Console.WriteLine("  SONUÇ: hiçbir marka bulunamadı (Brand=null, AmbiguousNames boş)");
    Console.WriteLine();
}
