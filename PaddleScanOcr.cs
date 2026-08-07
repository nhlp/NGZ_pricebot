// PaddleScanOcr.cs (2026-08-07) — PaddleOCR tabanlı IOcrEngine implementasyonu, Tesseract'a
// (FullScanOcr) alternatif. Spike sonucu (Spike/PaddleOcrProbe, gerçek üretim görselleriyle):
// PaddleOCR, Tesseract'ın ihtiyaç duyduğu ~10 farklı ön işleme kademesine (kontrast germe, yerel
// adaptif eşikleme, kenar-şerit/gri-dilim/min-RGB kurtarma taramaları — bkz. FullScanOcr.cs dosya
// başı) hiç gerek kalmadan TEK bir ham geçişte daha temiz ve daha hızlı sonuç verdi. Özellikle
// belgelenmiş Baby Flamindo dekoratif-logo vakasında (bkz. CLAUDE.md "Marka OCR kalibrasyonu")
// Tesseract'ın özel min-RGB kurtarma taraması gerektirdiği "FLAMINDO" yazısı, PaddleOCR'da normal
// taramada %99 güvenle doğrudan okundu (9/9 gerçek görsel). Bu yüzden bu dosyada Tesseract'ın
// preprocessing fonksiyonlarının HİÇBİRİ yok — sadece PaddleOcrAll.Run + aynı aday çıkarma/
// eşleştirme/fuzzy/yaş-aralığı mantığı.
//
// Aday çıkarma/eşleştirme (ExtractCandidates, MatchExact, ComputeFuzzyCandidates, yaş-aralığı v11
// tie-break, Levenshtein) FullScanOcr.cs'teki (v9-v11, kanıtlanmış) algoritmayla BİREBİR aynıdır —
// motor bağımsız oldukları için kasıtlı olarak buraya kopyalandı, FullScanOcr.cs'e HİÇ dokunulmadı.
// Amaç: appsettings.json'da "OcrEngine": "Tesseract" yazıp servisi yeniden başlatarak eski motora
// dönüşün risksiz/garantili olması — Tesseract tarafında satır bazında hiçbir değişiklik yok.
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

public sealed class PaddleScanOcr : IOcrEngine
{
    private readonly QueuedPaddleOcrAll _queue;
    private readonly bool _ownsQueue;
    private readonly Regex _candidate;

    private static readonly (char From, char To)[] Confusions =
    [
        ('O', '0'), ('D', '0'), ('Q', '0'),
        ('I', '1'), ('L', '1'),
        ('S', '5'),
        ('B', '8'),
        ('Z', '2'),
        ('G', '6'),
    ];

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
    /// HIZ DENEMESİ (2026-08-07, kullanıcı onayıyla): `Enable180Classification: false` — algılanan HER
    /// metin kutusu için ayrı bir "180° döndürülmüş mü?" sınıflandırma modeli geçişini atlar (kutu
    /// sayısıyla orantılı gerçek bir hız kazancı). Risk: bir görsel gerçekten baş aşağı gelirse o
    /// metin ters okunur/okunamaz — ürün fotoğrafları WhatsApp'tan geldiği için teorik olarak mümkün.
    /// Kullanıcı riski kabul ederek denemeyi istedi; gerçek klasörlerle kod okuma oranı DÜŞERSE
    /// (özellikle ters/yan çekilmiş fotoğraflarda kod kaçırma artarsa) true'ya geri alınmalı.</summary>
    public PaddleScanOcr(FullOcrModel model, string candidatePattern, int consumerCount)
    {
        _queue = new QueuedPaddleOcrAll(
            () => new PaddleOcrAll(model, PaddleDevice.Mkldnn(cacheCapacity: 8, cpuMathThreadCount: 1))
            {
                AllowRotateDetection = true,
                Enable180Classification = false,
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
        var extracted = ExtractCandidates(tokens);
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

    // ---- Aşağısı motor-bağımsız aday çıkarma/eşleştirme mantığı (eski FullScanOcr.cs'in
    // Tesseract kaldırıldıktan sonra hayatta kalan kısmı, bkz. IOcrEngine.cs dosya başı notu) ----

    private Dictionary<string, float> ExtractCandidates(Dictionary<string, float> tokens)
    {
        var extracted = new Dictionary<string, float>();
        foreach (var (word, conf) in tokens)
        {
            // Tire'li token'ın kendisi ("26-158", "98-104" gibi) HER ZAMAN ayrı bir aday olarak
            // denenir — aşağıdaki SizeOrAgeRangeToken filtresinden bilinçli olarak ETKİLENMEZ
            // (bkz. TryExtractHyphen yorumu).
            TryExtractHyphen(word, conf, extracted);

            // Beden/yaş listesi ("134-140-146-152", "98-104" gibi) SAF RAKAM alt-dizisi
            // çıkarımına asla girmez (2026-08-03 vakası: "98-104" -> "104" gibi tesadüfi bir
            // rakam dizisi başka bir ürün koduyla yanlışlıkla eşleşebiliyordu).
            if (SizeOrAgeRangeToken.IsMatch(word)) continue;

            TryExtract(word, conf, extracted);

            var normalized = Normalize(word);
            if (normalized != word)
            {
                TryExtractHyphen(normalized, conf * 0.95f, extracted);
                if (!SizeOrAgeRangeToken.IsMatch(normalized))
                    TryExtract(normalized, conf * 0.95f, extracted);
            }
        }
        return extracted;
    }

    /// <summary>Tire'li stil-numarası kodları ("26-158", "27-958" gibi — DECO KİDS vakası,
    /// 2026-08-07) için ek bir aday deseni. `_candidate` (appsettings'ten gelen `\d{3,7}`) tireyi
    /// asla eşleştirmez, ama Excel'deki kod aynen tire ile saklanıyorsa (ExcelPriceReader kodu
    /// olduğu gibi anahtarlar) saf rakam alt dizisi ("958") o kodla asla tam string eşitliği
    /// sağlayamaz. Bilinçli olarak SizeOrAgeRangeToken filtresinin (ExtractCandidates'taki
    /// "beden/yaş listesi" `continue`) DIŞINDA, HER token için çalıştırılır: "98-104" (gerçek
    /// beden aralığı) ile "26-158" (gerçek ürün kodu) AYNI şekle sahiptir, şekilden ayırt
    /// edilemez — MatchExact'ın Excel'e karşı tam-eşitlik kontrolü nihai hakemdir, burada risk
    /// (Excel'de GERÇEKTEN o tire'li değerde bir kod olması gerekir) zaten ihmal edilebilir.</summary>
    private static readonly Regex HyphenCandidate = new(@"\d{1,4}-\d{1,4}", RegexOptions.Compiled);

    private static void TryExtractHyphen(string word, float conf, Dictionary<string, float> extracted)
    {
        var m = HyphenCandidate.Match(word);
        if (m.Success && (!extracted.TryGetValue(m.Value, out var best) || conf > best))
            extracted[m.Value] = conf;
    }

    private void TryExtract(string word, float conf, Dictionary<string, float> extracted)
    {
        var m = _candidate.Match(word);
        if (!m.Success) return;
        if (!extracted.TryGetValue(m.Value, out var best) || conf > best)
            extracted[m.Value] = conf;
    }

    private static List<CodeMatch> MatchExact(Dictionary<string, float> extracted, IReadOnlySet<string> excelCodes) =>
        extracted.Keys
            .Where(excelCodes.Contains)
            .Select(c => new CodeMatch(c, extracted[c]))
            .OrderByDescending(m => m.Confidence)
            .ToList();

    private const float FuzzyMinConfidence = 40f;
    private const int NearbyAnchorDelta = 3;

    private static Dictionary<string, (float Conf, string Candidate, bool SelfReferential)> ComputeFuzzyCandidates(
        List<string> candidates, Dictionary<string, float> extracted,
        IReadOnlySet<string> excelCodes, IReadOnlyList<CodeMatch> alreadyMatchedOnThisImage)
    {
        var anchors = alreadyMatchedOnThisImage
            .Select(m => long.TryParse(m.Code, out var n) ? (long?)n : null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();

        var scope = anchors.Count == 0
            ? excelCodes
            : excelCodes
                .Where(code => long.TryParse(code, out var n) && anchors.Any(a => Math.Abs(a - n) <= NearbyAnchorDelta))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var alreadyMatchedCodes = new HashSet<string>(
            alreadyMatchedOnThisImage.Select(m => m.Code), StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<string, (float Conf, string Candidate, bool SelfReferential)>();

        foreach (var code in scope)
        {
            foreach (var candidate in candidates)
            {
                if (extracted[candidate] < FuzzyMinConfidence) continue;
                if (Math.Abs(candidate.Length - code.Length) > 1) continue;
                if (LevenshteinDistance(candidate, code) != 1) continue;

                bool selfRef = alreadyMatchedCodes.Contains(candidate);
                if (!found.TryGetValue(code, out var existing) || extracted[candidate] > existing.Conf)
                    found[code] = (extracted[candidate], candidate, selfRef);
            }
        }

        return found;
    }

    private const float AgeRangeMinConfidence = 50f;
    private static readonly Regex AgeRangeToken = new(@"^\d{1,2}([.\-]\d{1,2}){1,}$", RegexOptions.Compiled);

    private static List<(int Min, int Max)> ExtractAgeRanges(Dictionary<string, float> tokens)
    {
        var ranges = new List<(int Min, int Max)>();
        foreach (var (word, conf) in tokens)
        {
            if (conf < AgeRangeMinConfidence) continue;
            if (!AgeRangeToken.IsMatch(word)) continue;

            var parts = word.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);
            var numbers = new List<int>(parts.Length);
            bool allValid = true;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var n) || n is < 0 or > 99) { allValid = false; break; }
                numbers.Add(n);
            }
            if (!allValid || numbers.Count < 2) continue;

            ranges.Add((numbers.Min(), numbers.Max()));
        }
        return ranges;
    }

    private static readonly Regex DescriptionAgeRange =
        new(@"(\d{1,2})\s*-\s*(\d{1,2})\s*(yaş|yas|age)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (int Min, int Max)? ExtractAgeRangeFromDescription(string description)
    {
        var m = DescriptionAgeRange.Match(description);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var a) || !int.TryParse(m.Groups[2].Value, out var b)) return null;
        return (Math.Min(a, b), Math.Max(a, b));
    }

    private static string StyleTextWithoutAgeRange(string description) =>
        DescriptionAgeRange.Replace(description, "").Trim().ToUpperInvariant();

    private static int LevenshteinDistance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }

        return dp[a.Length, b.Length];
    }

    private static string Normalize(string token)
    {
        var chars = token.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            foreach (var (from, to) in Confusions)
                if (chars[i] == from) { chars[i] = to; break; }
        return new string(chars);
    }

    public void Dispose() { if (_ownsQueue) _queue.Dispose(); }
}
