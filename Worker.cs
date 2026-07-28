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
        decimal? PriceExcel,
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
        var rateProvider = new NebimRateProvider(nebimConnectionString, _logger);
        var brandProvider = new NebimBrandProvider(nebimConnectionString, _logger);

        // TesseractEngine thread-safe değil; görseller, başlangıçta bir kez kurulan sabit
        // boyutlu bir motor havuzuyla paralel taranır (motor kurulumu pahalı olduğu için
        // görsel başına değil, servis ömrü boyunca aynı örnekler kullanılır). Bir çekirdek
        // sistemin geri kalanına (bot, SQL) bırakılır; bellek için 6 örnekle sınırlanır.
        var ocrParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 6);
        using var ocrPool = new FullScanOcrPool(Path.Combine(appDir, "tessdata"), @"\d{4,7}", ocrParallelism);

        _logger.LogInformation("PriceBot Worker başladı. IncomingRoot={IncomingRoot} BotSendUrl={BotSendUrl} ExtraRecipients={ExtraRecipients} OcrParalel={OcrParallelism}",
            IncomingRoot, BotSendUrl, extraRecipients.Count == 0 ? "(boş)" : string.Join(", ", extraRecipients), ocrParallelism);

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
                        // Marka sorusu gönderilmiş ama cevap henüz gelmemişse klasöre dokunma:
                        // her 10 sn'de bir boşuna OCR koşturmamak için. Bot, gelen metin cevabını
                        // marka_cevap.txt olarak yazınca klasör tekrar işlenebilir hâle gelir.
                        (!File.Exists(Path.Combine(dir, "marka_sorusu.txt")) || File.Exists(Path.Combine(dir, "marka_cevap.txt"))) &&
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

                    // Süreç değişikliği (2026-07-28): birden fazla markanın fotoğraf + Excel'i
                    // aynı gönderimde gelebiliyor. Böyle klasörler doğrudan işlenmez; önce marka
                    // gruplarına bölünür — her görsel, OCR kodunun bulunduğu Excel'in grubuna
                    // atanır, her grup kardeş bir Gonderim klasörüne taşınır ve sonraki turlarda
                    // normal tek-marka akışıyla bağımsız işlenir (bkz. SplitMultiBrandFolder).
                    var excelFiles = RealXlsxFiles(folder).ToList();
                    if (excelFiles.Count > 1)
                    {
                        SplitMultiBrandFolder(folder, excelFiles, ocrPool, stoppingToken);
                        continue;
                    }

                    var excelFile = excelFiles[0];
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
                    _logger.LogInformation("Kur: 1 USD = {Rate} TRY (kur tarihi: {RateDate:yyyy-MM-dd}, AllExchangeRates tip 6)", rate.Value.Rate, rate.Value.RateDate);

                    var brandList = await brandProvider.GetBrandMultipliersAsync();
                    if (brandList.Count == 0)
                    {
                        _logger.LogWarning("AS_PWB_MarkaCarpan boş döndü, klasör {Folder} bu turda atlandı, bir sonraki turda tekrar denenecek.", folder);
                        continue;
                    }

                    var excelCodes = new HashSet<string>(excelPrices.Keys, StringComparer.OrdinalIgnoreCase);

                    var outputDir = Path.Combine(folder, "Islenmis");
                    Directory.CreateDirectory(outputDir);
                    var stampedFiles = new List<string>();
                    var imageResults = new List<ImageResult>();

                    var allFiles = Directory.EnumerateFiles(folder)
                        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                        .ToList();
                    _logger.LogInformation("Klasörde {Count} adet desteklenen görsel bulundu.", allFiles.Count);

                    if (allFiles.Count == 0)
                    {
                        // Görselsiz klasör (kullanıcı yalnız Excel göndermiş ya da çoklu marka
                        // bölmesi yarıda kalıp geriye yalnız-Excel'li bir grup klasörü kalmış):
                        // marka sorusu sormanın anlamı yok, işaretleyip kapat.
                        _logger.LogWarning("Klasör {Folder} hiç görsel içermiyor — işlem yapılmadan islendi.txt ile kapatıldı.", folder);
                        File.WriteAllText(Path.Combine(folder, "islendi.txt"),
                            $"=== PriceBot İşlem Raporu ==={Environment.NewLine}" +
                            $"Klasör: {folder}{Environment.NewLine}" +
                            $"Zaman: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                            $"Bu klasörde desteklenen görsel bulunamadı (yalnızca Excel); işlem yapılmadı.{Environment.NewLine}",
                            Encoding.UTF8);
                        continue;
                    }

                    // Aşama 1: her görselin OCR'ını topla, henüz karar verme. Bir klasördeki her
                    // Excel kodu tek bir ürünü/fotoğrafı temsil eder; bu yüzden aynı kod birden
                    // fazla görselde "eşleşti" çıkarsa (örn. dekoratif fontta bir hane yanlış
                    // okunup başka geçerli bir koda dönüşürse — "1379" -> "1349" gibi), bu bir
                    // OCR hatasının işaretidir, tesadüf değil.
                    // Görseller birbirinden bağımsızdır; havuz genişliğinde paralel taranır,
                    // sonuç dizisi indeksle doldurulduğu için sıra (rapor/atama) korunur.
                    var scanResults = new ScanResult[allFiles.Count];
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = ocrPool.Size,
                        CancellationToken = stoppingToken,
                    };
                    Parallel.For(0, allFiles.Count, parallelOptions, i =>
                    {
                        var imageTimer = Stopwatch.StartNew();
                        scanResults[i] = ocrPool.Run(o => o.FindProductCodes(allFiles[i], excelCodes));
                        _logger.LogInformation("OCR: {File} -> {Matches} eşleşme, {Candidates} aday ({Elapsed:N1} sn)",
                            Path.GetFileName(allFiles[i]), scanResults[i].Matches.Count, scanResults[i].Candidates.Count,
                            imageTimer.Elapsed.TotalSeconds);
                    });
                    var scans = allFiles.Select((file, i) => (File: file, Scan: scanResults[i])).ToList();

                    // Aşama 1.5: klasör markası tespiti — bir Gonderim klasörü her zaman TEK
                    // markadır (iş kuralı). Önce (varsa) kullanıcının WhatsApp marka cevabı,
                    // yoksa tüm görsellerin OCR token birleşimi denenir; ikisi de sonuç
                    // vermezse gönderene WhatsApp'tan marka sorulur ve klasör, bot cevabı
                    // marka_cevap.txt olarak yazana kadar bekletilir (islendi.txt yazılmaz).
                    var brandResolution = await ResolveFolderBrandAsync(
                        http, folder, senderPhone, Path.GetFileName(excelFile), ocrPool, scans, brandList, stoppingToken);
                    if (brandResolution is null) continue;
                    var (brand, brandSource) = brandResolution.Value;
                    _logger.LogInformation("Klasör markası: {Brand} (NetCarpan={Carpan}, kaynak: {Source})",
                        brand.FullName, brand.NetCarpan, brandSource);

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
                            // Okunan aday, Excel'de VAR ama fiyatı boş olduğu için yüklenmemiş bir koda
                            // denk geliyorsa bunu açıkça söyle — "OCR okuyamadı" ile "Excel'de fiyat eksik"
                            // teşhisleri operatör için tamamen farklı aksiyonlardır (gerçek vaka: 1311).
                            if (scan.Matches.Count == 0)
                            {
                                var priceless = scan.Candidates
                                    .Where(c => skippedExcelRows.Any(s => string.Equals(s.Code, c, StringComparison.OrdinalIgnoreCase)))
                                    .ToList();
                                if (priceless.Count > 0)
                                    reason += $". DİKKAT: kod(lar) {string.Join(", ", priceless)} Excel'de VAR ama fiyat hücresi boş/geçersiz olduğu için yüklenmemişti — Excel'de fiyat doldurulursa bu görsel işlenebilir";
                            }
                            _logger.LogWarning("'{File}' atlandı: {Reason}", fileName, reason);
                            imageResults.Add(new ImageResult(fileName, false, null, null, scan.Matches.Count, null, null, null, null, reason));
                            continue;
                        }

                        decimal priceExcel = excelPrices[best.Code];
                        decimal priceInTry = priceExcel * brand.NetCarpan;
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
                                priceExcel, priceInTry, priceInUsd, Path.GetFileName(stamped), null, best.IsFuzzy));
                            if (best.IsFuzzy)
                            {
                                _logger.LogWarning("'{File}' -> YAKLAŞIK (fuzzy) eşleşme, KONTROL ÖNERİLİR: kod {Code} (güven {Confidence:N0}) -> {PriceExcel:N2} × {Carpan} = {PriceTry:N2} TRY ({PriceUsd:N2} USD) -> {Output}",
                                    fileName, best.Code, best.Confidence, priceExcel, brand.NetCarpan, priceInTry, priceInUsd, Path.GetFileName(stamped));
                            }
                            else
                            {
                                _logger.LogInformation("Eşleşti: {File} -> kod {Code} (güven {Confidence:N0}) -> {PriceExcel:N2} × {Carpan} = {PriceTry:N2} TRY ({PriceUsd:N2} USD) -> {Output}",
                                    fileName, best.Code, best.Confidence, priceExcel, brand.NetCarpan, priceInTry, priceInUsd, Path.GetFileName(stamped));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "HATA (Resim basılamadı): {File}", fileName);
                            imageResults.Add(new ImageResult(fileName, true, best.Code, best.Confidence, scan.Matches.Count,
                                priceExcel, priceInTry, priceInUsd, null, $"Damgalama hatası: {ex.Message}", best.IsFuzzy));
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
                        brand, brandSource, allFiles.Count, imageResults, recipients, sendResults, processStart, processEnd, stopwatch.Elapsed);

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

    /// <summary>Klasörün markasını çözer. Öncelik sırası:
    /// 1) Bot'un yazdığı marka_cevap.txt (kullanıcının WhatsApp cevabı) — eşleşirse marka budur;
    ///    eşleşmezse cevap marka_cevap_red_*.txt olarak arşivlenir ve önerilerle tekrar sorulur.
    /// 2) Tüm görsellerin OCR token birleşimi (klasör tek marka olduğu için herhangi bir
    ///    görselden okunabilen logo yeterlidir).
    /// 3) İkisi de yoksa gönderene WhatsApp'tan marka sorulur; soru BAŞARIYLA gönderilirse
    ///    marka_sorusu.txt işaretçisi yazılır ve klasör cevaba kadar taramalarda atlanır.
    /// null dönerse klasör bu turda işlenmez (soru soruldu / cevap bekleniyor / soru gönderilemedi).</summary>
    private async Task<(BrandMultiplier Brand, string Source)?> ResolveFolderBrandAsync(
        HttpClient http,
        string folder,
        string senderPhone,
        string excelName,
        FullScanOcrPool ocrPool,
        List<(string File, ScanResult Scan)> scans,
        List<BrandMultiplier> brandList,
        CancellationToken ct)
    {
        var questionPath = Path.Combine(folder, "marka_sorusu.txt");
        var answerPath = Path.Combine(folder, "marka_cevap.txt");

        if (File.Exists(answerPath))
        {
            var answerText = File.ReadAllText(answerPath, Encoding.UTF8).Trim();
            var outcome = BrandMatcher.MatchFromUserText(answerText, brandList);
            if (outcome.Brand is not null)
            {
                var note = outcome.Approximate ? ", yazım farkıyla yaklaşık eşleşme" : "";
                return (outcome.Brand, $"kullanıcı cevabı ('{answerText}'{note})");
            }

            // Cevap listeyle eşleşmedi: aynı cevabı her turda tekrar işlememek için arşivle,
            // önerilerle birlikte tekrar sor. Eski soru işaretçisi de silinir ki tekrar-sorma
            // gönderimi başarısız olursa (örn. bot kapalı) klasör "cevap bekliyor" durumunda
            // kilitli kalmasın — işaretçisiz klasör bir sonraki turda baştan işlenir ve soru
            // (öneriler olmadan, genel hâliyle de olsa) yeniden gönderilmeye çalışılır.
            File.Move(answerPath, Path.Combine(folder, $"marka_cevap_red_{DateTime.Now:yyyyMMdd_HHmmss}.txt"), overwrite: true);
            File.Delete(questionPath);

            var suggestionText = outcome.Suggestions.Count > 0
                ? $" Şunlardan birini mi kastettiniz: {string.Join(", ", outcome.Suggestions)}?"
                : "";
            var retryQuestion = $"PriceBot: '{answerText}' marka listesinde bulunamadı.{suggestionText} Lütfen '{excelName}' listesindeki ürünler için markanın tam adını tek mesaj olarak tekrar yazınız.";
            _logger.LogWarning("Klasör {Folder}: marka cevabı '{Answer}' listeyle eşleşmedi, tekrar sorulacak. Öneriler: {Suggestions}",
                folder, answerText, outcome.Suggestions.Count == 0 ? "(yok)" : string.Join(", ", outcome.Suggestions));
            await SendBrandQuestionAsync(http, folder, senderPhone, questionPath, retryQuestion, ct);
            return null;
        }

        var unionTokens = new Dictionary<string, float>();
        void MergeTokens(IReadOnlyDictionary<string, float> tokens)
        {
            foreach (var (word, conf) in tokens)
                if (!unionTokens.TryGetValue(word, out var best) || conf > best)
                    unionTokens[word] = conf;
        }
        foreach (var (_, scan) in scans) MergeTokens(scan.Tokens);

        var ocrOutcome = BrandMatcher.MatchFromOcrTokens(unionTokens, brandList);

        // Kurtarma taraması: kod taramasının token'larında marka yoksa, her görselin üst
        // (logo) şeridi renk-duyarlı (min-RGB) ön işlemeyle yeniden OCR'lanır — renkli logo
        // harfleri (Baby Flamindo vakası) luminans gri tonlamada kayboluyor. Marka bulunur
        // bulunmaz durulur; maliyet sadece markasız kalan klasörlerde ödenir.
        if (ocrOutcome.Brand is null && ocrOutcome.AmbiguousNames.Count == 0)
        {
            _logger.LogInformation("Klasör {Folder}: kod taraması token'larında marka bulunamadı, renk-duyarlı (min-RGB) kurtarma taraması deneniyor.", folder);
            // İlk 4 görselle sınırlı: kurtarma görsel başına ~15-40 sn sürüyor ve 4 görselde
            // çıkmayan marka yazısının sonrakilerde çıkma olasılığı düşük — soru akışına
            // düşmeyi dakikalarca geciktirmeye değmez.
            // Görseller paralel taranır ve karar, token'ların BİRLEŞİMİ üzerinden bir kez
            // verilir (eski davranış sırayla tarayıp marka bulunur bulunmaz duruyordu;
            // paralelde erken çıkışın süre avantajı yok, birleşik karar ayrıca ilk görselden
            // tek marka bulup sonraki görsellerdeki farklı markayı hiç görmeme riskini de
            // kapatır — farklı çarpanlı çelişki çıkarsa kullanıcıya sorulur).
            var recoveryFiles = scans.Take(4).Select(s => s.File).ToList();
            var recoveredTokens = new Dictionary<string, float>[recoveryFiles.Count];
            var recoveryOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = ocrPool.Size,
                CancellationToken = ct,
            };
            Parallel.For(0, recoveryFiles.Count, recoveryOptions,
                i => recoveredTokens[i] = ocrPool.Run(o => o.CollectBrandTokens(recoveryFiles[i])));
            foreach (var tokens in recoveredTokens) MergeTokens(tokens);
            ocrOutcome = BrandMatcher.MatchFromOcrTokens(unionTokens, brandList);
        }

        if (ocrOutcome.Brand is not null)
        {
            var approxNote = ocrOutcome.Approximate ? ", KESİK/HATALI OKUMADAN yaklaşık eşleşme" : "";
            return (ocrOutcome.Brand, $"görsel OCR (kanıt: {string.Join(" ", ocrOutcome.MatchedWords)}{approxNote})");
        }

        if (ocrOutcome.AmbiguousNames.Count > 0)
        {
            _logger.LogWarning("Klasör {Folder}: OCR birden fazla çelişkili (farklı çarpanlı) marka buldu ({Brands}), kullanıcıya sorulacak.",
                folder, string.Join(", ", ocrOutcome.AmbiguousNames));
        }

        // Soru metnine Excel adı eklenir: çoklu marka gönderimi gruplara bölündüğünde aynı numaraya
        // birden fazla soru düşebilir; kullanıcı hangi sorunun hangi listeye ait olduğunu kendi
        // gönderdiği dosyanın adından ayırt eder. Bot, cevabı her zaman en eski bekleyen klasöre
        // yazdığı için soruların ve cevapların sırası eşleşir (bkz. bot_marka_cevap_gorevi.md).
        var question = "PriceBot: Gönderdiğiniz fotoğraflardaki ürünlerin markası otomatik tespit edilemedi " +
                       $"('{excelName}' listesindeki ürünler). Lütfen markanın tam adını tek mesaj olarak yazınız (örnek: LİLAX).";
        await SendBrandQuestionAsync(http, folder, senderPhone, questionPath, question, ct);
        return null;
    }

    /// <summary>Marka sorusunu gönderene iletir ve BAŞARILIYSA marka_sorusu.txt işaretçisini
    /// yazar — klasör, bot cevabı marka_cevap.txt olarak yazana kadar taramalarda atlanır.
    /// Gönderim başarısızsa (örn. bot kapalı) işaretçi yazılmaz; klasör bir sonraki turda
    /// baştan işlenir ve soru tekrar denenir.</summary>
    private async Task SendBrandQuestionAsync(HttpClient http, string folder, string senderPhone, string questionPath, string question, CancellationToken ct)
    {
        var result = await TrySendTextAsync(http, question, senderPhone, ct);
        if (!result.Success)
        {
            _logger.LogWarning("Klasör {Folder}: marka sorusu gönderilemedi ({Status}), bir sonraki turda tekrar denenecek.",
                folder, result.StatusInfo);
            return;
        }

        File.WriteAllText(questionPath,
            $"Gönderilme zamanı: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"Alıcı: {senderPhone}{Environment.NewLine}" +
            $"Soru: {question}{Environment.NewLine}",
            Encoding.UTF8);
        _logger.LogInformation("Klasör {Folder}: marka sorusu {Recipient} numarasına gönderildi, cevap bekleniyor.", folder, senderPhone);
    }

    /// <summary>Bot'a dosyasız düz metin mesaj gönderir (FilePath boş string). Bot tarafının
    /// boş FilePath'i "sadece metin gönder" olarak yorumlaması gerekir (bkz. CLAUDE.md'deki
    /// bot sözleşmesi).</summary>
    private async Task<SendResult> TrySendTextAsync(HttpClient http, string messageText, string recipient, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { ToNumber = recipient, MessageText = messageText, FilePath = "" });
        try
        {
            var resp = await http.PostAsync(BotSendUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
            _logger.LogInformation("Metin gönderimi: {Recipient} -> {Status}", recipient, resp.StatusCode);
            return new SendResult("(metin)", recipient, resp.IsSuccessStatusCode, ((int)resp.StatusCode) + " " + resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Metin gönderim hatası: {Recipient}", recipient);
            return new SendResult("(metin)", recipient, false, $"HATA: {ex.Message}");
        }
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

    /// <summary>Birden fazla fiyat listesi (Excel) içeren bir gönderim klasörünü marka gruplarına böler
    /// (süreç değişikliği 2026-07-28: birden fazla markanın fotoğraf + Excel'i aynı gönderimde
    /// gelebiliyor). Gruplama sıraya/zaman damgasına değil VERİYE dayanır: her görselin OCR'dan okunan
    /// ürün kodu hangi Excel'de bulunuyorsa görsel o Excel'in grubuna aittir. Her grup için telefon
    /// klasörü altında "&lt;orijinal&gt;_grupN" adlı KARDEŞ bir Gonderim klasörü oluşturulur (alt klasör
    /// değil — gönderen numara klasör yolundaki parent addan çıkarıldığı için grup klasörleri de aynı
    /// telefon klasörünün doğrudan çocuğu olmalıdır), Excel + görselleri oraya taşınır; bu klasörler
    /// sonraki turlarda normal tek-marka akışıyla (kendi marka tespiti, kendi soru/cevap döngüsü, kendi
    /// islendi.txt raporu) bağımsız işlenir. Kod tabanlı gruplama OCR gerektirdiği için görseller burada
    /// bir kez, grup klasöründe bir kez daha taranır — kabul edilmiş maliyet (paralel taramayla dakikalar
    /// düzeyinde); karşılığında tek-marka işleme akışına hiç dokunulmaz.
    ///
    /// Kodu okunamayan veya kodu birden fazla Excel'de bulunan (belirsiz) görseller ile hiç görseli
    /// olmayan Excel'ler orijinal klasörde bırakılır; orijinal klasöre islendi.txt olarak yazılan bölme
    /// raporu hepsini nedenleriyle listeler.
    ///
    /// Kısmi çökme güvenliği: her grupta önce Excel, sonra görseller taşınır. Yarıda kalırsa: orijinal
    /// klasörde tek Excel kaldıysa normal akışa düşer (taşınmış görseller zaten kendi grubunda işlenir);
    /// yalnız-Excel'li kalmış bir grup klasörünü de ana döngüdeki görselsiz-klasör koruması soru
    /// sormadan kapatır. Dosya taşıma LastWriteTime'ı koruduğu için grup klasörleri 60 sn'lik sessizlik
    /// kuralını beklemeden bir sonraki turda hazırdır.</summary>
    private void SplitMultiBrandFolder(string folder, List<string> excelFiles, FullScanOcrPool ocrPool, CancellationToken ct)
    {
        var folderName = Path.GetFileName(folder);
        var parentDir = Path.GetDirectoryName(folder)!;
        _logger.LogInformation("=== Bölünüyor: {Folder} — {Count} adet Excel bulundu (çoklu marka gönderimi) ===",
            folder, excelFiles.Count);

        // Kod -> hangi Excel'ler(de) geçiyor. Birden fazla Excel'de geçen kodlar grup ayrımı
        // için kullanılamaz (hangi markanın fiyat listesinden geldiği belirsiz). Fiyatı boş/
        // geçersiz olduğu için yüklenmeyen kodlar da ayrıca tutulur: görselden okunan bir kod
        // bu kümedeyse, "OCR okuyamadı" değil "Excel'de fiyat eksik" teşhisi raporlanabilir
        // (gerçek vaka: BOBİŞKO listesinde 1311'in fiyat hücresi boştu, fotoğrafı atlanmıştı).
        var codeOwners = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var pricelessCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // kod -> Excel adı
        for (int e = 0; e < excelFiles.Count; e++)
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(excelFiles[e], out var skippedRows);
            foreach (var code in prices.Keys)
            {
                if (!codeOwners.TryGetValue(code, out var owners))
                    codeOwners[code] = owners = [];
                owners.Add(e);
            }
            foreach (var (_, code, _) in skippedRows)
                if (!string.IsNullOrWhiteSpace(code) && !pricelessCodes.ContainsKey(code))
                    pricelessCodes[code] = Path.GetFileName(excelFiles[e]);
        }

        var sharedCodes = codeOwners.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();
        if (sharedCodes.Count > 0)
        {
            _logger.LogWarning("Klasör {Folder}: {Count} ürün kodu birden fazla Excel'de birden var ({Codes}) — bu kodlar grup ayrımında kullanılamayacak.",
                folder, sharedCodes.Count, string.Join(", ", sharedCodes.Take(10)));
        }

        var unionCodes = new HashSet<string>(codeOwners.Keys, StringComparer.OrdinalIgnoreCase);
        var images = Directory.EnumerateFiles(folder)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var scanResults = new ScanResult[images.Count];
        Parallel.For(0, images.Count,
            new ParallelOptions { MaxDegreeOfParallelism = ocrPool.Size, CancellationToken = ct },
            i => scanResults[i] = ocrPool.Run(o => o.FindProductCodes(images[i], unionCodes)));

        // Atama: görselin güvene göre sıralı eşleşmelerinden, TEK bir Excel'e ait ilk kod kazanır.
        // Aynı kodun birden fazla görselde çıkması burada sorun değil; kod/görsel çakışma çözümü
        // (greedy atama) her grubun kendi normal işlenmesinde zaten yapılır.
        var groupImages = excelFiles.Select(_ => new List<(string File, CodeMatch Match)>()).ToList();
        var unassigned = new List<(string File, string Reason)>();
        for (int i = 0; i < images.Count; i++)
        {
            var pick = scanResults[i].Matches.FirstOrDefault(m => codeOwners[m.Code].Count == 1);
            if (pick is not null)
            {
                groupImages[codeOwners[pick.Code][0]].Add((images[i], pick));
            }
            else if (scanResults[i].Matches.Count == 0)
            {
                var candidates = scanResults[i].Candidates;
                var reason = candidates.Count == 0
                    ? "görselden hiçbir sayısal kod adayı okunamadı"
                    : $"hiçbir Excel koduyla eşleşen ürün kodu okunamadı — okunan adaylar: {string.Join(", ", candidates.Take(10))}";
                var priceless = candidates.Where(pricelessCodes.ContainsKey).ToList();
                if (priceless.Count > 0)
                    reason += ". DİKKAT: " + string.Join("; ", priceless.Select(c =>
                        $"kod {c} '{pricelessCodes[c]}' listesinde VAR ama fiyat hücresi boş/geçersiz olduğu için yüklenmemişti")) +
                        " — Excel'de fiyat doldurulup klasör yeniden gönderilirse bu görsel işlenebilir";
                unassigned.Add((images[i], reason));
            }
            else
            {
                unassigned.Add((images[i],
                    $"okunan kod(lar) ({string.Join(", ", scanResults[i].Matches.Select(m => m.Code))}) birden fazla Excel'de bulunduğu için grup belirlenemedi"));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== PriceBot Bölme Raporu (çoklu marka gönderimi) ===");
        sb.AppendLine($"Klasör: {folder}");
        sb.AppendLine($"Zaman: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Bu gönderimde {excelFiles.Count} fiyat listesi (Excel) bulundu; görseller, OCR ile okunan ürün");
        sb.AppendLine("kodunun hangi listede yer aldığına göre gruplara ayrıldı. Her grup aşağıdaki kardeş klasörde");
        sb.AppendLine("normal akışla AYRI işlenecek ve kendi islendi.txt raporunu üretecek:");
        sb.AppendLine();

        for (int e = 0; e < excelFiles.Count; e++)
        {
            var excelName = Path.GetFileName(excelFiles[e]);
            if (groupImages[e].Count == 0)
            {
                sb.AppendLine($"[GRUPSUZ EXCEL] {excelName} -> bu listeye ait hiçbir görsel bulunamadı, dosya bu klasörde bırakıldı.");
                _logger.LogWarning("Klasör {Folder}: '{Excel}' listesine ait hiçbir görsel bulunamadı, Excel orijinal klasörde bırakıldı.",
                    folder, excelName);
                continue;
            }

            var groupFolder = Path.Combine(parentDir, $"{folderName}_grup{e + 1}");
            Directory.CreateDirectory(groupFolder);
            File.Move(excelFiles[e], Path.Combine(groupFolder, excelName));
            foreach (var (file, _) in groupImages[e])
                File.Move(file, Path.Combine(groupFolder, Path.GetFileName(file)));

            sb.AppendLine($"[GRUP {e + 1}] {excelName} + {groupImages[e].Count} görsel -> {Path.GetFileName(groupFolder)}");
            foreach (var (file, match) in groupImages[e])
                sb.AppendLine($"  {Path.GetFileName(file)} -> kod {match.Code} (güven {match.Confidence:N0}{(match.IsFuzzy ? ", fuzzy" : "")})");
            _logger.LogInformation("Klasör {Folder}: grup {Index} oluşturuldu — '{Excel}' + {Count} görsel -> {GroupFolder}",
                folder, e + 1, excelName, groupImages[e].Count, Path.GetFileName(groupFolder));
        }

        if (unassigned.Count > 0)
        {
            sb.AppendLine();
            foreach (var (file, reason) in unassigned)
                sb.AppendLine($"[ATANAMADI] {Path.GetFileName(file)} -> {reason} (bu klasörde kaldı, işlenmeyecek)");
            _logger.LogWarning("Klasör {Folder}: {Count} görsel hiçbir gruba atanamadı, orijinal klasörde bırakıldı (ayrıntı bölme raporunda).",
                folder, unassigned.Count);
        }

        if (sharedCodes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"UYARI: {sharedCodes.Count} ürün kodu birden fazla Excel'de birden bulunduğu için grup ayrımında kullanılamadı: {string.Join(", ", sharedCodes)}");
        }

        // islendi.txt orijinal klasörü kapatır: buradaki iş bölmeden ibaret, fiyatlama grup
        // klasörlerinde yapılacak. Atanamayan görseller bu raporla birlikte arşivlenmiş olur.
        File.WriteAllText(Path.Combine(folder, "islendi.txt"), sb.ToString(), Encoding.UTF8);
        _logger.LogInformation("=== Bölme tamamlandı: {Folder} — {Groups} grup, {Unassigned} atanamayan görsel. Gruplar sonraki turda işlenecek. ===",
            folder, groupImages.Count(g => g.Count > 0), unassigned.Count);
    }

    private static string BuildReport(
        string folder,
        string senderPhone,
        string excelFile,
        int excelProductCount,
        List<(int Row, string Code, string RawPrice)> skippedExcelRows,
        decimal rateValue,
        DateTime rateDate,
        BrandMultiplier brand,
        string brandSource,
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
        sb.AppendLine($"1 USD = {rateValue} TRY (kur tarihi: {rateDate:yyyy-MM-dd}, kaynak: Nebim AllExchangeRates, tip 6)");
        sb.AppendLine();

        sb.AppendLine("--- Marka ---");
        sb.AppendLine($"Marka: {brand.FullName} (önek: {brand.OnEk})");
        sb.AppendLine($"NetCarpan: {brand.NetCarpan}");
        sb.AppendLine($"Tespit kaynağı: {brandSource}");
        sb.AppendLine($"Fiyat formülü: Excel fiyatı × {brand.NetCarpan} ÷ {rateValue} = USD");
        sb.AppendLine();

        sb.AppendLine($"--- Görseller (toplam: {totalImages}, damgalanan: {matched.Count}, atlanan/hatalı: {skippedOrFailed.Count}) ---");
        foreach (var r in imageResults)
        {
            if (r.OutputFileName is not null)
            {
                var candidateNote = r.CandidateCount > 1 ? $", {r.CandidateCount} aday arasından seçildi" : "";
                var tag = r.IsFuzzy ? "[DAMGALANDI-FUZZY, KONTROL ÖNERİLİR]" : "[DAMGALANDI]";
                sb.AppendLine($"{tag} {r.FileName} -> kod {r.Code} (güven {r.Confidence:N0}{candidateNote}) -> {r.PriceExcel:N2} × {brand.NetCarpan} = {r.PriceTry:N2} TRY / {r.PriceUsd:N2} USD -> {r.OutputFileName}");
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
