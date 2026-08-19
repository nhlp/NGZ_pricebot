using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    /// <summary>Marka seçim listesinde (WhatsApp interaktif liste mesajı — bkz. bot_marka_secim_gorevi.md,
    /// 2026-08-08) her zaman son satır olarak eklenir. Kullanıcı bunu seçerse bot, cevabı olduğu gibi
    /// (bu sabit metin) marka_cevap.txt'ye yazar; bu metin HİÇBİR markayla eşleşmeyeceği için (bilinçli
    /// olarak — BrandMatcher'a dokunmadan) mevcut "eşleşmedi -> tekrar sor" akışına düşer, ama
    /// ResolveFolderBrandAsync bu özel metni tanıyıp tekrar soruyu listesiz/doğrudan serbest-metin
    /// olarak sorar (kullanıcı zaten listeyi reddetmiş, tekrar liste sunmak gereksiz).</summary>
    private const string OtherBrandOptionText = "Diğer (markayı kendim yazacağım)";

    /// <summary>Gonderim klasör adının başındaki zaman damgasını yakalar (bkz. "Klasör/dosya
    /// adlandırma sözleşmesi" — Gonderim_yyyyMMdd_HHmmss_guid; çoklu marka bölmesinde _grupN son
    /// eki eklenir ama bu damga korunur). Bkz. GetFolderCreatedAt.</summary>
    private static readonly Regex FolderTimestampRegex = new(@"^Gonderim_(\d{8})_(\d{6})", RegexOptions.Compiled);

    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    /// <summary>Bir görsele damgalanan tek bir kod/fiyat satırı. Bir görselde birden fazla
    /// Excel koduna karşılık gelen aday bulunduğunda (ör. aynı görsel birden fazla yaş/beden
    /// grubunu temsil ediyorsa, 2026-08-xx vakası) artık en yüksek güvenli TEK aday seçilip
    /// diğerleri atılmıyor — hepsi ayrı ayrı fiyatlandırılıp görsele alt alta basılıyor.</summary>
    private sealed record StampedCode(string Code, double Confidence, bool IsFuzzy, decimal PriceExcel, decimal PriceTry, decimal PriceUsd, string Source = "OCR");

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
        // Test amaçlı: true iken islendi.txt raporunun (Gönderim bölümü çıkarılmış, "DAMGALANDI"
        // yerine müşteriye yönelik "ETİKETLİ" etiketiyle) bir kopyası gönderen numaraya WhatsApp
        // metni olarak da gönderilir; aynı içerik WhatsApp gönderiminden bağımsız olarak (gönderim
        // başarısız olsa bile) klasöre "musteri_raporu.txt" olarak da yazılır (2026-08-10) — kalıcı
        // kayıt için. Yayına geçmeden önce appsettings.json'da false yapılmalı.
        var sendReportToCustomer = config["SendReportToCustomer"]?.GetValue<bool>() ?? false;

        // İYİ KOMŞU AYARI (2026-08-09): sunucu paylaşımlı (SQL Server + IIS + RDP aynı makinede) ve
        // 65 görsellik gibi büyük klasörler CPU'yu üretimde canlı olarak %96-100'e çıkarabiliyor.
        // Bu tek başına sunucuyu ÇÖKERTMEZ (Windows'ta CPU doygunluğu "yumuşak" bir yavaşlamadır,
        // bellek tükenmesi gibi "sert" bir kararsızlık değil) ama paylaşılan SQL Server sorgularını/
        // bot'un IIS isteklerini geciktirebilir. Process önceliğini BelowNormal'a çekmek, Windows
        // zamanlayıcısına "boştaysan istediğin kadar CPU kullan, ama SQL/IIS gerçekten CPU isterse
        // önce ona ver" dedirtiyor — iş boşta değilken hiçbir verim kaybı yok, sadece gerçek çakışma
        // anında geri çekiliyor. appsettings.json'da "OcrProcessPriority" ile ("Normal", "High" vb.
        // — bkz. System.Diagnostics.ProcessPriorityClass) override edilebilir; geçersiz/eksikse
        // BelowNormal varsayılan.
        var priorityConfigValue = config["OcrProcessPriority"]?.GetValue<string>();
        var ocrProcessPriority = Enum.TryParse<ProcessPriorityClass>(priorityConfigValue, ignoreCase: true, out var parsedPriority)
            ? parsedPriority
            : ProcessPriorityClass.BelowNormal;
        try
        {
            Process.GetCurrentProcess().PriorityClass = ocrProcessPriority;
            _logger.LogInformation("Process önceliği ayarlandı: {Priority}", ocrProcessPriority);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process önceliği ayarlanamadı (varsayılan Normal ile devam ediliyor) — zararsız.");
        }

        using var http = new HttpClient();
        var rateProvider = new NebimRateProvider(nebimConnectionString, _logger);
        var brandProvider = new NebimBrandProvider(nebimConnectionString, _logger);

        // Görü tabanlı marka tespiti fallback'i (2026-08-10) — bkz. GeminiBrandClassifier.cs
        // dosya başı yorumu. GeminiApiKey boşsa (varsayılan) tamamen kapalı, hiçbir davranış
        // değişmez; ResolveFolderBrandAsync'e OcrEnginePool gibi hep (null kontrolü olmadan)
        // geçirilir.
        var geminiApiKey = config["GeminiApiKey"]?.GetValue<string>() ?? "";
        var geminiModel = config["GeminiBrandModel"]?.GetValue<string>() ?? "gemini-flash-latest";
        var geminiFallbackModel = config["GeminiBrandModelFallback"]?.GetValue<string>() ?? "gemini-flash-lite-latest";
        var geminiClassifier = new GeminiVisionClassifier(geminiApiKey, geminiModel, geminiFallbackModel, _logger);

        // Kod tespiti için çoklu-sağlayıcı zinciri (2026-08-13, kullanıcı isteği: "gemini sınırını
        // başka bir api kullanarak aşabilir miyiz" — gerçek vaka: NGZ/MİNİCE klasöründe Gemini'nin
        // ücretsiz katman kotası tek bir klasörde tükenmişti, bkz. GeminiVisionClassifier.cs dosya
        // başı "429 (KOTA)" notu). ÜÇ ek, opsiyonel basamak — hepsi appsettings.json'dan boş
        // bırakılırsa (varsayılan) tamamen kapalı, hiçbir davranış değişmez:
        // 1. GeminiApiKeySecondary — Google Cloud'da AYRI bir proje/key (ücretsiz, GCP'nin meşru
        //    çok-proje mekanizması — aynı hesapta birden fazla proje her biri kendi kotasına sahip
        //    olabilir). Aynı GeminiVisionClassifier sınıfının İKİNCİ bir örneği, farklı apiKey ile.
        // 2. GroqApiKey — Groq (bkz. GroqVisionClassifier.cs dosya başı yorumu; "Gro-Q", Google'ın
        //    Gemini'siyle İLGİSİZ AYRI bir şirket/kota havuzu — Elon Musk'ın "Gro-K" (xAI) ile de
        //    KARIŞTIRILMAMALI, 2026-08-13'te kullanıcı bu ikisini karıştırıp yanlışlıkla x.ai'den
        //    ücretli bir key almıştı). Ücretsiz katman, Gemini'nin İKİ key'i de tükendiğinde devreye
        //    girer.
        // 3. AnthropicApiKey — Claude (bkz. AnthropicVisionClassifier.cs dosya başı yorumu),
        //    yukarıdaki ÜÇ (ücretsiz) basamak da tükendiğinde devreye giren PARALI son çare.
        // codeClassifiers SIRAYLA (ucuz/ücretsiz önce) denenir — bkz. Aşama 3'teki döngü ve
        // IProductCodeClassifier.cs dosya başı yorumu.
        var geminiApiKeySecondary = config["GeminiApiKeySecondary"]?.GetValue<string>() ?? "";
        var geminiClassifierSecondary = new GeminiVisionClassifier(geminiApiKeySecondary, geminiModel, geminiFallbackModel, _logger);
        var groqApiKey = config["GroqApiKey"]?.GetValue<string>() ?? "";
        var groqModel = config["GroqModel"]?.GetValue<string>() ?? "qwen/qwen3.6-27b";
        var groqClassifier = new GroqVisionClassifier(groqApiKey, groqModel, _logger);
        var anthropicApiKey = config["AnthropicApiKey"]?.GetValue<string>() ?? "";
        var anthropicModel = config["AnthropicModel"]?.GetValue<string>() ?? "claude-haiku-4-5-20251001";
        var anthropicClassifier = new AnthropicVisionClassifier(anthropicApiKey, anthropicModel, _logger);
        var codeClassifiers = new List<(string Source, IProductCodeClassifier Classifier)>
        {
            ("Gemini görü tespiti", geminiClassifier),
            ("Gemini görü tespiti (ikincil key)", geminiClassifierSecondary),
            ("Groq görü tespiti", groqClassifier),
            ("Claude görü tespiti", anthropicClassifier),
        };

        // PaddleOcrAll'ın altındaki native motor thread-affinity gerektirdiği için görseller
        // QueuedPaddleOcrAll'ın adanmış thread'leri üzerinden paralel taranır (bkz.
        // PaddleScanOcr.cs). Motor kurulumu pahalı olduğu için görsel başına değil, servis
        // ömrü boyunca aynı örnekler kullanılır.
        //
        // 2026-08-08 DÜŞÜRÜLDÜ: eski `ProcessorCount-1` (üst sınır 6) formülü, sunucunun 8 vCPU
        // olduğu varsayımıyla kalibre edilmişti (CLAUDE.md, 2026-07-28 teyitli) — ama üretim
        // sunucusu gerçekte 6 çekirdekli ve SQL Server + IIS ile PAYLAŞILIYOR (canlı Görev
        // Yöneticisi'nde teyit edildi, 2026-08-08). 6 çekirdekte eski formül 5 adanmış Paddle
        // thread'i açıp diğer servislere tek çekirdek bırakıyordu; her adanmış örnek kendi
        // model+kernel-cache belleğini taşıdığı için (bkz. PaddleScanOcr.cs bellek notu) bu aynı
        // zamanda servisi yeniden 23 GB/%95 belleğe çıkardı. Yeni varsayılan çekirdeklerin
        // YARISINI (üst sınır 3) kullanır, diğer yarısını SQL/IIS/OS'a bırakır. Rebuild
        // gerektirmeden ince ayar için appsettings.json'a "OcrParallelism" / "OcrCacheCapacity"
        // eklenebilir (sadece servis yeniden başlatılması yeterli).
        var defaultOcrParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 3);
        var ocrParallelism = config["OcrParallelism"]?.GetValue<int>() ?? defaultOcrParallelism;
        var ocrCacheCapacity = config["OcrCacheCapacity"]?.GetValue<int>() ?? 4;
        // PERİYODİK YENİLEME (2026-08-08): parallelism/cacheCapacity düşürülmesine rağmen üretimde
        // bellek büyümeye devam ediyor (6.5GB -> 8.3GB, aynı restart içinde) — bu artık oversubscription
        // değil, PaddleInference'ın kendi native belleği (Scope tabanlı ayırıcı, en büyük görülen
        // tensör/batch boyutuna göre "yüksek su işareti" tutuyor, otomatik küçülmüyor; bkz.
        // `PaddlePredictor.TryShrinkMemory`/`ClearIntermediateTensor` kütüphanede var ama
        // Sdcb.PaddleOCR bunları sarmalayıp DIŞARI vermiyor — `PaddleOcrDetector/Recognizer/
        // Classifier._p` private). Kütüphaneyi yamalayamadığımız için elimizdeki tek temiz araç,
        // tüm havuzu (QueuedPaddleOcrAll + altındaki native PaddlePredictor'lar dahil) periyodik
        // olarak Dispose edip SIFIRDAN kurmak — native tarafın tamamen yıkılıp yeniden kurulması
        // "yüksek su işaretini" native düzeyde gerçekten sıfırlar. Yenileme SADECE dış while
        // döngüsünün başında (bir klasör turu bitmişken, hiçbir Parallel.For çalışmıyorken) olur,
        // bu yüzden thread-safety sorunu yok. Maliyet düşük — dev makinede ölçüldü: OS dosya cache'i
        // ısındıktan sonra 3 örneği paralel kurmak ~2 sn (ilk/soğuk kurulum ~9 sn), 10 sn'lik tarama
        // döngüsü içinde ihmal edilebilir. İki bağımsız tetikleyici var (biri yeter):
        // (1) OcrRecycleHours süresi dolması, (2) OcrRecycleMemoryMb eşiği — Task Manager'da
        //     görülenle AYNI metrik (Process.WorkingSet64), büyüme hızı öngörülenden farklı çıkarsa
        //     zamanlayıcıyı beklemeden tepki verir — bu ASIL güvenlik ağı. İkisi de
        //     appsettings.json'dan ayarlanabilir (rebuild gerekmez); <= 0 verilirse ilgili tetikleyici
        //     kapanır.
        // OcrRecycleHours varsayılanı 2026-08-11'de 1 saatten 6 saate çıkarıldı: üretimde 9,5 saat
        // boyunca (10 ardışık saatlik yenileme logu) çalışma belleği hep 1,2-1,6 GB bandında kaldı,
        // 2048 MB eşiğine hiç yaklaşmadı — yani zamanlayıcı tetiklendiğinde bellek tetikleyicisi zaten
        // hiçbir zaman devrede olmadı, saatlik yenilemenin fiilen bir işe yaramadığı (sadece log
        // satırı + ~2-9 sn'lik gereksiz Dispose/rebuild) gözlemlendi. Bu, 2026-08-08 parallelism/
        // cacheCapacity düzeltmesinin (bkz. [[project_ocr_parallelism_memory_fix]]) native bellek
        // büyümesini gerçekten durdurduğunu doğruluyor. Zamanlayıcı TAMAMEN kaldırılmadı (0 yapılmadı)
        // — birkaç günlük/haftalık çok yavaş bir sızıntı ihtimaline karşı ucuz bir sigorta olarak
        // kalsın diye — ama sıklığı, asıl koruma zaten bellek eşiğinden geldiği için 6 kata düşürüldü
        // (log gürültüsü ve gereksiz rebuild sayısı da aynı oranda azalır).
        var ocrRecycleHours = config["OcrRecycleHours"]?.GetValue<double>() ?? 6.0;
        // 2GB (2026-08-08, kullanıcı geri bildirimi): yenileme neredeyse bedavaya geldiği için
        // ("düşük maliyet" yukarıdaki not) eşiği yüksek tutmanın faydası yok — mümkün olduğunca
        // düşük tutup sunucuyu sürekli düşük bellek baskısında bırakmak tercih edildi. 6GB (ilk
        // seçilen değer) "felakete yakınken müdahale et" gibiydi; 2GB, taze kurulmuş 3 model
        // örneğinin gerçek tabanının (muhtemelen ~1-1.5GB) üstünde bir tavan.
        var ocrRecycleMemoryMb = config["OcrRecycleMemoryMb"]?.GetValue<long>() ?? 2048;
        var ocrPool = OcrEngineFactory.Create(ocrParallelism, ocrCacheCapacity);
        var ocrPoolCreatedAt = DateTime.UtcNow;

        _logger.LogInformation("PriceBot Worker başladı. IncomingRoot={IncomingRoot} BotSendUrl={BotSendUrl} OcrParalel={OcrParallelism} OcrCacheCapacity={OcrCacheCapacity} OcrRecycleHours={OcrRecycleHours} OcrRecycleMemoryMb={OcrRecycleMemoryMb} SendReportToCustomer={SendReportToCustomer}",
            IncomingRoot, BotSendUrl, ocrParallelism, ocrCacheCapacity, ocrRecycleHours, ocrRecycleMemoryMb, sendReportToCustomer);

        // 2026-08-09: Önceden bu kontrol SADECE dış while döngüsünün başında çalışıyordu — ama
        // aşağıdaki `foreach (var folder in readyFolders)` birikmiş çok sayıda klasörü TEK bir turda
        // (bir sonraki döngü başına dönmeden) art arda işliyor. Birikinti varsa (ör. servis birkaç kez
        // yeniden başlatılıp test edilirken kuyruklanan klasörler) bu foreach saatlerce sürebilir ve
        // yenileme hiç fırsat bulamadan bellek/CPU tavana çıkabilirdi (üretimde canlı gözlemlendi —
        // CPU %96,8, bellek 7,1GB, 2GB eşiğinin üstünde ama yenileme tetiklenmemişti). Çözüm: kontrolü
        // yerel bir fonksiyona çıkarıp hem dış döngü başında HEM DE her klasörden önce çağırmak — böylece
        // büyük bir birikinti işlenirken bile klasörler arasında düzenli olarak fırsat buluyor. Sadece
        // klasör aralarında çağrıldığı için güvenlik aynı: hiçbir zaman bir Parallel.For'un ortasında
        // tetiklenmiyor.
        void TryRecycleOcrPool()
        {
            var elapsedSinceRecycle = DateTime.UtcNow - ocrPoolCreatedAt;
            var workingSetMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
            var recycleDueToTime = ocrRecycleHours > 0 && elapsedSinceRecycle >= TimeSpan.FromHours(ocrRecycleHours);
            var recycleDueToMemory = ocrRecycleMemoryMb > 0 && workingSetMb >= ocrRecycleMemoryMb;
            if (!recycleDueToTime && !recycleDueToMemory) return;

            _logger.LogInformation(
                "OCR motor havuzu yenileniyor (native bellek geri kazanımı için) — sebep: {Reason}, o anki çalışma belleği {WorkingSetMb} MB, son yenilemeden bu yana {Elapsed}.",
                recycleDueToMemory ? $"bellek eşiği ({ocrRecycleMemoryMb} MB)" : $"zamanlayıcı ({ocrRecycleHours} sa)",
                workingSetMb, elapsedSinceRecycle);
            // Yenileme kendi try/catch'iyle izole: yeni havuz kurulumu başarısız olursa
            // (ör. geçici dosya kilidi, bellek yetersizliği) eski havuz DOKUNULMADAN çalışmaya
            // devam eder ve klasör işlemesi normal şekilde ilerler — dıştaki genel catch'e düşüp
            // gereksiz yere atlanmaz. Başarısız kurulum bir sonraki fırsatta otomatik tekrar denenir,
            // servis kesintiye uğramaz.
            try
            {
                var newPool = OcrEngineFactory.Create(ocrParallelism, ocrCacheCapacity);
                var oldPool = ocrPool;
                ocrPool = newPool;
                ocrPoolCreatedAt = DateTime.UtcNow;
                try
                {
                    oldPool.Dispose();
                }
                catch (Exception disposeEx)
                {
                    // Yeni havuz zaten devrede ve sağlıklı — eski havuzun kapanışı başarısız
                    // olsa bile servis etkilenmez, sadece bir miktar native bellek geri
                    // kazanılamamış olabilir (zararsız, bir sonraki başarılı yenilemede düzelir).
                    _logger.LogWarning(disposeEx, "Eski OCR havuzu kapatılırken hata oluştu (yeni havuz zaten devrede, servise zararı yok).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR havuzu yenilenemedi — eski havuzla devam ediliyor, bir sonraki fırsatta tekrar denenecek.");
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TryRecycleOcrPool();

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
                    // Birikmiş çok sayıda klasör varsa bu foreach uzun sürebilir — her klasörden
                    // önce tekrar kontrol ederek yenilemenin dış döngü turunu beklemesini engelle
                    // (bkz. TryRecycleOcrPool tanımındaki 2026-08-09 notu).
                    TryRecycleOcrPool();

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
                        http, folder, senderPhone, excelFile, Path.GetFileName(excelFile), ocrPool, scans, brandList, excludedBrands, geminiClassifier, stoppingToken);
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

                    // Klasör başına, SAĞLAYICI BAŞINA devre kesici (2026-08-10, çoklu-sağlayıcıya
                    // genişletildi 2026-08-13 — bkz. codeClassifiers, IProductCodeClassifier.cs):
                    // her sağlayıcı kendi ApiFailed sinyaliyle BAĞIMSIZ olarak "bu klasörde tükendi"
                    // işaretlenir — biri (ör. Gemini birincil key) tükense bile diğerleri (ikincil
                    // key, Groq, Claude) klasördeki kalan görseller için denenmeye devam eder. Sabit
                    // bir sayı sınırı yerine, hata görülene kadar dener, görülünce o sağlayıcı için
                    // durur. ApiFailed=true KOTA (429) ile eş anlamlı DEĞİL — bkz. hemen aşağıdaki
                    // RetryAfter notu; ApiFailed=true SADECE 400/404/içerik-engeli gibi gerçekten
                    // kalıcı bir hatada dönüyor.
                    var codeClassifierHealthy = new bool[codeClassifiers.Count];
                    Array.Fill(codeClassifierHealthy, true);

                    // GÜVENLİK AĞI (2026-08-19, gerçek vaka: Gonderim_20260819_121909_286105ab,
                    // dosyalar "MODEL_19942_...jpg", "MODEL_19944_...jpg", "MODEL_19945_...jpg"):
                    // bot bazen kullanıcının yazdığı model numarasını dosya adına gömüyor. Bu üç
                    // görselde OCR kodu okuyamayınca AI görü fallback'i devreye girdi; ama
                    // 19942/19944/19945 Excel'de VAR, sadece fiyat hücresi boş olduğu için
                    // excelCodes'a (AI'ye verilen KAPALI liste) hiç girmemişti — AI bu yüzden
                    // gerçek kodu ASLA döndüremezdi, en yakın komşu bir kodu (13994/13995) "%100
                    // güven"le "buluyordu" ve YANLIŞ fiyat müşteriye gönderiliyordu (aynı yanlış
                    // kod 13994, dosya adına göre FARKLI iki ürüne — 19944 VE 19945'e — düşmesi bu
                    // hallüsinasyonun somut kanıtıydı). OCR'ın kendi "DİKKAT: Excel'de VAR ama
                    // fiyat hücresi boş" güvenlik ağı (Aşama 3.3, aşağıda) SADECE scan.Candidates'e
                    // (görselden OCR'ın okuduğu adaylar) bakıyordu, dosya adına hiç bakmıyordu ve
                    // AI'ye hiç uygulanmıyordu. Çözüm: dosya adında geçen bir sayı, Excel'de VAR
                    // ama fiyatsız (skippedExcelRows) bir koda denk geliyorsa, bu görsel için
                    // hiçbir AI sağlayıcısı ÇAĞRILMAZ — güçlü ters kanıt varken kapalı-liste
                    // zorlamasının üreteceği "güvenli görünen ama yanlış" bir tahmine güvenmektense
                    // açıkça atlanıp Aşama 3.3'te aynı DİKKAT notuyla raporlanır.
                    List<string> FilenamePricelessCodes(string file) =>
                        Regex.Matches(Path.GetFileNameWithoutExtension(file), @"\d{3,7}")
                            .Select(m => m.Value)
                            .Where(c => skippedExcelRows.Any(s => string.Equals(s.Code, c, StringComparison.OrdinalIgnoreCase)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                    // Aşama 3.1: kod çözümleme (2026-08-13, kullanıcı isteği: kota/rate-limit'te
                    // SENKRON beklemek yerine "diğer görselleri işlerken geçecek doğal süreyi
                    // kullan, sonra bir kez daha dene" — gerçek vaka: Groq'un TPM (dakikada token)
                    // sınırı, büyük bir aday-kod listesiyle SANİYELER içinde doluyor; bir görseli
                    // onlarca saniye bloke etmek yerine sıradaki görsele geçip dönmek çok daha
                    // verimli). Bir sağlayıcı <see cref="IProductCodeClassifier"/>'ın RetryAfter'ını
                    // dönerse (kota/rate-limit ama KALICI DEĞİL) bu sağlayıcı bu görsel için ŞİMDİLİK
                    // atlanır (devre kesici TETİKLENMEZ) ve SIRADAKİ sağlayıcı hemen denenir; hiçbir
                    // sağlayıcı bulamayıp en az biri RetryAfter dönmüşse görsel "atlandı" YERİNE
                    // deferredForRetry'e eklenir — Aşama 3.2'de tekrar denenecek.
                    var deferredForRetry = new List<int>();
                    for (int idx = 0; idx < scans.Count; idx++)
                    {
                        if (chosen.ContainsKey(idx)) continue;
                        var (file, scan) = scans[idx];
                        var fileName = Path.GetFileName(file);
                        bool anyRetryAfter = false;

                        // Yukarıdaki güvenlik ağı: dosya adında Excel'de VAR ama fiyatsız bir kod
                        // görülüyorsa AI'ye hiç sorulmadan geç — Aşama 3.3, scan.Candidates'e ek
                        // olarak bunu da DİKKAT notunda değerlendirecek.
                        if (FilenamePricelessCodes(file).Count > 0) continue;

                        // Son çare (2026-08-10, çoklu-sağlayıcıya genişletildi 2026-08-13): OCR
                        // bu görselde Excel kodlarından hiçbiriyle eşleşen bir aday bulamadı.
                        // Marka fallback'inin aksine burada WhatsApp'a soru YOK — kod, marka gibi
                        // klasör genelinde sabit değil, sorulacak tek bir "doğru cevap" yok.
                        // codeClassifiers SIRAYLA (ucuz/ücretsiz önce) denenir, ilk başarılı
                        // sonuçta durulur; sağlığı düşen (ApiFailed) bir sağlayıcı bu görsel için
                        // atlanır ama SIRADAKİ sağlayıcı yine denenir.
                        for (int ci = 0; ci < codeClassifiers.Count && !chosen.ContainsKey(idx); ci++)
                        {
                            if (!codeClassifierHealthy[ci]) continue;

                            var (source, classifier) = codeClassifiers[ci];
                            var (code, apiFailed, retryAfter) = await classifier.ClassifyCodeAsync(file, excelCodes, stoppingToken);
                            if (apiFailed)
                            {
                                codeClassifierHealthy[ci] = false;
                                _logger.LogWarning("Klasör {Folder}: {Source} API hatası verdi, bu klasördeki kalan görseller için bu sağlayıcı atlanacak.", folder, source);
                            }
                            else if (retryAfter is not null)
                            {
                                anyRetryAfter = true;
                            }
                            else if (code is not null && excelPrices.ContainsKey(code))
                            {
                                chosen[idx] = [new CodeMatch(code, Confidence: 100, IsFuzzy: true, Source: source)];
                                _logger.LogInformation("'{File}': OCR kodu bulamadı, {Source} buldu: {Code}", fileName, source, code);
                            }
                        }

                        if (!chosen.ContainsKey(idx) && anyRetryAfter)
                            deferredForRetry.Add(idx);
                    }

                    // Aşama 3.2: ertelenen görseller — Aşama 3.1'de klasördeki DİĞER görselleri
                    // işlerken doğal olarak geçen süreden SONRA, TEK bir ek turda tekrar denenir.
                    // Hâlâ kota/rate-limit alırsa (yeterli süre geçmediyse) üçüncü bir tur YOK —
                    // sınırlı kalsın diye normal "atlandı" akışına düşülür (aşağıdaki Aşama 3.3).
                    if (deferredForRetry.Count > 0)
                    {
                        _logger.LogInformation("Klasör {Folder}: {Count} görsel kota/rate-limit yüzünden ertelenmişti, diğer görseller işlendikten sonra tekrar deneniyor.", folder, deferredForRetry.Count);
                        foreach (var idx in deferredForRetry)
                        {
                            if (chosen.ContainsKey(idx)) continue;
                            var (file, scan) = scans[idx];
                            var fileName = Path.GetFileName(file);

                            for (int ci = 0; ci < codeClassifiers.Count && !chosen.ContainsKey(idx); ci++)
                            {
                                if (!codeClassifierHealthy[ci]) continue;

                                var (source, classifier) = codeClassifiers[ci];
                                var (code, apiFailed, retryAfter) = await classifier.ClassifyCodeAsync(file, excelCodes, stoppingToken);
                                if (apiFailed)
                                {
                                    codeClassifierHealthy[ci] = false;
                                    _logger.LogWarning("Klasör {Folder}: {Source} API hatası verdi, bu klasördeki kalan görseller için bu sağlayıcı atlanacak.", folder, source);
                                }
                                else if (code is not null && excelPrices.ContainsKey(code))
                                {
                                    chosen[idx] = [new CodeMatch(code, Confidence: 100, IsFuzzy: true, Source: source)];
                                    _logger.LogInformation("'{File}': OCR kodu bulamadı, {Source} (ertelenmiş tekrar deneme) buldu: {Code}", fileName, source, code);
                                }
                                // retryAfter burada bilinçli olarak yok sayılır — ikinci tur da
                                // kota/rate-limit'e takılırsa üçüncü bir erteleme yapılmaz.
                            }
                        }
                    }

                    // Aşama 3.3: sonuçlara göre damgala/gönder (chosen artık, ertelenenler dahil,
                    // TAMAMEN çözümlenmiş durumda).
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
                            // Dosya adındaki kanıt de (bkz. yukarıdaki FilenamePricelessCodes) aynı
                            // DİKKAT notuna dahil edilir — MODEL_19942/19944/19945 vakasında OCR
                            // görselden HİÇ aday okuyamamıştı, tek kanıt dosya adındaydı.
                            var priceless = scan.Candidates
                                .Where(c => skippedExcelRows.Any(s => string.Equals(s.Code, c, StringComparison.OrdinalIgnoreCase)))
                                .Union(FilenamePricelessCodes(file), StringComparer.OrdinalIgnoreCase)
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
                            return new StampedCode(m.Code, m.Confidence, m.IsFuzzy, priceExcel, priceInTry, priceInUsd, m.Source);
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
                            if (best.IsFuzzy && best.Source != "OCR")
                            {
                                // Gemini görü tespiti (2026-08-10): OCR'ın Levenshtein-tabanlı kısmi
                                // güven skoruyla KARIŞTIRILMASIN diye ayrı bir log mesajı — "YAKLAŞIK
                                // (fuzzy)" ifadesi burada yanıltıcı olurdu (enum zorlaması sayesinde
                                // Gemini'nin cevabı "kesin" bir seçim, kısmi bir okuma değil).
                                _logger.LogWarning("'{File}' -> {Source}, KONTROL ÖNERİLİR: kod {Code} -> {PriceExcel:N2} × {Carpan} = {PriceTry:N2} TRY ({PriceUsd:N2} USD) -> {Output}",
                                    fileName, best.Source, best.Code, best.PriceExcel, brand.NetCarpan, best.PriceTry, best.PriceUsd, Path.GetFileName(stamped));
                            }
                            else if (best.IsFuzzy)
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

                    // 2026-08-08: ExtraRecipients kaldırıldı (kullanılmıyordu) — alıcı artık her zaman
                    // sadece gönderen numara.
                    var recipients = new List<string> { senderPhone };
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

                    // GÜVENLİK AĞI (2026-08-19, gerçek vaka: Gonderim_20260818_133622_804e335e):
                    // excelFiles/allFiles yukarıda (satır ~342/~401) döngü BAŞINDA bir kez
                    // okunuyor — bu klasörün OCR/damgalama/gönderim işi dakikalar sürebildiği
                    // için, müşteri bu ARALIKTA klasöre YENİ bir gerçek Excel + yeni görseller
                    // bırakabiliyor, worker bunu hiç fark etmiyordu. Gerçek vakada aynı ada sahip
                    // İKİNCİ bir "ALİSA PİYASA 3 İP .xlsx" + 56 görsel, birinci Excel işlenirken
                    // (13:38-13:41 arası) klasöre düştü; worker tek-Excel akışıyla (sadece ilk
                    // Excel'i) işleyip islendi.txt yazdı — bu dosyanın varlığı klasörü KALICI
                    // olarak taramadan çıkardığı için (yukarıda "!File.Exists(islendi.txt)" şartı)
                    // ikinci Excel + 56 görsel hiçbir rapora girmeden, hiç işlenmeden, hiç
                    // gönderilmeden SONSUZA DEK kayboldu — hiçbir yerde hata da loglanmadı.
                    //
                    // Düzeltme: gönderim tamamlandıktan ama islendi.txt yazılmadan HEMEN ÖNCE,
                    // klasör bir kez daha (en güncel hâliyle) taranır. Başlangıçta görülenden
                    // FAZLA gerçek Excel varsa, YENİ Excel(ler) + bu turda hiç ele alınmamış
                    // (allFiles snapshot'ında olmayan) görseller, standart
                    // "Gonderim_yyyyMMdd_HHmmss_guid" adlandırmasıyla TAZE bir kardeş klasöre
                    // taşınır — bu klasörün islendi.txt'si olmadığı için bir sonraki taramada
                    // sıfırdan (gerekirse yine SplitMultiBrandFolder ile) bağımsız işlenir. Bu
                    // turda zaten damgalanıp GÖNDERİLMİŞ görseller asla taşınmaz/tekrar işlenmez
                    // — sadece henüz hiç ele alınmamış olanlar kurtarılır. Taşıma bir OS hatasıyla
                    // (ör. dosya o an kilitli) başarısız olursa bu turda kurtarılamaz (kalan artık
                    // risk, kabul edildi) — ama en azından loglanır, mevcut davranışta olduğu gibi
                    // sessizce kaybolmaz.
                    try
                    {
                        var lateExcelFiles = RealXlsxFiles(folder)
                            .Where(f => !excelFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                            .ToList();
                        if (lateExcelFiles.Count > 0)
                        {
                            var handledFileNames = new HashSet<string>(allFiles.Select(f => Path.GetFileName(f)!), StringComparer.OrdinalIgnoreCase);
                            var lateImageFiles = Directory.EnumerateFiles(folder)
                                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                                .Where(f => !handledFileNames.Contains(Path.GetFileName(f)))
                                .ToList();

                            var earliestLateWrite = lateExcelFiles.Concat(lateImageFiles)
                                .Select(File.GetLastWriteTime)
                                .DefaultIfEmpty(DateTime.Now)
                                .Min();
                            var continuationName = $"Gonderim_{earliestLateWrite:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
                            var continuationFolder = Path.Combine(new DirectoryInfo(folder).Parent!.FullName, continuationName);
                            Directory.CreateDirectory(continuationFolder);

                            foreach (var f in lateExcelFiles.Concat(lateImageFiles))
                                File.Move(f, Path.Combine(continuationFolder, Path.GetFileName(f)));

                            File.WriteAllText(Path.Combine(continuationFolder, "devam_kaynak_klasoru.txt"),
                                $"Bu klasör, '{Path.GetFileName(folder)}' klasörü işlenirken (OCR/damgalama sürerken) o klasöre " +
                                $"sonradan düşen {lateExcelFiles.Count} Excel + {lateImageFiles.Count} görsel için otomatik oluşturuldu " +
                                $"(bkz. Worker.cs, 2026-08-19 güvenlik ağı). Zaman: {DateTime.Now:yyyy-MM-dd HH:mm:ss}.{Environment.NewLine}",
                                Encoding.UTF8);

                            _logger.LogWarning(
                                "Klasör {Folder}: işlem sürerken {ExcelCount} yeni Excel + {ImageCount} yeni görsel geldi — kaybolmasınlar diye {Continuation} klasörüne taşındı, bir sonraki turda bağımsız işlenecek.",
                                folder, lateExcelFiles.Count, lateImageFiles.Count, continuationName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Klasör {Folder}: işlem sırasında gelen yeni Excel/görselleri ayrı klasöre taşırken hata oluştu — bu turda kurtarılamadılar, orijinal klasörde kalacaklar.",
                            folder);
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
                        var customerReport = BuildCustomerFacingReport(
                            folder, brand, rate.Value.Rate, allFiles.Count, imageResults, sendResults, stopwatch.Elapsed);
                        // Müşteriye giden metnin kaydı: gönderim başarısız olsa bile (bot kapalı vb.)
                        // ne gönderilmeye çalışıldığı klasörde kalıcı olarak dursun diye WhatsApp
                        // gönderiminden ÖNCE yazılıyor. islendi.txt (iç/ayrıntılı rapor) ile
                        // karışmaması için ayrı dosya adı.
                        File.WriteAllText(Path.Combine(folder, "musteri_raporu.txt"), customerReport, Encoding.UTF8);
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

        ocrPool.Dispose();
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
        string excelPath,
        string excelName,
        OcrEnginePool ocrPool,
        List<(string File, ScanResult Scan)> scans,
        List<BrandMultiplier> brandList,
        List<BrandMultiplier> excludedBrands,
        GeminiVisionClassifier geminiClassifier,
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

            // Kullanıcı bir önceki listede "Diğer"i seçmişti (bkz. OtherBrandOptionText) — tekrar
            // liste sunmak anlamsız (zaten reddetti), doğrudan serbest metin iste, "listede
            // bulunamadı" çerçevelemesi de kafa karıştırıcı olurdu ("Diğer" zaten bir marka adı
            // değil), o yüzden atlanır.
            string retryQuestion;
            List<string>? retryOptions;
            if (string.Equals(answerText, OtherBrandOptionText, StringComparison.OrdinalIgnoreCase))
            {
                retryQuestion = $"PriceBot: '{excelName}' listesindeki ürünler için markanın tam adını tek mesaj olarak yazınız (örnek: LİLAX).";
                retryOptions = null;
            }
            else
            {
                var suggestionText = outcome.Suggestions.Count > 0
                    ? $" Şunlardan birini mi kastettiniz: {string.Join(", ", outcome.Suggestions)}? Bunlardan biri doğruysa onu yazabilir, değilse markanın tam adını tekrar yazabilirsiniz."
                    : $" Lütfen '{excelName}' listesindeki ürünler için markanın tam adını tek mesaj olarak tekrar yazınız.";
                retryQuestion = $"PriceBot: '{answerText}' marka listesinde bulunamadı.{suggestionText}";
                retryOptions = outcome.Suggestions.Count > 0 ? [.. outcome.Suggestions, OtherBrandOptionText] : null;
            }
            _logger.LogWarning("Klasör {Folder}: marka cevabı '{Answer}' listeyle eşleşmedi, tekrar sorulacak. Öneriler: {Suggestions}",
                folder, answerText, outcome.Suggestions.Count == 0 ? "(yok)" : string.Join(", ", outcome.Suggestions));
            await SendBrandQuestionAsync(http, folder, senderPhone, questionPath, retryQuestion, retryOptions, ct);
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

        // Excel letterhead marka taraması (2026-08-11, gerçek vaka: PRETTY LİFE): OCR (kod
        // taraması + renk kurtarma) markayı bulamadı/çelişkili buldu. Gemini'ye (ağ çağrısı,
        // gecikme+kota) başvurmadan önce, bazı üretici Excel'lerinin kod/fiyat başlığından
        // ÖNCEKİ satırlarında duran firma adı/logo yanı metnine ("PRETTY LİFE TEKSTİL İNŞ...")
        // bakılır — ücretsiz ve deterministik. Gerçek vakada hiçbir ürün görselinde marka adı
        // basılı değildi (sadece jenerik tasarım ibareleri vardı), ama Excel'in üst bilgisinde
        // gerçek metin olarak duruyordu. ExtractLetterheadTokens bilinçli olarak SADECE bu alanı
        // tarar (ürün açıklama sütunları hariç — ALİSA/karışık-katalog riskiyle aynı sınıftaki
        // yanlış eşleşmeyi Excel tarafında tekrarlamamak için, bkz. CLAUDE.md "KIDSWEAR" notu);
        // aynı BrandMatcher kelime-eşleştirme + jenerik-kelime filtresi + çelişkili-NetCarpan
        // güvenlik ağı kullanılır. Bulunamazsa/çelişkiliyse davranış hiç değişmez.
        var letterheadOutcome = BrandMatcher.MatchFromOcrTokens(ExcelPriceReader.ExtractLetterheadTokens(excelPath), brandList);
        if (letterheadOutcome.Brand is not null)
        {
            var approxNote = letterheadOutcome.Approximate ? ", yaklaşık eşleşme" : "";
            _logger.LogInformation("Klasör {Folder}: Excel üst bilgisinde (letterhead) marka bulundu: {Brand} (kanıt: {Evidence})",
                folder, letterheadOutcome.Brand.FullName, string.Join(" ", letterheadOutcome.MatchedWords));
            return (letterheadOutcome.Brand, $"Excel üst bilgisi (kanıt: {string.Join(" ", letterheadOutcome.MatchedWords)}{approxNote})");
        }
        if (letterheadOutcome.AmbiguousNames.Count > 0)
        {
            _logger.LogInformation("Klasör {Folder}: Excel üst bilgisinde çelişkili (farklı çarpanlı) marka adayları bulundu ({Brands}), atlanıyor.",
                folder, string.Join(", ", letterheadOutcome.AmbiguousNames));
        }

        // Son çare: OCR (kod taraması + renk-duyarlı kurtarma) ve Excel letterhead taraması
        // markayı ya HİÇ bulamadı ya da ÇELİŞKİLİ biçimde birden fazla (farklı çarpanlı) marka
        // buldu — WhatsApp'a sormadan önce Google Gemini'nin görü modeline (2026-08-10, ücretsiz
        // katman) kapalı marka listesiyle son bir şans verilir — bkz. GeminiVisionClassifier.cs
        // dosya başı yorumu (resim-logo/aşırı dekoratif font vakası, hiçbir OCR'ın çözemediği
        // durum). GeminiApiKey boşsa ClassifyBrandAsync hiçbir ağ isteği atmadan null döner,
        // davranış hiç değişmez.
        //
        // 2026-08-11 GENİŞLETME (kullanıcı isteği): eskiden bu adım SADECE ocrOutcome.
        // AmbiguousNames boşken ("hiç bulamadım") çalışırdı; "çelişkili birden fazla marka"
        // durumu bilinçli olarak atlanıp direkt WhatsApp sorusuna düşülürdü (risk almama
        // tercihiydi, ilk sürümde kapsam dar tutulmuştu). Artık HER İKİ durumda da Gemini önce
        // denenir: gerçek bir vaka (bkz. CLAUDE.md "KIDSWEAR" notu) çelişkili eşleşmelerin çoğu
        // zaman OCR gürültüsünden (ör. iki markanın ortak jenerik tagline'ının üçüncü, alakasız
        // bir markayla tesadüfen çakışması) kaynaklandığını gösterdi — görü modeli harf
        // okumadığı için bu tür OCR-özgü tuzaklara düşmüyor, asıl görsele bakıp doğrudan karar
        // verebiliyor. Gemini de bulamazsa (ya da kapalıysa) davranış aynı: WhatsApp sorusu.
        var visionFiles = scans.Take(4).Select(s => s.File).ToList();
        var visionResult = await geminiClassifier.ClassifyBrandAsync(visionFiles, brandList, ct);
        if (visionResult is not null)
        {
            _logger.LogInformation("Klasör {Folder}: Gemini görü modeli markayı buldu: {Brand}",
                folder, visionResult.Value.Brand.FullName);
            return (visionResult.Value.Brand, $"Gemini görü modeli (etiket: '{visionResult.Value.RawLabel}')");
        }

        if (ocrOutcome.AmbiguousNames.Count > 0)
        {
            _logger.LogWarning("Klasör {Folder}: OCR birden fazla çelişkili (farklı çarpanlı) marka buldu ({Brands}), Gemini de çözemedi, kullanıcıya sorulacak.",
                folder, string.Join(", ", ocrOutcome.AmbiguousNames));
        }
        else
        {
            _logger.LogInformation("Klasör {Folder}: Gemini görü modeli de marka bulamadı (veya kapalı), kullanıcıya sorulacak.", folder);
        }

        // Soru metnine Excel adı eklenir: çoklu marka gönderimi gruplara bölündüğünde aynı numaraya
        // birden fazla soru düşebilir; kullanıcı hangi sorunun hangi listeye ait olduğunu kendi
        // gönderdiği dosyanın adından ayırt eder. Bot, cevabı her zaman en eski bekleyen klasöre
        // yazdığı için soruların ve cevapların sırası eşleşir (bkz. bot_marka_cevap_gorevi.md).
        // Seçenek listesi önceliği: OCR'ın çelişkili biçimde eşleştirdiği markalar (ocrOutcome.
        // AmbiguousNames — bunlar kesin OCR kanıtına dayanır, sadece NetCarpan çakışması yüzünden
        // otomatik karar verilemedi) varsa ONLAR sunulur; yoksa (hiç eşleşme yok) daha zayıf
        // Levenshtein-yakınlık tahminleri (SuggestBrandsFromOcrTokens) kullanılır. Her iki durumda
        // da listeye "Diğer" eklenir (bkz. OtherBrandOptionText) ki kullanıcı hiçbiri değilse kendi
        // yazabilsin — bkz. bot_marka_secim_gorevi.md (WhatsApp interaktif liste mesajı sözleşmesi).
        var candidateOptions = ocrOutcome.AmbiguousNames.Count > 0
            ? ocrOutcome.AmbiguousNames
            : BrandMatcher.SuggestBrandsFromOcrTokens(unionTokens, brandList);
        var hasOptions = candidateOptions.Count > 0;
        var guessText = hasOptions
            ? $" Şunlardan biri olabilir mi: {string.Join(", ", candidateOptions)}?"
            : "";
        var tail = hasOptions
            ? " Bunlardan biri doğruysa onu yazabilir, değilse markanın tam adını yazabilirsiniz."
            : " Markanın tam adını tek mesaj olarak yazınız (örnek: LİLAX).";
        var question = "PriceBot: Gönderdiğiniz fotoğraflardaki ürünlerin markası otomatik tespit edilemedi " +
                       $"('{excelName}' listesindeki ürünler).{guessText}{tail}";
        var options = hasOptions ? candidateOptions.Append(OtherBrandOptionText).ToList() : null;
        if (hasOptions)
            _logger.LogInformation("Klasör {Folder}: marka bulunamadı, ilk soruya seçenekler eklendi ({Kaynak}): {Guesses}",
                folder, ocrOutcome.AmbiguousNames.Count > 0 ? "çelişkili OCR eşleşmesi" : "yakınlık tahmini", string.Join(", ", candidateOptions));
        await SendBrandQuestionAsync(http, folder, senderPhone, questionPath, question, options, ct);
        return null;
    }

    /// <summary>Marka sorusunu gönderene iletir ve BAŞARILIYSA marka_sorusu.txt işaretçisini
    /// yazar — klasör, bot cevabı marka_cevap.txt olarak yazana kadar taramalarda atlanır.
    /// Gönderim başarısızsa (örn. bot kapalı) işaretçi yazılmaz; klasör bir sonraki turda
    /// baştan işlenir ve soru tekrar denenir.
    /// <paramref name="options"/> doluysa (2026-08-08) bot'a WhatsApp interaktif liste mesajı
    /// olarak göndermesi için iletilir (bkz. bot_marka_secim_gorevi.md) — null/boşsa eskisi gibi
    /// sade metin sorusu gönderilir, davranış değişmez.</summary>
    private async Task SendBrandQuestionAsync(HttpClient http, string folder, string senderPhone, string questionPath, string question, List<string>? options, CancellationToken ct)
    {
        var result = await TrySendTextAsync(http, question, senderPhone, ct, options);
        if (!result.Success)
        {
            _logger.LogWarning("Klasör {Folder}: marka sorusu gönderilemedi ({Status}), bir sonraki turda tekrar denenecek.",
                folder, result.StatusInfo);
            return;
        }

        File.WriteAllText(questionPath,
            $"Gönderilme zamanı: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"Alıcı: {senderPhone}{Environment.NewLine}" +
            (options is { Count: > 0 } ? $"Seçenekler: {string.Join(" | ", options)}{Environment.NewLine}" : "") +
            $"Soru: {question}{Environment.NewLine}",
            Encoding.UTF8);
        _logger.LogInformation("Klasör {Folder}: marka sorusu {Recipient} numarasına gönderildi, cevap bekleniyor.", folder, senderPhone);
    }

    /// <summary>Bot'a dosyasız düz metin mesaj gönderir (FilePath boş string). Bot tarafının
    /// boş FilePath'i "sadece metin gönder" olarak yorumlaması gerekir (bkz. CLAUDE.md'deki
    /// bot sözleşmesi). <paramref name="options"/> (2026-08-08): doluysa JSON body'de "Options"
    /// alanı olarak iletilir — bot bunu WhatsApp interaktif liste mesajına çevirebilir (bkz.
    /// bot_marka_secim_gorevi.md); null ise alan null serileşir, bot'un mevcut sade-metin
    /// davranışı DEĞİŞMEZ (geriye dönük uyumlu, opsiyonel alan).</summary>
    private async Task<SendResult> TrySendTextAsync(HttpClient http, string messageText, string recipient, CancellationToken ct, List<string>? options = null)
    {
        var body = JsonSerializer.Serialize(new { ToNumber = recipient, MessageText = messageText, FilePath = "", Options = options });
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
    // 2026-08-09: Müşteriye giden metin artık tam raporun kısaltılmış hâli DEĞİL, baştan sona
    // ayrı ve kısa bir özet — sadece gerçekten bilinmesi gereken şeyler: kaç görsel fiyatlandı,
    // kaçı neden atlandı/hata aldı, hangi marka/kurla hesaplandı. Fiyatlandırılan her görselin
    // kod/fiyat dökümü BİLEREK yok — müşteri zaten o görselleri fiyat damgalı olarak ayrıca
    // alıyor, aynı bilgiyi ikinci kez metinle okumasına gerek yok. Önceki tasarım (tam raporu
    // "--- Görseller ---" bölümünden kısaltarak üretmek) büyük klasörlerde (65 görsel) hâlâ
    // WhatsApp'ın pratik metin sınırını (~4096 karakter) zorluyordu — üretimde canlı gözlemlendi
    // (DECO SPORT: bot "OK" döndürdü ama müşteriye hiçbir şey ulaşmadı). Bu tasarım klasör
    // büyüklüğünden bağımsız olarak sabit/kısa kalır (en fazla `CustomerReportMaxSkippedListed`
    // atlanan satırı listelenir, gerisi sayıyla özetlenir).
    private const int CustomerReportMaxSkippedListed = 15;

    /// <summary>Gonderim klasörünün müşteri tarafından GERÇEKTEN oluşturulduğu (gönderildiği) anı
    /// döner — klasör adındaki Gonderim_yyyyMMdd_HHmmss_... zaman damgasından ayrıştırılır. Bu,
    /// dosya sisteminin CreationTime'ından daha güvenilir: çoklu marka bölmesinde (_grupN) grup
    /// klasörü worker tarafından İŞLEME SIRASINDA (bölme anında) yaratılır, o anki CreationTime
    /// müşterinin asıl gönderim anını değil bölme anını gösterir; oysa klasör adının başındaki
    /// zaman damgası bölme sonrasında da (Gonderim_..._grupN) korunur. Ad beklenen kalıba
    /// uymuyorsa (savunmacı fallback, örn. eski/elle oluşturulmuş test klasörleri) dosya
    /// sisteminin CreationTime'ına düşülür.</summary>
    private static DateTime GetFolderCreatedAt(string folder)
    {
        var name = Path.GetFileName(folder);
        var m = FolderTimestampRegex.Match(name);
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        return Directory.GetCreationTime(folder);
    }

    private static string BuildCustomerFacingReport(
        string folder, BrandMultiplier brand, decimal rateValue, int totalImages,
        List<ImageResult> imageResults, List<SendResult> sendResults, TimeSpan duration)
    {
        var matchedCount = imageResults.Count(r => r.OutputFileName is not null);
        var skipped = imageResults.Where(r => r.OutputFileName is null).ToList();
        var sentOk = sendResults.Count(s => s.Success);

        var sb = new StringBuilder();
        sb.AppendLine("=== PriceBot Raporu ===");
        sb.AppendLine($"Klasör: {Path.GetFileName(folder)}");
        sb.AppendLine($"Gönderim tarihi: {GetFolderCreatedAt(folder):yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Marka: {brand.FullName}  |  Kur: 1 USD = {rateValue} TRY  |  Süre: {duration.TotalSeconds:N0} sn");
        sb.AppendLine();
        sb.AppendLine($"Toplam görsel: {totalImages}  |  Fiyatlandırılan: {matchedCount}  |  Atlanan/Hatalı: {skipped.Count}  |  Gönderilen: {sentOk}/{sendResults.Count}");

        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Fiyatlandırılamayanlar:");
            foreach (var r in skipped.Take(CustomerReportMaxSkippedListed))
                sb.AppendLine($"- {r.FileName}: {r.SkipOrErrorReason}");
            if (skipped.Count > CustomerReportMaxSkippedListed)
                sb.AppendLine($"... ve {skipped.Count - CustomerReportMaxSkippedListed} tane daha (tam döküm için islendi.txt'ye bakınız)");
        }

        return sb.ToString();
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
                    {
                        // Source != "OCR" (2026-08-10): Gemini görü tespiti kaynaklı kod — OCR'ın
                        // Levenshtein-tabanlı kısmi ", fuzzy" notuyla karıştırılmasın diye ayrı not.
                        var sourceNote = s.Source != "OCR" ? $", kaynak: {s.Source}" : (s.IsFuzzy ? ", fuzzy" : "");
                        sb.AppendLine($"    kod {s.Code} (güven {s.Confidence:N0}{sourceNote}) -> {s.PriceExcel:N2} × {brand.NetCarpan} = {s.PriceTry:N2} TRY / {s.PriceUsd:N2} USD");
                    }
                }
                else
                {
                    var candidateNote = r.CandidateCount > 1 ? $", {r.CandidateCount} aday arasından seçildi" : "";
                    var singleSource = r.AllCodes is { Count: > 0 } ? r.AllCodes[0].Source : "OCR";
                    var sourceNote = singleSource != "OCR" ? $", kaynak: {singleSource}" : "";
                    sb.AppendLine($"{tag} {r.FileName} -> kod {r.Code} (güven {r.Confidence:N0}{candidateNote}{sourceNote}) -> {r.PriceExcel:N2} × {brand.NetCarpan} = {r.PriceTry:N2} TRY / {r.PriceUsd:N2} USD -> {r.OutputFileName}");
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
