// PaddleScanOcr.cs (2026-08-07) — PaddleOCR tabanlı IOcrEngine implementasyonu, Tesseract'a
// (FullScanOcr) alternatif. Spike sonucu (Spike/PaddleOcrProbe, gerçek üretim görselleriyle):
// PaddleOCR, Tesseract'ın ihtiyaç duyduğu ~10 farklı ön işleme kademesine (kontrast germe, yerel
// adaptif eşikleme, kenar-şerit/gri-dilim/min-RGB kurtarma taramaları — bkz. FullScanOcr.cs dosya
// başı) hiç gerek kalmadan TEK bir ham geçişte daha temiz ve daha hızlı sonuç verdi. Özellikle
// belgelenmiş Baby Flamindo dekoratif-logo vakasında (bkz. CLAUDE.md "Marka OCR kalibrasyonu")
// Tesseract'ın özel min-RGB kurtarma taraması gerektirdiği "FLAMINDO" yazısı, PaddleOCR'da normal
// taramada %99 güvenle doğrudan okundu (9/9 gerçek görsel). Bu yüzden bu dosyada Tesseract'ın
// preprocessing fonksiyonlarının HİÇBİRİ yok — sadece PaddleOcrAll.Run + native/Paddle'a bağımlı
// kurulum (constructor, Scan). Motor-bağımsız aday çıkarma/eşleştirme/fuzzy/yaş-aralığı mantığı
// (2026-08-12'de) ayrı bir partial dosyaya taşındı: PaddleScanOcrMatching.cs — OpenCvSharp/Paddle'a
// bağımlı olmadığı için Tests projesine doğrudan dahil edilip birim testlerle doğrulanabiliyor
// (bkz. o dosyanın başı, "57-099/57-097 vakası").
//
// Eski geri-dönüş notu (artık geçersiz, tarihi referans olarak bırakıldı): appsettings.json'da
// "OcrEngine": "Tesseract" yazıp Tesseract'a dönme yolu 2026-08-08'de `375f080` ile TAMAMEN
// kaldırıldı (FullScanOcr.cs dahil silindi, Worker.cs'in motor seçme kodu da kalktı).
// appsettings.json'da hâlâ bir "OcrEngine" anahtarı görürseniz o dönemden kalma ÖLÜ bir alandır,
// hiçbir kod okumuyor — silinmesi zararsızdır. Gerçek bir Tesseract'a dönüş gerekirse
// `git checkout checkpoint/dual-ocr-engines` ile o dönemki Worker.cs/OcrEngineFactory.cs'e
// bakılıp switch mantığı elle geri getirilmeli.
//
// EŞ ZAMANLILIK NOTU (2026-08-07, gerçek Worker koşusuyla bulundu): OcrEnginePool, Tesseract'ın
// modeline göre (N bağımsız TesseractEngine, her biri kendi thread'inde) tasarlanmıştı — ama
// PaddleOcrAll'ın altındaki native Paddle çıkarım motoru bunu desteklemiyor: N ayrı PaddleOcrAll
// örneği, Parallel.For'un rastgele/rotasyonlu thread pool thread'lerinden çağrılınca (her örnek
// HER SEFERİNDE aynı OS thread'inden çağrılmıyor) "PaddlePredictor(Detector) run failed" ile
// aralıklı ama sık başarısız oluyordu (gerçek koşu: 33 görsellik bir klasör hiç işlenemedi, sonsuz
// döngüde tekrar denendi). Kütüphanenin kendisi bunun için özel bir sınıf sağlıyor: QueuedPaddleOcrAll
// — sabit sayıda ADANMIŞ arka plan thread'i açar, her biri KENDİ PaddleOcrAll örneğini o thread
// üzerinde kurar ve hep aynı thread'den çalıştırır (native tarafın thread-affinity varsayımını
// karşılar). Bu yüzden burada tek bir paylaşılan QueuedPaddleOcrAll var; OcrEnginePool'un N
// "yuvası" (Worker.cs'in Parallel.For'unun beklediği paralellik derecesi için) bu TEK kuyruğa
// yönlendiren ince sarmalayıcılardır (bkz. PaddleScanOcr(PaddleScanOcr shared) kurucusu) — asıl
// paralellik QueuedPaddleOcrAll'ın kendi consumerCount'u üzerinden, adanmış thread'lerle sağlanır.
namespace PriceBotPipeline;

using System.Text.RegularExpressions;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;

public sealed partial class PaddleScanOcr : IOcrEngine
{
    private readonly QueuedPaddleOcrAll _queue;
    private readonly bool _ownsQueue;
    private readonly Regex _candidate;

    /// <summary>"Sahip" örnek: paylaşılan kuyruğu kurar (consumerCount adanmış thread ile) ve
    /// Dispose'da kapatır. OcrEngineFactory bunu havuzdaki TEK bir yuva için çağırır.
    ///
    /// BELLEK NOTU (2026-08-07, üretimde 23 GB/%95 bellek şikayetiyle bulundu): `PaddleDevice.Mkldnn()`
    /// varsayılanları (`cpuMathThreadCount: 0` = "otomatik", genelde tüm çekirdekleri kullan) burada
    /// KASITSIZ bir oversubscription yaratıyordu — consumerCount (=parallelism, 8 çekirdekli sunucuda 6)
    /// zaten her biri kendi adanmış OS thread'inde çalışan 6 BAĞIMSIZ PaddleOcrAll örneği kuruyor; her
    /// örnek İÇİNDE de "otomatik" tüm çekirdekleri kullanmaya çalışınca 6×8 = 48 rekabetçi iş parçacığı
    /// ortaya çıkıyor ve oneDNN'in thread-başına scratchpad/arena tamponları + `cacheCapacity` (varsayılan
    /// 10) ile örnek başına saklanan, görülen HER farklı girdi boyutu için ayrı derlenmiş kernel
    /// grafiği bu 6 örnek boyunca katlanarak birikiyor — CPU %0 olsa bile bellek geri verilmiyor (klasik
    /// "idle ama elde tutulan native bellek" imzası). Düzeltme: `cpuMathThreadCount: 1` (paralellik zaten
    /// consumerCount'un adanmış thread'leriyle sağlanıyor, örnek içi çoklu iş parçacığına gerek yok — bu
    /// asıl oversubscription'ı tek başına giderir) ve `Detector.MaxSize: 1600` (WhatsApp'tan gelen
    /// orijinal boyuttaki büyük fotoğrafların algılama modeline sınırsız boyutta girip her seferinde
    /// farklı/devasa tensör şekilleri için yeni kernel derlemesi + bellek ayırması tetiklemesini önler —
    /// PriceStamper'ın kendi 1600px sınırıyla tutarlı; bu ayrıca gerçek bir HIZ kazancıdır, algılama daha
    /// küçük tensörle çalışır).
    ///
    /// HIZ NOTU (2026-08-07): `cacheCapacity` (örnek başına saklanan, görülen her farklı girdi tensör
    /// şekli için derlenmiş kernel grafiği sayısı) ilk bellek düzeltmesinde 4'e kısılmıştı — ama asıl
    /// oversubscription'ın nedeni thread sayısıydı, cache'in payı görece küçüktü. Çok düşük bir
    /// cacheCapacity, klasördeki görseller farklı çözünürlüklerdeyse her yeni boyutta oneDNN'in kernel'i
    /// YENİDEN DERLEMESİNE yol açar (genelde asıl OCR hesabından bile pahalı) — bu yüzden 8'e çıkarıldı
    /// (orijinal varsayılan 10'un altında, oversubscription düzeltildiği için güvenli, ama daha az
    /// cache-miss/yeniden derleme).
    ///
    /// HIZ DENEMESİ (2026-08-07, kullanıcı onayıyla) — 2026-08-08'de GERİ ALINDI: `Enable180Classification:
    /// false` algılanan HER metin kutusu için ayrı bir "180° döndürülmüş mü?" sınıflandırma modeli
    /// geçişini atlıyordu (kutu sayısıyla orantılı gerçek bir hız kazancı vardı). Gerçek DECO KIDS WEAR
    /// klasörüyle (testFto3, 65 görsel) doğrulanan vaka: yoğun çok-panelli katalog görsellerinde kalın/
    /// kompakt stil-numarası kutuları ("27-958" gibi) DB algılama aşamasında bazen 180° ters açıyla
    /// tespit ediliyor — bu, görselin kendisinin ters olmasıyla İLGİLİ DEĞİL, o tek kutunun en/boy
    /// oranı yüzünden algılayıcının açı kararı belirsiz kalması. Sınıflandırma kapalıyken bu ters
    /// kutu düzeltilmeden tanıyıcıya gidiyor ve tutarlı biçimde yanlış okunuyor ("27-948" → "846-4Z"/
    /// "846-28" gibi, %78-93 güvenle — YANLIŞ ama YÜKSEK güvenli, bu yüzden sessizce yanlış kod
    /// eşleşmesi riski de var). 3/3 örnek görselde Enable180Classification=true ile kod okuma
    /// %99-100 güvenle DOĞRU çıktı (bkz. deney: reflect_probe, scratchpad). Bu tam olarak yukarıdaki
    /// notun öngördüğü geri alma koşuluydu ("gerçek klasörlerle kod okuma oranı DÜŞERSE... true'ya
    /// geri alınmalı") — sadece "ters çekilmiş fotoğraf" değil, "tek kutu açı belirsizliği" olarak
    /// gerçekleşti. Hız kaybı kabul edilebilir bulundu (bkz. Worker.cs OCR loglarındaki görsel başına
    /// süre); tekrar denenmek istenirse bu not ve commit mesajı başlangıç noktası olsun.
    ///
    /// cacheCapacity GERİ DÜŞÜRÜLDÜ (2026-08-08): üretim sunucusunun gerçekte 8 değil **6 çekirdekli**
    /// olduğu ve SQL Server/IIS ile PAYLAŞILDIĞI anlaşıldı (CLAUDE.md'deki "8 vCPU, 2026-07-28 teyit"
    /// notu güncel değilmiş ya da sunucu değişmiş). 6 çekirdekte eski `ProcessorCount-1` formülü 5
    /// adanmış örnek açıyordu; 5 örnek × cacheCapacity 8 = 40 kernel-cache yuvası, üretimde servis
    /// yeniden 23 GB/%95 belleğe çıktığı (aynı, ilk şikayetteki rakam) canlı olarak gözlemlendi —
    /// yani "asıl neden thread sayısıydı, cache payı küçük" varsayımı (yukarıdaki HIZ NOTU) hatalıydı
    /// ya da en azından tek başına yeterli değildi. `consumerCount` artık Worker.cs'te daha düşük
    /// (paylaşılan/az çekirdekli sunucuya göre), `cacheCapacity` da parametre yapılıp varsayılanı
    /// düşürüldü — appsettings.json'daki "OcrCacheCapacity" ile rebuild gerektirmeden ayarlanabilir.</summary>
    public PaddleScanOcr(FullOcrModel model, string candidatePattern, int consumerCount, int cacheCapacity = 4)
    {
        _queue = new QueuedPaddleOcrAll(
            () => new PaddleOcrAll(model, PaddleDevice.Mkldnn(cacheCapacity: cacheCapacity, cpuMathThreadCount: 1))
            {
                AllowRotateDetection = true,
                Enable180Classification = true,
                Detector = { MaxSize = 1600 },
            },
            consumerCount: consumerCount);
        _ownsQueue = true;
        _candidate = new Regex(candidatePattern, RegexOptions.Compiled);
    }

    /// <summary>"Ödünç alan" örnek: başka bir PaddleScanOcr'ın kurduğu paylaşılan kuyruyu kullanır,
    /// Dispose'da KAPATMAZ (kuyruk tek bir sahibe ait). Havuzun geri kalan yuvalarını doldurmak için.</summary>
    public PaddleScanOcr(PaddleScanOcr shared)
    {
        _queue = shared._queue;
        _ownsQueue = false;
        _candidate = shared._candidate;
    }

    public ScanResult FindProductCodes(string imagePath, IReadOnlySet<string> excelCodes,
        IReadOnlyDictionary<string, string>? descriptions = null)
    {
        var tokens = Scan(imagePath);
        var extracted = ExtractCandidates(tokens, _candidate);
        var matches = MatchExact(extracted, excelCodes);
        var candidates = extracted.Keys.ToList();

        // Son çare: Excel'deki HENÜZ eşleşmemiş kodları OCR adaylarında 1 karakter farkla ara
        // (bkz. FullScanOcr.cs v9-v11 notları — aynı mantık, motor bağımsız).
        var unmatchedCodes = new HashSet<string>(excelCodes, StringComparer.OrdinalIgnoreCase);
        unmatchedCodes.ExceptWith(matches.Select(m => m.Code));
        if (unmatchedCodes.Count > 0 && candidates.Count > 0)
        {
            var fuzzyCandidates = ComputeFuzzyCandidates(candidates, extracted, unmatchedCodes, matches);
            if (fuzzyCandidates.Count == 1 && !fuzzyCandidates.First().Value.SelfReferential)
            {
                var only = fuzzyCandidates.First();
                matches.Add(new CodeMatch(only.Key, only.Value.Conf * 0.6f, IsFuzzy: true));
            }
            else if (fuzzyCandidates.Count > 0 && descriptions is not null)
            {
                var ageRanges = ExtractAgeRanges(tokens);
                if (ageRanges.Count > 0)
                {
                    var anchorStyles = matches
                        .Select(m => descriptions.TryGetValue(m.Code, out var d) ? StyleTextWithoutAgeRange(d) : null)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var resolved = fuzzyCandidates.Keys
                        .Where(code => descriptions.TryGetValue(code, out var desc)
                            && ExtractAgeRangeFromDescription(desc) is { } descRange
                            && ageRanges.Any(r => r.Min == descRange.Min && r.Max == descRange.Max)
                            && anchorStyles.Contains(StyleTextWithoutAgeRange(desc)))
                        .ToList();
                    if (resolved.Count == 1)
                    {
                        var code = resolved[0];
                        var conf = fuzzyCandidates[code].Conf;
                        matches.Add(new CodeMatch(code, conf * 0.55f, IsFuzzy: true));
                    }
                }
            }
        }

        return new ScanResult(matches, candidates, tokens);
    }

    /// <summary>Tesseract'ın (FullScanOcr) aksine ayrı bir min-RGB kurtarma taraması yok — normal
    /// tek geçiş zaten marka yazısını genelde yakalıyor (bkz. dosya başı Flamindo notu), bu yüzden
    /// aynı taramayı tekrar kullanmak yeterli.</summary>
    public Dictionary<string, float> CollectBrandTokens(string imagePath) => Scan(imagePath);

    private Dictionary<string, float> Scan(string imagePath)
    {
        using var src = Cv2.ImDecode(File.ReadAllBytes(imagePath), ImreadModes.Color);
        // QueuedPaddleOcrAll.Run zaten kendi adanmış thread'ine kuyruklayıp orada çalıştırıyor;
        // burada senkron olarak beklemek (bu metodun IOcrEngine sözleşmesi gereği senkron olması
        // dışında) ek bir paralellik kaybı yaratmıyor -- OcrEnginePool zaten çağıranı (Worker.cs'in
        // Parallel.For'u) tek bir iş parçacığında bloke ediyordu.
        var result = _queue.Run(src).GetAwaiter().GetResult();

        var tokens = new Dictionary<string, float>();
        foreach (var region in result.Regions)
        {
            var word = region.Text.Trim().ToUpperInvariant();
            if (word.Length == 0) continue;
            var conf = region.Score * 100f;
            if (!tokens.TryGetValue(word, out var best) || conf > best)
                tokens[word] = conf;
        }
        return tokens;
    }

    // ---- Motor-bağımsız aday çıkarma/eşleştirme/fuzzy mantığı artık PaddleScanOcrMatching.cs'te
    // (partial class) — OpenCvSharp/Paddle bağımlılığı olmadığı için Tests projesine doğrudan
    // <Compile Include> ile dahil edilip birim testlerle doğrulanabiliyor (bkz. o dosyanın başı).

    public void Dispose() { if (_ownsQueue) _queue.Dispose(); }
}
