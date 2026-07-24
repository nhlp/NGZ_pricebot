using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PriceBotPipeline;

public class Worker : BackgroundService
{
    private const string IncomingRoot = @"C:\PriceBot\Incoming";
    //private const string BotSendUrl = "http://localhost:3978/api/whatsapp/internal/send"; // Bot portu 3978'e eşitlendi!
    private const string BotSendUrl =  "https://asistyazilim.pakabulut.com:2304/api/whatsapp/internal/send";
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    private sealed record ImageResult(
        string FileName,
        bool Matched,
        string? Code,
        double? Confidence,
        int CandidateCount,
        decimal? PriceTry,
        decimal? PriceUsd,
        string? OutputFileName,
        string? SkipOrErrorReason,
        bool IsFuzzy = false);

    private sealed record SendResult(string FileName, string Recipient, bool Success, string StatusInfo);

    /// <summary>Damgalanmış ama henüz (tüm alıcılara) başarıyla gönderilememiş bir dosya/alıcı
    /// çifti. Klasörün kök dizininde "gonderim_bekleyen.json" olarak saklanır — bot kapalıyken
    /// biten bir gönderim turunda mesajın sessizce kaybolmasını önlemek için: islendi.txt yine de
    /// yazılır (pahalı OCR/damgalama işi tekrarlanmasın), ama bu dosya var olduğu sürece worker
    /// her turda SADECE bu bekleyen çiftleri tekrar göndermeyi dener — zaten başarılı olmuş
    /// alıcılara ikinci kez göndermeden.</summary>
    private sealed record PendingSend(string FilePath, string Recipient);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var appDir = AppContext.BaseDirectory;
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(appDir, "appsettings.json")))!;
        var nebimConnectionString = config["ConnectionStrings"]!["Nebim"]!.GetValue<string>();
        var extraRecipients = config["ExtraRecipients"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];

        using var http = new HttpClient();
        var rateProvider = new NebimRateProvider(nebimConnectionString);
        using var ocr = new FullScanOcr(Path.Combine(appDir, "tessdata"), @"\d{4,7}");

        _logger.LogInformation("PriceBot Worker başladı. IncomingRoot={IncomingRoot} BotSendUrl={BotSendUrl} ExtraRecipients={ExtraRecipients}",
            IncomingRoot, BotSendUrl, extraRecipients.Count == 0 ? "(boş)" : string.Join(", ", extraRecipients));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!Directory.Exists(IncomingRoot))
                {
                    _logger.LogWarning("IncomingRoot bulunamadı: {IncomingRoot}, 5 sn sonra tekrar denenecek.", IncomingRoot);
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                // "~$dosya.xlsx" Excel'in dosya açıkken oluşturduğu geçici kilit dosyasıdır, gerçek içerik değildir.
                static IEnumerable<string> RealXlsxFiles(string dir) =>
                    Directory.EnumerateFiles(dir, "*.xlsx").Where(f => !Path.GetFileName(f).StartsWith("~$"));

                // Önce bekleyen (daha önce gönderilemeyen) gönderimleri tekrar dene — bu
                // klasörler zaten OCR/damgalama işini tamamlamış (islendi.txt yazılmış), sadece
                // gönderim adımı (ör. bot kapalıyken) başarısız olmuştu. OCR'ı tekrarlamadan,
                // SADECE hâlâ başarısız olan (dosya, alıcı) çiftlerini yeniden dener.
                var pendingSendFolders = Directory
                    .EnumerateFiles(IncomingRoot, "gonderim_bekleyen.json", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .Where(dir => dir is not null)
                    .Select(dir => dir!)
                    .ToList();

                foreach (var folder in pendingSendFolders)
                {
                    await RetryPendingSendsAsync(http, folder, stoppingToken);
                }

                var readyFolders = Directory
                    .EnumerateDirectories(IncomingRoot, "Gonderim_*", SearchOption.AllDirectories)
                    .Where(dir =>
                        !File.Exists(Path.Combine(dir, "islendi.txt")) &&
                        RealXlsxFiles(dir).Any() &&
                        DateTime.Now - Directory.EnumerateFiles(dir).Select(File.GetLastWriteTime).Max() > TimeSpan.FromSeconds(60))
                    .ToList();

                if (readyFolders.Count > 0)
                {
                    _logger.LogInformation("Taramada {Count} hazır klasör bulundu.", readyFolders.Count);
                }

                foreach (var folder in readyFolders)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var processStart = DateTime.Now;

                    _logger.LogInformation("=== İşleniyor: {Folder} ===", folder);
                    string senderPhone = new DirectoryInfo(folder).Parent!.Name;

                    var excelFile = RealXlsxFiles(folder).First();
                    _logger.LogInformation("Excel okunuyor: {File}", Path.GetFileName(excelFile));
                    var excelPrices = ExcelPriceReader.LoadPricesFromExcel(excelFile, out var skippedExcelRows);
                    _logger.LogInformation("Excel'den {Count} adet ürün fiyatı hafızaya alındı.", excelPrices.Count);
                    foreach (var (rowNum, code, rawPrice) in skippedExcelRows)
                    {
                        _logger.LogWarning("Excel satır {Row} atlandı: kod '{Code}', fiyat '{RawPrice}' sayıya çevrilemedi.",
                            rowNum, code, rawPrice);
                    }

                    var rate = await rateProvider.GetUsdRateAsync(DateTime.Today);
                    if (rate is null)
                    {
                        _logger.LogWarning("USD kuru bulunamadı, klasör {Folder} bu turda atlandı, bir sonraki turda tekrar denenecek.", folder);
                        continue;
                    }
                    _logger.LogInformation("Kur: 1 USD = {Rate} TRY (kur tarihi: {RateDate:yyyy-MM-dd})", rate.Value.Rate, rate.Value.RateDate);

                    var excelCodes = new HashSet<string>(excelPrices.Keys, StringComparer.OrdinalIgnoreCase);

                    var outputDir = Path.Combine(folder, "Islenmis");
                    Directory.CreateDirectory(outputDir);
                    var stampedFiles = new List<string>();
                    var imageResults = new List<ImageResult>();

                    var allFiles = Directory.EnumerateFiles(folder)
                        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                        .ToList();
                    _logger.LogInformation("Klasörde {Count} adet desteklenen görsel bulundu.", allFiles.Count);

                    // Aşama 1: her görselin OCR'ını topla, henüz karar verme. Bir klasördeki her
                    // Excel kodu tek bir ürünü/fotoğrafı temsil eder; bu yüzden aynı kod birden
                    // fazla görselde "eşleşti" çıkarsa (örn. dekoratif fontta bir hane yanlış
                    // okunup başka geçerli bir koda dönüşürse — "1379" -> "1349" gibi), bu bir
                    // OCR hatasının işaretidir, tesadüf değil.
                    var scans = allFiles.Select(file => (File: file, Scan: ocr.FindProductCodes(file, excelCodes))).ToList();

                    // Aşama 2: klasör geneli çakışma çözümü. Tüm (görsel, aday kod) çiftlerini
                    // güvene göre büyükten küçüğe sıralayıp aç gözlü (greedy) ata: bir kod veya
                    // görsel bir kez atandıktan sonra tekrar kullanılamaz. Böylece iki görsel aynı
                    // kodu "bulursa", sadece en yüksek güvenli olan o kodu alır — diğeri, yanlış
                    // fiyat atamak yerine güvenle atlanır ve kalan eşleşmemiş kodlar için tekrar
                    // aday olabilir hâle gelmez (kod zaten tüketilmiş sayılır).
                    var allPairs = scans
                        .SelectMany((s, idx) => s.Scan.Matches.Select(m => (ImageIdx: idx, Match: m)))
                        .OrderByDescending(p => p.Match.Confidence)
                        .ToList();

                    var assignedImages = new HashSet<int>();
                    var assignedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var chosen = new Dictionary<int, CodeMatch>();

                    foreach (var pair in allPairs)
                    {
                        if (assignedImages.Contains(pair.ImageIdx) || assignedCodes.Contains(pair.Match.Code)) continue;
                        chosen[pair.ImageIdx] = pair.Match;
                        assignedImages.Add(pair.ImageIdx);
                        assignedCodes.Add(pair.Match.Code);
                    }

                    // Aşama 3: sonuçlara göre damgala/gönder.
                    for (int idx = 0; idx < scans.Count; idx++)
                    {
                        var (file, scan) = scans[idx];
                        var fileName = Path.GetFileName(file);

                        if (!chosen.TryGetValue(idx, out var best))
                        {
                            // Adayların rapora yazılması teşhis için kritik: "OCR mi okuyamadı,
                            // Excel'de mi yoktu" sorusu islendi.txt'ye bakarak tek seferde cevaplanır
                            // (Bebly 20020 vakasında bu bilgi olmadığı için teşhis loglardan yapılamamıştı).
                            string reason = scan.Matches.Count == 0
                                ? scan.Candidates.Count == 0
                                    ? "Görselden hiçbir sayısal kod adayı okunamadı"
                                    : $"Excel'deki kodlardan biriyle eşleşen bir ürün kodu bulunamadı — OCR'ın okuduğu adaylar: {string.Join(", ", scan.Candidates)}"
                                : $"Bulunan kod(lar) ({string.Join(", ", scan.Matches.Select(m => m.Code))}) bu klasörde daha yüksek güvenle başka bir görsele atandı, yanlış fiyat riskine karşı atlandı";
                            _logger.LogWarning("'{File}' atlandı: {Reason}", fileName, reason);
                            imageResults.Add(new ImageResult(fileName, false, null, null, scan.Matches.Count, null, null, null, reason));
                            continue;
                        }

                        decimal priceInTry = excelPrices[best.Code];
                        decimal priceInUsd = priceInTry / rate.Value.Rate;

                        if (scan.Matches.Count > 1)
                        {
                            _logger.LogInformation("'{File}' içinde {Count} aday kod bulundu, en yüksek güvenli seçildi: {Code} (güven {Confidence:N0}). Diğer adaylar: {Others}",
                                fileName, scan.Matches.Count, best.Code, best.Confidence,
                                string.Join(", ", scan.Matches.Where(m => m.Code != best.Code).Select(m => $"{m.Code} ({m.Confidence:N0})")));
                        }

                        try
                        {
                            var stamped = PriceStamper.Stamp(file, outputDir, priceInUsd);
                            stampedFiles.Add(stamped);
                            imageResults.Add(new ImageResult(fileName, true, best.Code, best.Confidence, scan.Matches.Count,
                                priceInTry, priceInUsd, Path.GetFileName(stamped), null, best.IsFuzzy));
                            if (best.IsFuzzy)
                            {
                                _logger.LogWarning("'{File}' -> YAKLAŞIK (fuzzy) eşleşme, KONTROL ÖNERİLİR: kod {Code} (güven {Confidence:N0}) -> {PriceTry:N2} TRY ({PriceUsd:N2} USD) -> {Output}",
                                    fileName, best.Code, best.Confidence, priceInTry, priceInUsd, Path.GetFileName(stamped));
                            }
                            else
                            {
                                _logger.LogInformation("Eşleşti: {File} -> kod {Code} (güven {Confidence:N0}) -> {PriceTry:N2} TRY ({PriceUsd:N2} USD) -> {Output}",
                                    fileName, best.Code, best.Confidence, priceInTry, priceInUsd, Path.GetFileName(stamped));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "HATA (Resim basılamadı): {File}", fileName);
                            imageResults.Add(new ImageResult(fileName, true, best.Code, best.Confidence, scan.Matches.Count,
                                priceInTry, priceInUsd, null, $"Damgalama hatası: {ex.Message}", best.IsFuzzy));
                        }
                    }

                    var recipients = new[] { senderPhone }.Concat(extraRecipients).Distinct().ToList();
                    var sendResults = new List<SendResult>();
                    var stillPending = new List<PendingSend>();

                    foreach (var file in stampedFiles)
                    {
                        foreach (var recipient in recipients)
                        {
                            var result = await TrySendAsync(http, file, recipient, stoppingToken);
                            sendResults.Add(result);
                            if (!result.Success) stillPending.Add(new PendingSend(file, recipient));
                            await Task.Delay(400, stoppingToken);
                        }
                    }

                    stopwatch.Stop();
                    var processEnd = DateTime.Now;

                    var report = BuildReport(folder, senderPhone, excelFile, excelPrices.Count, skippedExcelRows, rate.Value.Rate, rate.Value.RateDate,
                        allFiles.Count, imageResults, recipients, sendResults, processStart, processEnd, stopwatch.Elapsed);

                    // islendi.txt, OCR/eşleştirme/damgalama işinin TAMAMLANDIĞINI işaretler (bu iş
                    // pahalı ve idempotent değildir, tekrarlanmamalı). Gönderim başarısız olan
                    // (dosya, alıcı) çiftleri varsa ayrı bir bekleyen-gönderim dosyasına yazılır;
                    // worker her turda bu dosyayı görüp SADECE o çiftleri tekrar dener — böylece
                    // bot kapalıyken biten bir tur mesajı sessizce kaybetmez, ama zaten başarılı
                    // olmuş alıcılara ikinci kez göndermez.
                    File.WriteAllText(Path.Combine(folder, "islendi.txt"), report, Encoding.UTF8);
                    if (stillPending.Count > 0)
                    {
                        WritePendingSends(folder, stillPending);
                        _logger.LogWarning("{Count} gönderim başarısız oldu, klasör {Folder} için 'gonderim_bekleyen.json' yazıldı — bir sonraki turda sadece bu gönderimler tekrar denenecek.",
                            stillPending.Count, folder);
                    }
                    _logger.LogInformation("=== Tamamlandı: {Folder} — toplam {Total} görsel, {Stamped} damgalandı, {Skipped} atlandı, süre {Duration:N1} sn ===",
                        folder, allFiles.Count, stampedFiles.Count, imageResults.Count(r => !r.Matched), stopwatch.Elapsed.TotalSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Döngü hatası (devam ediliyor)");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("PriceBot Worker durduruluyor.");
    }

    private async Task<SendResult> TrySendAsync(HttpClient http, string filePath, string recipient, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var body = JsonSerializer.Serialize(new { ToNumber = recipient, MessageText = "", FilePath = filePath });

        try
        {
            var resp = await http.PostAsync(BotSendUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
            bool success = resp.IsSuccessStatusCode;
            _logger.LogInformation("Gönderim: {File} -> {Recipient} -> {Status}", fileName, recipient, resp.StatusCode);
            return new SendResult(fileName, recipient, success, ((int)resp.StatusCode).ToString() + " " + resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Gönderim hatası: {File} -> {Recipient}", fileName, recipient);
            return new SendResult(fileName, recipient, false, $"HATA: {ex.Message}");
        }
    }

    private static string PendingSendsPath(string folder) => Path.Combine(folder, "gonderim_bekleyen.json");

    private static void WritePendingSends(string folder, List<PendingSend> pending) =>
        File.WriteAllText(PendingSendsPath(folder), JsonSerializer.Serialize(pending), Encoding.UTF8);

    /// <summary>Daha önce (ör. bot kapalıyken) gönderilemeyen (dosya, alıcı) çiftlerini, OCR/
    /// damgalama işini TEKRARLAMADAN yeniden dener — çünkü bu klasör için islendi.txt zaten
    /// yazılmış, damgalı dosyalar Islenmis/ altında hazır duruyor. Hâlâ başarısız olanlar dosyada
    /// kalır, tamamı başarılı olursa dosya silinir.</summary>
    private async Task RetryPendingSendsAsync(HttpClient http, string folder, CancellationToken ct)
    {
        var path = PendingSendsPath(folder);
        List<PendingSend>? pending;
        try
        {
            pending = JsonSerializer.Deserialize<List<PendingSend>>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "'{Path}' okunamadı, bekleyen gönderimler bu turda atlandı.", path);
            return;
        }
        if (pending is null || pending.Count == 0) { File.Delete(path); return; }

        _logger.LogInformation("Klasör {Folder} için {Count} bekleyen gönderim tekrar deneniyor.", folder, pending.Count);

        var stillPending = new List<PendingSend>();
        foreach (var item in pending)
        {
            var result = await TrySendAsync(http, item.FilePath, item.Recipient, ct);
            if (result.Success)
                _logger.LogInformation("Gecikmeli gönderim başarılı: {File} -> {Recipient}", Path.GetFileName(item.FilePath), item.Recipient);
            else
                stillPending.Add(item);

            await Task.Delay(400, ct);
        }

        if (stillPending.Count == 0) File.Delete(path);
        else WritePendingSends(folder, stillPending);
    }

    private static string BuildReport(
        string folder,
        string senderPhone,
        string excelFile,
        int excelProductCount,
        List<(int Row, string Code, string RawPrice)> skippedExcelRows,
        decimal rateValue,
        DateTime rateDate,
        int totalImages,
        List<ImageResult> imageResults,
        List<string> recipients,
        List<SendResult> sendResults,
        DateTime processStart,
        DateTime processEnd,
        TimeSpan duration)
    {
        var matched = imageResults.Where(r => r.Matched && r.OutputFileName is not null).ToList();
        var skippedOrFailed = imageResults.Where(r => r.OutputFileName is null).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("=== PriceBot İşlem Raporu ===");
        sb.AppendLine($"Klasör: {folder}");
        sb.AppendLine($"Gönderen numara: {senderPhone}");
        sb.AppendLine($"İşlem başlangıcı: {processStart:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"İşlem bitişi: {processEnd:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Toplam süre: {duration.TotalSeconds:N1} sn");
        sb.AppendLine();

        sb.AppendLine("--- Excel ---");
        sb.AppendLine($"Dosya: {Path.GetFileName(excelFile)}");
        sb.AppendLine($"Ürün sayısı: {excelProductCount}");
        if (skippedExcelRows.Count > 0)
        {
            sb.AppendLine($"UYARI: {skippedExcelRows.Count} satırın fiyatı sayıya çevrilemediği için atlandı:");
            foreach (var (row, code, rawPrice) in skippedExcelRows)
                sb.AppendLine($"  satır {row}: kod '{code}', ham fiyat '{rawPrice}'");
        }
        sb.AppendLine();

        sb.AppendLine("--- Kur ---");
        sb.AppendLine($"1 USD = {rateValue} TRY (kur tarihi: {rateDate:yyyy-MM-dd}, kaynak: Nebim ERP)");
        sb.AppendLine();

        sb.AppendLine($"--- Görseller (toplam: {totalImages}, damgalanan: {matched.Count}, atlanan/hatalı: {skippedOrFailed.Count}) ---");
        foreach (var r in imageResults)
        {
            if (r.OutputFileName is not null)
            {
                var candidateNote = r.CandidateCount > 1 ? $", {r.CandidateCount} aday arasından seçildi" : "";
                var tag = r.IsFuzzy ? "[DAMGALANDI-FUZZY, KONTROL ÖNERİLİR]" : "[DAMGALANDI]";
                sb.AppendLine($"{tag} {r.FileName} -> kod {r.Code} (güven {r.Confidence:N0}{candidateNote}) -> {r.PriceTry:N2} TRY / {r.PriceUsd:N2} USD -> {r.OutputFileName}");
            }
            else if (r.Code is not null)
            {
                sb.AppendLine($"[HATA] {r.FileName} -> kod {r.Code} bulundu ama damgalanamadı: {r.SkipOrErrorReason}");
            }
            else
            {
                sb.AppendLine($"[ATLANDI] {r.FileName} -> {r.SkipOrErrorReason}");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"--- Gönderim (alıcılar: {string.Join(", ", recipients)}) ---");
        if (sendResults.Count == 0)
        {
            sb.AppendLine("(Gönderilecek damgalı görsel olmadığı için gönderim yapılmadı.)");
        }
        foreach (var s in sendResults)
        {
            sb.AppendLine($"[{(s.Success ? "OK" : "HATA")}] {s.FileName} -> {s.Recipient} -> {s.StatusInfo}");
        }
        sb.AppendLine();

        sb.AppendLine("--- Özet ---");
        sb.AppendLine($"Toplam görsel: {totalImages}");
        sb.AppendLine($"Damgalanan: {matched.Count}");
        sb.AppendLine($"Atlanan/hatalı: {skippedOrFailed.Count}");
        sb.AppendLine($"Gönderilen mesaj sayısı: {sendResults.Count} (başarılı: {sendResults.Count(s => s.Success)}, başarısız: {sendResults.Count(s => !s.Success)})");

        return sb.ToString();
    }
}
