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
    /// <summary>Tamamlanmış (islendi.txt yazılmış VE gonderim_bekleyen.json'u kalmamış) Gonderim
    /// klasörlerinin taşındığı arşiv kökü (2026-08-06). IncomingRoot aylar içinde sınırsız
    /// birikiyordu ve her 10 sn'lik tarama SearchOption.AllDirectories ile TÜM geçmişi (Islenmis/
    /// alt klasörleri dahil) yeniden dolaştığı için tur süresi zamanla doğrusal büyüyordu —
    /// bkz. ArchiveIfComplete. Telefon alt klasör yapısı korunur: Archive\<telefon>\<Gonderim_...>.</summary>
    private const string ArchiveRoot = @"C:\PriceBot\Archive";
    //private const string BotSendUrl = "http://localhost:3978/api/whatsapp/internal/send"; // Bot portu 3978'e eşitlendi!
    private const string BotSendUrl =  "https://asistyazilim.pakabulut.com:2304/api/whatsapp/internal/send";
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    /// <summary>Bir görsele damgalanan tek bir kod/fiyat satırı. Bir görselde birden fazla
    /// Excel koduna karşılık gelen aday bulunduğunda (ör. aynı görsel birden fazla yaş/beden
    /// grubunu temsil ediyorsa, 2026-08-xx vakası) artık en yüksek güvenli TEK aday seçilip
    /// diğerleri atılmıyor — hepsi ayrı ayrı fiyatlandırılıp görsele alt alta basılıyor.</summary>
    private sealed record StampedCode(string Code, double Confidence, bool IsFuzzy, decimal PriceExcel, decimal PriceTry, decimal PriceUsd);

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
        bool IsFuzzy = false,
        List<StampedCode>? AllCodes = null);

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
        // "Paddle" (varsayılan) veya "Tesseract" — bkz. OcrEngineFactory. Olumsuz geri dönüş
        // olursa appsettings.json'da "Tesseract" yazıp servisi yeniden başlatmak yeterlidir.
        var ocrEngineName = config["OcrEngine"]?.GetValue<string>() ?? "Paddle";
        // Test amaçlı: true iken islendi.txt raporunun (Gönderim bölümü çıkarılmış, "DAMGALANDI"
        // yerine müşteriye yönelik "ETİKETLİ" etiketiyle) bir kopyası gönderen numaraya WhatsApp
        // metni olarak da gönderilir. Yayına geçmeden önce appsettings.json'da false yapılmalı.
        var sendReportToCustomer = config["SendReportToCustomer"]?.GetValue<bool>() ?? false;

        using var http = new HttpClient();
        var rateProvider = new NebimRateProvider(nebimConnectionString, _logger);
        var brandProvider = new NebimBrandProvider(nebimConnectionString, _logger);

        // TesseractEngine thread-safe değil; görseller, başlangıçta bir kez kurulan sabit
        // boyutlu bir motor havuzuyla paralel taranır (motor kurulumu pahalı olduğu için
        // görsel başına değil, servis ömrü boyunca aynı örnekler kullanılır). Bir çekirdek
        // sistemin geri kalanına (bot, SQL) bırakılır; bellek için 6 örnekle sınırlanır.
        var ocrParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 6);
        // Alt sınır 3: bazı fiyat listeleri 3 haneli ürün kodu kullanıyor (gerçek vaka,
        // BABY Hi 2026-08-03: "473" gibi kodlar 4 haneli varsayımıyla aday listesine hiç
        // girmiyordu, Excel'de karşılığı olsa bile karşılaştırmaya ulaşamıyordu).
        using var ocrPool = OcrEngineFactory.Create(ocrEngineName, Path.Combine(appDir, "tessdata"), ocrParallelism);

        _logger.LogInformation("PriceBot Worker başladı. IncomingRoot={IncomingRoot} BotSendUrl={BotSendUrl} ExtraRecipients={ExtraRecipients} OcrEngine={OcrEngine} OcrParalel={OcrParallelism} SendReportToCustomer={SendReportToCustomer}",
            IncomingRoot, BotSendUrl, extraRecipients.Count == 0 ? "(boş)" : string.Join(", ", extraRecipients), ocrEngineName, ocrParallelism, sendReportToCustomer);

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
                // .xls (eski Excel 97-2003 ikili format) de kabul edilir — ExcelPriceReader, ClosedXML
                // dosyayı açamazsa (üretim vakası, 2026-08-03: müşteri .xls göndermişti) otomatik olarak
                // ExcelDataReader'a düşer; burada sadece klasörün "hazır" sayılıp taranmaya değer olması
                // için dosyanın var olması yeterli.
                static IEnumerable<string> RealXlsxFiles(string dir) =>
                    Directory.EnumerateFiles(dir)
                        .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                        .Where(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xls", StringComparison.OrdinalIgnoreCase));

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

                    // Başlık metni tek başına hangi sütunun ürün kodu olduğunu güvenilir biçimde
                    // söylemez (üretim vakaları: "KOD-B" + "BARKOD" aynı satırda, ya da kodun kendisi
                    // "MODEL" başlıklı bir sütunda basılı) — bu yüzden burada TÜM olası aday sütunlar
                    // yüklenir, hangisinin doğru olduğuna klasördeki görsellerin OCR kanıtına bakılarak
                    // (Aşama 1.5) karar verilir. OCR taraması için şimdilik adayların birleşimi kullanılır.
                    var codeColumnCandidates = ExcelPriceReader.LoadCandidateCodeColumns(excelFile);
                    if (codeColumnCandidates.Count == 0)
                    {
                        _logger.LogWarning("Klasör {Folder}: Excel'de olası bir ürün kodu sütunu bulunamadı, bu turda atlandı.", folder);
                        continue;
                    }
                    _logger.LogInformation("Excel'de {Count} olası kod sütunu bulundu: {Cols}", codeColumnCandidates.Count,
                        string.Join(" | ", codeColumnCandidates.Select(c => $"'{c.HeaderName}' ({c.Prices.Count} ürün)")));

                    var excelCodesUnion = new HashSet<string>(
                        codeColumnCandidates.SelectMany(c => c.Prices.Keys), StringComparer.OrdinalIgnoreCase);

                    // v11: kod -> Excel satırının diğer hücrelerinin ham metni (ör. "Malın Cinsi").
                    // FullScanOcr, çoklu-kod görsellerde fuzzy kurtarma belirsiz kaldığında (ör.
                    // ardışık kod bloklarının sınırında, 4224 vs 4227 gibi) görselin okuduğu yaş/
                    // beden aralığını bu metinle çapraz doğrulamak için kullanır. Hangi aday sütun
                    // kazanırsa kazansın aynı Excel satırından geldiği için birleşim güvenli.
                    var descriptionsUnion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var col in codeColumnCandidates)
                        foreach (var (code, desc) in col.Descriptions)
                            descriptionsUnion[code] = desc;

                    var rate = await rateProvider.GetUsdRateAsync(DateTime.Today);
                    if (rate is null)
                    {
                        _logger.LogWarning("USD kuru bulunamadı, klasör {Folder} bu turda atlandı, bir sonraki turda tekrar denenecek.", folder);
                        continue;
                    }
                    _logger.LogInformation("Kur: 1 USD = {Rate} TRY (kur tarihi: {RateDate:yyyy-MM-dd}, AllExchangeRates tip 6)", rate.Value.Rate, rate.Value.RateDate);

                    var brandLoad = await brandProvider.GetBrandMultipliersAsync();
                    var brandList = brandLoad.Brands;
                    var excludedBrands = brandLoad.Excluded;
                    if (brandList.Count == 0)
                    {
                        _logger.LogWarning("AS_PWB_MarkaCarpan boş döndü, klasör {Folder} bu turda atlandı, bir sonraki turda tekrar denenecek.", folder);
                        continue;
                    }

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
                        ArchiveIfComplete(folder);
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
                        scanResults[i] = ocrPool.Run(o => o.FindProductCodes(allFiles[i], excelCodesUnion, descriptionsUnion));
                        _logger.LogInformation("OCR: {File} -> {Matches} eşleşme, {Candidates} aday ({Elapsed:N1} sn)",
                            Path.GetFileName(allFiles[i]), scanResults[i].Matches.Count, scanResults[i].Candidates.Count,
                            imageTimer.Elapsed.TotalSeconds);
                    });
                    var scans = allFiles.Select((file, i) => (File: file, Scan: scanResults[i])).ToList();

                    // Aşama 1.4: birden fazla olası kod sütunu varsa (üstteki uyarı), hangisinin
                    // GERÇEK kod sütunu olduğuna başlık metniyle değil, klasördeki görsellerden fiilen
                    // OKUNAN kodların hangi sütunla örtüştüğüne bakarak karar verilir — marka
                    // tespitinin de OCR kanıtına dayanması gibi (bkz. Aşama 1.5). Her sütun, kendi
                    // kod kümesindeki bir değerin kaç görselde OCR eşleşmesi olarak çıktığı kadar oy
                    // alır; en çok oyu alan kazanır (eşitlikte başlık önceliği, sonra ürün sayısı).
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

                    var codeColumnSummary = codeColumnCandidates.Count <= 1
                        ? codeColumn.HeaderName
                        : $"{codeColumn.HeaderName} ({string.Join(", ", columnVotes.Select(kv => $"{kv.Key.HeaderName}={kv.Value} oy"))})";

                    if (codeColumnCandidates.Count > 1)
                    {
                        _logger.LogInformation(
                            "Kod sütunu seçildi: '{Header}' ({Votes} OCR eşleşmesi, {Total} olası sütun arasından: {AllVotes})",
                            codeColumn.HeaderName, columnVotes[codeColumn], codeColumnCandidates.Count,
                            string.Join(", ", columnVotes.Select(kv => $"'{kv.Key.HeaderName}'={kv.Value}")));
                    }

                    var excelPrices = codeColumn.Prices;
                    var skippedExcelRows = codeColumn.SkippedRows;
                    var excelCodes = new HashSet<string>(excelPrices.Keys, StringComparer.OrdinalIgnoreCase);
                    _logger.LogInformation("Excel'den {Count} adet ürün fiyatı hafızaya alındı ('{Header}' sütunu).", excelPrices.Count, codeColumn.HeaderName);
                    foreach (var (rowNum, code, rawPrice) in skippedExcelRows)
                    {
                        _logger.LogWarning("Excel satır {Row} atlandı: kod '{Code}', fiyat '{RawPrice}' sayıya çevrilemedi.",
                            rowNum, code, rawPrice);
                    }

                    // Kazanan sütunun dışındaki kodlara ait eşleşmeler artık geçersiz (union'dan
                    // geldikleri için scan.Matches'te hâlâ durabilirler) — sonraki aşamalar
                    // excelPrices[kod] araması yapacağı için burada elenmeleri şart.
                    scans = scans
                        .Select(s => (s.File, Scan: new ScanResult(
                            s.Scan.Matches.Where(m => excelCodes.Contains(m.Code)).ToList(),
                            s.Scan.Candidates,
                            s.Scan.Tokens)))
                        .ToList();

                    // Aşama 1.5: klasör markası tespiti — bir Gonderim klasörü her zaman TEK
                    // markadır (iş kuralı). Önce (varsa) kullanıcının WhatsApp marka cevabı,
                    // yoksa tüm görsellerin OCR token birleşimi denenir; ikisi de sonuç
                    // vermezse gönderene WhatsApp'tan marka sorulur ve klasör, bot cevabı
                    // marka_cevap.txt olarak yazana kadar bekletilir (islendi.txt yazılmaz).
                    var brandResolution = await ResolveFolderBrandAsync(
                        http, folder, senderPhone, Path.GetFileName(excelFile), ocrPool, scans, brandList, excludedBrands, stoppingToken);
                    if (brandResolution is null) continue;
                    var (brand, brandSource) = brandResolution.Value;
                    _logger.LogInformation("Klasör markası: {Brand} (NetCarpan={Carpan}, kaynak: {Source})",
                        brand.FullName, brand.NetCarpan, brandSource);

                    // Aşama 2: her görsel KENDİ OCR sonucundan bağımsız karar verir. Aynı ürün kodu
                    // birden fazla görselde çıkabilir ve bu MEŞRUDİR (ör. aynı ürünün önden/arkadan
                    // iki fotoğrafı, ya da aynı modelin renk varyantları aynı kodu paylaşıyorsa) —
                    // hepsi aynı fiyatla damgalanmalı. Eskiden klasör genelinde bir kodu "tüketen" aç
                    // gözlü (greedy) atama vardı; üretim vakası (2026-08-03) bunun aynı kodun tekrar
                    // ettiği MEŞRU durumlarda ikinci görseli gerekçesiz atladığını gösterdi, o yüzden
                    // kaldırıldı. Bir görselin kendi OCR'ı birden fazla aday bulduysa (ör. aynı görsel
                    // birden fazla yaş/beden grubunu temsil ediyorsa) artık TEK bir "en yüksek güvenli"
                    // aday seçilip diğerleri atılmıyor — hepsi ayrı satır olarak damgalanıyor (bkz.
                    // StampedCode). MatchExact zaten sadece Excel'de gerçekten var olan kodları
                    // döndürdüğü için (extract edilen rastgele bir rakam dizisi değil), listedeki
                    // her aday güvenilir bir Excel eşleşmesidir.
                    var chosen = new Dictionary<int, List<CodeMatch>>();
                    for (int idx = 0; idx < scans.Count; idx++)
                    {
                        if (scans[idx].Scan.Matches.Count > 0)
                            chosen[idx] = scans[idx].Scan.Matches;
                    }

                    // Aşama 3: sonuçlara göre damgala/gönder.
                    for (int idx = 0; idx < scans.Count; idx++)
                    {
                        var (file, scan) = scans[idx];
                        var fileName = Path.GetFileName(file);

                        if (!chosen.TryGetValue(idx, out var matches))
                        {
                            // Adayların rapora yazılması teşhis için kritik: "OCR mi okuyamadı,
                            // Excel'de mi yoktu" sorusu islendi.txt'ye bakarak tek seferde cevaplanır
                            // (Bebly 20020 vakasında bu bilgi olmadığı için teşhis loglardan yapılamamıştı).
                            string reason = scan.Candidates.Count == 0
                                ? "Görselden hiçbir sayısal kod adayı okunamadı"
                                : $"Excel'deki kodlardan biriyle eşleşen bir ürün kodu bulunamadı — OCR'ın okuduğu adaylar: {string.Join(", ", scan.Candidates)}";
                            // Okunan aday, Excel'de VAR ama fiyatı boş olduğu için yüklenmemiş bir koda
                            // denk geliyorsa bunu açıkça söyle — "OCR okuyamadı" ile "Excel'de fiyat eksik"
                            // teşhisleri operatör için tamamen farklı aksiyonlardır (gerçek vaka: 1311).
                            var priceless = scan.Candidates
                                .Where(c => skippedExcelRows.Any(s => string.Equals(s.Code, c, StringComparison.OrdinalIgnoreCase)))
                                .ToList();
                            if (priceless.Count > 0)
                                reason += $". DİKKAT: kod(lar) {string.Join(", ", priceless)} Excel'de VAR ama fiyat hücresi boş/geçersiz olduğu için yüklenmemişti — Excel'de fiyat doldurulursa bu görsel işlenebilir";
                            _logger.LogWarning("'{File}' atlandı: {Reason}", fileName, reason);
                            imageResults.Add(new ImageResult(fileName, false, null, null, scan.Matches.Count, null, null, null, null, reason));
                            continue;
                        }

                        var stampedCodes = matches.Select(m =>
                        {
                            decimal priceExcel = excelPrices[m.Code];
                            decimal priceInTry = priceExcel * brand.NetCarpan;
                            decimal priceInUsd = priceInTry / rate.Value.Rate;
                            return new StampedCode(m.Code, m.Confidence, m.IsFuzzy, priceExcel, priceInTry, priceInUsd);
                        }).ToList();
                        var best = stampedCodes[0];

                        if (stampedCodes.Count > 1)
                        {
                            _logger.LogInformation("'{File}' içinde {Count} farklı kod bulundu, hepsi damgalanıyor: {Codes}",
                                fileName, stampedCodes.Count,
                                string.Join(", ", stampedCodes.Select(s => $"{s.Code} (${s.PriceUsd:N2}, güven {s.Confidence:N0})")));
                        }

                        try
                        {
                            var stamped = PriceStamper.Stamp(file, outputDir, stampedCodes.Select(s => (s.Code, s.PriceUsd)).ToList());
                            stampedFiles.Add(stamped);
                            imageResults.Add(new ImageResult(fileName, true, best.Code, best.Confidence, scan.Matches.Count,
                                best.PriceExcel, best.PriceTry, best.PriceUsd, Path.GetFileName(stamped), null, best.IsFuzzy, stampedCodes));
                            if (best.IsFuzzy)
                            {
                                _logger.LogWarning("'{File}' -> YAKLAŞIK (fuzzy) eşleşme, KONTROL ÖNERİLİR: kod {Code} (güven {Confidence:N0}) -> {PriceExcel:N2} × {Carpan} = {PriceTry:N2} TRY ({PriceUsd:N2} USD) -> {Output}",
                                    fileName, best.Code, best.Confidence, best.PriceExcel, brand.NetCarpan, best.PriceTry, best.PriceUsd, Path.GetFileName(stamped));
                            }
                            else if (stampedCodes.Count == 1)
                            {
                                _logger.LogInformation("Eşleşti: {File} -> kod {Code} (güven {Confidence:N0}) -> {PriceExcel:N2} × {Carpan} = {PriceTry:N2} TRY ({PriceUsd:N2} USD) -> {Output}",
                                    fileName, best.Code, best.Confidence, best.PriceExcel, brand.NetCarpan, best.PriceTry, best.PriceUsd, Path.GetFileName(stamped));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "HATA (Resim basılamadı): {File}", fileName);
                            imageResults.Add(new ImageResult(fileName, true, best.Code, best.Confidence, scan.Matches.Count,
                                best.PriceExcel, best.PriceTry, best.PriceUsd, null, $"Damgalama hatası: {ex.Message}", best.IsFuzzy, stampedCodes));
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

                    var report = BuildReport(folder, senderPhone, excelFile, excelPrices.Count, codeColumnSummary, skippedExcelRows, rate.Value.Rate, rate.Value.RateDate,
                        brand, brandSource, allFiles.Count, imageResults, recipients, sendResults, processStart, processEnd, stopwatch.Elapsed);

                    // islendi.txt, OCR/eşleştirme/damgalama işinin TAMAMLANDIĞINI işaretler (bu iş
                    // pahalı ve idempotent değildir, tekrarlanmamalı). Gönderim başarısız olan
                    // (dosya, alıcı) çiftleri varsa ayrı bir bekleyen-gönderim dosyasına yazılır;
                    // worker her turda bu dosyayı görüp SADECE o çiftleri tekrar dener — böylece
                    // bot kapalıyken biten bir tur mesajı sessizce kaybetmez, ama zaten başarılı
                    // olmuş alıcılara ikinci kez göndermez.
                    File.WriteAllText(Path.Combine(folder, "islendi.txt"), report, Encoding.UTF8);

                    if (sendReportToCustomer)
                    {
                        var customerReport = BuildCustomerFacingReport(report);
                        var reportSend = await TrySendTextAsync(http, customerReport, senderPhone, stoppingToken);
                        if (!reportSend.Success)
                        {
                            _logger.LogWarning("Rapor metni gönderilemedi: {Recipient} -> {StatusInfo} (test amaçlı özellik, gönderim/arşivleme akışını etkilemez)",
                                senderPhone, reportSend.StatusInfo);
                        }
                    }

                    if (stillPending.Count > 0)
                    {
                        WritePendingSends(folder, stillPending);
                        _logger.LogWarning("{Count} gönderim başarısız oldu, klasör {Folder} için 'gonderim_bekleyen.json' yazıldı — bir sonraki turda sadece bu gönderimler tekrar denenecek.",
                            stillPending.Count, folder);
                    }
                    else
                    {
                        // Tüm gönderimler ilk turda başarılı oldu — gonderim_bekleyen.json hiç
                        // yazılmadı, klasör hemen arşivlenebilir. stillPending.Count > 0 ise
                        // arşivleme RetryPendingSendsAsync tarafındaki bekleyenler tükenince yapılır.
                        ArchiveIfComplete(folder);
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
        OcrEnginePool ocrPool,
        List<(string File, ScanResult Scan)> scans,
        List<BrandMultiplier> brandList,
        List<BrandMultiplier> excludedBrands,
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

        // NetCarpan <= 0 olduğu için elenmiş markalar (bkz. NebimBrandProvider) her yükleme
        // turunda değil, sadece burada OCR fiilen bu markayı yakalarsa bilgi amaçlı loglanır.
        if (excludedBrands.Count > 0)
        {
            var excludedOutcome = BrandMatcher.MatchFromOcrTokens(unionTokens, excludedBrands);
            if (excludedOutcome.Brand is not null)
            {
                _logger.LogInformation(
                    "Klasör {Folder}: OCR '{Brand}' markasını tespit etti ama NetCarpan={Carpan} (<= 0, veri hatası) olduğu için eşleştirme dışı bırakıldı — Nebim tarafında düzeltilmeli.",
                    folder, excludedOutcome.Brand.FullName, excludedOutcome.Brand.NetCarpan);
            }
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
        // Kesin/yaklaşık eşleşme yok, ama OCR token'ları listedeki bazı markalara yakın
        // düşüyor olabilir (kesik/hatalı okuma) — kullanıcıya ilk soruda "tahmin" olarak
        // sunulur, doğruysa bir tur (retry sorusu) atlanmış olur.
        var firstGuesses = BrandMatcher.SuggestBrandsFromOcrTokens(unionTokens, brandList);
        var guessText = firstGuesses.Count > 0
            ? $" Şunlardan biri olabilir mi: {string.Join(", ", firstGuesses)}?"
            : "";
        var question = "PriceBot: Gönderdiğiniz fotoğraflardaki ürünlerin markası otomatik tespit edilemedi " +
                       $"('{excelName}' listesindeki ürünler).{guessText} Değilse markanın tam adını tek mesaj olarak yazınız (örnek: LİLAX).";
        if (firstGuesses.Count > 0)
            _logger.LogInformation("Klasör {Folder}: marka bulunamadı, ilk soruya OCR-yakınlık tahminleri eklendi: {Guesses}",
                folder, string.Join(", ", firstGuesses));
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

        if (stillPending.Count == 0)
        {
            File.Delete(path);
            ArchiveIfComplete(folder);
        }
        else WritePendingSends(folder, stillPending);
    }

    /// <summary>Klasör "tamamlanmış" ise (islendi.txt var VE gonderim_bekleyen.json yok — yani
    /// hem OCR/damgalama hem de tüm gönderimler bitmiş) IncomingRoot dışına, ArchiveRoot altına
    /// aynı telefon alt klasör yapısı korunarak taşır (bkz. ArchiveRoot yorumu). Taşıma başarısız
    /// olursa (ör. dosya o an kilitliyse) klasör olduğu yerde kalır — işlevsel bir sorun değil,
    /// islendi.txt zaten yeniden işlenmeyi engelliyor; sadece bir sonraki tarama biraz daha yavaş
    /// olur. Bu metodun kendisi tarama yapmaz, sadece işi biten TEK bir klasörü taşır — çağıran,
    /// bir klasörün işinin (gönderimler dahil) o an gerçekten bittiği noktalarda çağırmalı.</summary>
    private void ArchiveIfComplete(string folder)
    {
        try
        {
            if (!File.Exists(Path.Combine(folder, "islendi.txt"))) return;
            if (File.Exists(PendingSendsPath(folder))) return;

            var phone = new DirectoryInfo(folder).Parent!.Name;
            var target = Path.Combine(ArchiveRoot, phone, Path.GetFileName(folder));
            if (Directory.Exists(target))
            {
                // Pratikte olmamalı (klasör adları GUID/zaman damgası içerir, _grupN son ekleri de
                // orijinal ad üzerinden benzersizdir) ama sessiz veri kaybı yaşanmasın diye
                // zaman damgalı bir yedek adla devam edilir.
                target += $"_{DateTime.Now:yyyyMMdd_HHmmss}";
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(folder, target);
            _logger.LogInformation("Klasör arşivlendi: {Folder} -> {Target}", folder, target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Klasör {Folder} arşivlenemedi (IncomingRoot'ta kalacak, işlevsel bir sorun değil).", folder);
        }
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
    private void SplitMultiBrandFolder(string folder, List<string> excelFiles, OcrEnginePool ocrPool, CancellationToken ct)
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
        // Bölme burada gönderim içermez (grup klasörleri kendi akışında gönderir), yani bu
        // orijinal konteyner klasör için "iş bitti" = islendi.txt yazıldı; hemen arşivlenebilir.
        ArchiveIfComplete(folder);
        _logger.LogInformation("=== Bölme tamamlandı: {Folder} — {Groups} grup, {Unassigned} atanamayan görsel. Gruplar sonraki turda işlenecek. ===",
            folder, groupImages.Count(g => g.Count > 0), unassigned.Count);
    }

    /// <summary>islendi.txt raporundan müşteriye WhatsApp metni olarak gönderilecek sürümü türetir
    /// (SendReportToCustomer=true iken). "--- Gönderim ---" bölümü (alıcı numaraları + HTTP durum
    /// kodları gibi iç detaylar) çıkarılır ve iç kullanım etiketi "DAMGALANDI" müşteriye yönelik
    /// "ETİKETLİ" ile değiştirilir ("DAMGALANDI-FUZZY, KONTROL ÖNERİLİR" -> "ETİKETLİ-FUZZY, KONTROL
    /// ÖNERİLİR" olur, kasıtlı). islendi.txt dosyasının kendisi bu fonksiyondan etkilenmez.</summary>
    private static string BuildCustomerFacingReport(string fullReport)
    {
        var start = fullReport.IndexOf("--- Gönderim (al", StringComparison.Ordinal);
        var end = fullReport.IndexOf("--- Özet ---", StringComparison.Ordinal);
        var trimmed = (start >= 0 && end > start)
            ? fullReport.Remove(start, end - start)
            : fullReport;
        return trimmed.Replace("DAMGALANDI", "ETİKETLİ", StringComparison.Ordinal);
    }

    private static string BuildReport(
        string folder,
        string senderPhone,
        string excelFile,
        int excelProductCount,
        string codeColumnSummary,
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
        sb.AppendLine($"Kod sütunu: {codeColumnSummary}");
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
                var tag = r.IsFuzzy ? "[DAMGALANDI-FUZZY, KONTROL ÖNERİLİR]" : "[DAMGALANDI]";
                if (r.AllCodes is { Count: > 1 })
                {
                    // Görselde Excel'in birden fazla koduyla eşleşen aday bulundu (ör. tek görsel
                    // birden fazla yaş/beden grubunu temsil ediyor) — hepsi ayrı satır olarak
                    // basıldı, rapor da hepsini ayrı ayrı listeler.
                    sb.AppendLine($"{tag} {r.FileName} -> {r.AllCodes.Count} farklı kod aynı görselde bulundu, hepsi basıldı -> {r.OutputFileName}");
                    foreach (var s in r.AllCodes)
                        sb.AppendLine($"    kod {s.Code} (güven {s.Confidence:N0}{(s.IsFuzzy ? ", fuzzy" : "")}) -> {s.PriceExcel:N2} × {brand.NetCarpan} = {s.PriceTry:N2} TRY / {s.PriceUsd:N2} USD");
                }
                else
                {
                    var candidateNote = r.CandidateCount > 1 ? $", {r.CandidateCount} aday arasından seçildi" : "";
                    sb.AppendLine($"{tag} {r.FileName} -> kod {r.Code} (güven {r.Confidence:N0}{candidateNote}) -> {r.PriceExcel:N2} × {brand.NetCarpan} = {r.PriceTry:N2} TRY / {r.PriceUsd:N2} USD -> {r.OutputFileName}");
                }
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
