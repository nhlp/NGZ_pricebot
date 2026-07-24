// FullScanOcr.cs — Tam-görsel OCR (v5). 3 gerçek fotoğrafla %100,
// kullanıcının 28'lik setiyle %93 doğrulandı. Konumdan bağımsızdır:
// tüm görsel kelime kelime taranır, Excel listesi gürültü filtresidir.
//
// v3 eklentileri (skip oranını düşürmek için):
//  1) Harf/rakam karışıklığı normalizasyonu (S->5, O->0, I/L->1, B->8, Z->2, G->6)
//     — hâlâ Excel listesiyle EXACT match, risk yok, sadece regex'in reddettiği
//     token'ları ("134S" gibi) kurtarır.
//  2) Sadece-rakam whitelist'li ikinci bir Tesseract motoru — İngilizce sözlük
//     varsayımının harf/rakam karışıklığına yol açtığı durumları azaltır.
//  3) Yukarıdakiler de bulamazsa, Excel'deki bilinen kodları OCR çıktısında
//     Levenshtein mesafesi <=1 ile arayan bir "fuzzy" son çare — SADECE tek ve
//     belirsiz olmayan bir aday varsa kabul edilir, IsFuzzy=true ile işaretlenir
//     (çağıran taraf bunu loglarda/raporda ayrıca göstermeli).
//
// v4 eklentisi: bazı ürün etiketlerinde kod, kesik/parçalı çizgili dekoratif bir
// fontla ve arka planla düşük yerel kontrastla basılıyor (örn. krem zemin üstü
// krem rakam). Standart Preprocess() TÜM görsel için TEK bir global min/max
// kontrast germe uyguluyor — görselin başka bir yerinde saf beyaz/siyah piksel
// varsa global aralık zaten geniş çıkıyor ve kod bölgesindeki yerel düşük
// kontrast hiç güçlenmiyor. Bu görsellerde aynı pikseli tekrar tekrar OCR'dan
// geçirmek hiçbir şeyi değiştirmez (Tesseract deterministiktir). Bu yüzden ilk
// geçiş hiç aday bulamazsa, SADECE o zaman devreye giren ikinci bir preprocessing
// yolu eklendi: yerel (pencere bazlı) adaptif eşikleme + kesik font çizgilerini
// birleştiren hafif bir dilation (genişletme).
//
// v5 eklentisi: gerçek örneklerde (Baby Flamindo etiketleri) kod görselin ALT
// şeridinde, kendi satırında duruyor ("AGE / 3-4-5-6" ile birlikte) — ama bazı
// etiketlerde kod üst şeritte (örn. marka logosunun hemen altında) da olabilir.
// Tam görsel üzerindeki iki deneme de (global kontrast + adaptif eşikleme)
// başarısız olursa, SADECE o zaman devreye giren üçüncü ve dördüncü kurtarma
// denemeleri eklendi: önce alt %35'lik şerit, sonra (o da bulamazsa) üst %35'lik
// şerit — ikisi de kırpılıp çok daha yüksek oranda (4x) büyütülüp adaptif eşikleme
// + dilation uygulanıyor. Kırpma, fotoğrafın geri kalanındaki model/renk varyantı
// küçük resimleri gibi dikkat dağıtan diğer sayıları da eler. Bu denemeler de
// bulamazsa pipeline yine normal şekilde fuzzy son çareye / atlanmaya düşer —
// yani kodun tamamen başka bir yerde olduğu etiketlere hiçbir zarar vermez,
// sadece ek bir şans.
//
// v6 eklentisi: adaptif eşikleme + dilation, kalın yuvarlak (bubble) fontlarda ters
// etki yapıyor — Bebly "20020" etiketiyle kanıtlandı: aynı kırpılmış bölge düz gri
// tonlamayla verilince Tesseract kodu kusursuz okurken, adaptif eşiklemeden geçince
// tanınmaz hâle geliyor (rakamların iç boşlukları/konturları bozuluyor). Bu yüzden
// beşinci bir kurtarma denemesi eklendi: üst/alt %20'lik bandın YATAYDA ÖRTÜŞEN
// üçte-birlik dilimleri, eşikleme uygulanmadan (sadece kırpma + 4x + gri tonlama)
// tek tek denenir. Dilimlerin dar olması şart: tam genişlik şerit gri tonlamayla da
// çalışmıyor, yanlardaki fotoğraf içeriği Tesseract'ın segmentasyonunu bozuyor
// (Bebly 20020 ile ölçüldü). Ayrıca fuzzy son çareye asgari güven eşiği eklendi:
// gri dilimlerin kumaş/dantel dokusundan ürettiği çöp adaylar (güven ~5) tesadüfen
// bir Excel koduna 1 mesafede olabiliyor ve tek aday olarak fuzzy'den geçebiliyordu
// (gerçek vaka: "51005" -> 5005). Sıralama bilinçli: önce mevcut adaptif geçişler
// (kesik-çizgili/düşük kontrastlı fontlar), bulamazlarsa gri dilimler (bubble fontlar).
namespace PriceBotPipeline;

using System.Text.RegularExpressions;
using SkiaSharp;
using Tesseract;


public sealed record CodeMatch(string Code, float Confidence, bool IsFuzzy = false);
public sealed record ScanResult(List<CodeMatch> Matches, List<string> Candidates, string RawTokens);

public sealed class FullScanOcr : IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly TesseractEngine _digitsEngine;
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

    public FullScanOcr(string tessdataPath, string candidatePattern)
    {
        _engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);

        _digitsEngine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);
        _digitsEngine.SetVariable("tessedit_char_whitelist", "0123456789");

        _candidate = new Regex(candidatePattern, RegexOptions.Compiled);
    }

    public ScanResult FindProductCodes(string imagePath, IReadOnlySet<string> excelCodes)
    {
        var prepared = new List<string>(3) { Preprocess(imagePath) };
        try
        {
            var tokens = CollectTokens(prepared[0]);
            var extracted = ExtractCandidates(tokens);
            var matches = MatchExact(extracted, excelCodes);

            if (matches.Count == 0)
            {
                // İkinci deneme: yerel adaptif eşikleme + çizgi-köprüleme. Sadece
                // ilk (global kontrast germeli) geçiş hiçbir Excel koduyla exact
                // eşleşmediğinde çalışır — düşük yerel kontrastlı / kesik-çizgili
                // dekoratif fontları kurtarmak için.
                prepared.Add(PreprocessAdaptive(imagePath));
                MergeTokens(tokens, CollectTokens(prepared[^1]));

                extracted = ExtractCandidates(tokens);
                matches = MatchExact(extracted, excelCodes);
            }

            if (matches.Count == 0)
            {
                // Üçüncü deneme: alt %35'lik şerit, 4x büyütme + adaptif eşikleme.
                // Kod bu şeritte değilse zaten hiçbir aday çıkmaz, zararsız.
                prepared.Add(PreprocessEdgeStrip(imagePath, fromTop: false));
                MergeTokens(tokens, CollectTokens(prepared[^1]));

                extracted = ExtractCandidates(tokens);
                matches = MatchExact(extracted, excelCodes);
            }

            if (matches.Count == 0)
            {
                // Dördüncü deneme: aynısı ama üst %35'lik şerit için — kod bazı etiketlerde
                // alt yerine üstte de olabilir (örn. marka logosunun hemen altında).
                prepared.Add(PreprocessEdgeStrip(imagePath, fromTop: true));
                MergeTokens(tokens, CollectTokens(prepared[^1]));

                extracted = ExtractCandidates(tokens);
                matches = MatchExact(extracted, excelCodes);
            }

            if (matches.Count == 0)
            {
                // Beşinci deneme (v6): DAR gri tonlamalı dilimler — adaptif eşikleme OLMADAN,
                // sadece kırpma + 4x büyütme + gri. Kalın yuvarlak (bubble) fontları adaptif
                // eşikleme bozuyor; eşiklemesiz hâlde sorunsuz okunuyorlar. Tam genişlik şerit
                // de İŞE YARAMIYOR (yanlardaki fotoğraf içeriği Tesseract'ın segmentasyonunu
                // bozuyor — Bebly 20020 ile denendi), bu yüzden üst ve alt %20'lik bant,
                // yatayda birbiriyle örtüşen üçte-birlik dilimlere bölünür ve dilimler tek tek
                // denenir. Önce üst-orta (Bebly tipi kolajlarda kodun olduğu yer), eşleşme
                // bulunan ilk dilimde durulur.
                foreach (var (fromTop, xFrom, xTo) in new[]
                {
                    (true,  0.25, 0.75), (true,  0.0, 0.5), (true,  0.5, 1.0),
                    (false, 0.25, 0.75), (false, 0.0, 0.5), (false, 0.5, 1.0),
                })
                {
                    prepared.Add(PreprocessTileGray(imagePath, fromTop, xFrom, xTo));
                    MergeTokens(tokens, CollectTokens(prepared[^1]));

                    extracted = ExtractCandidates(tokens);
                    matches = MatchExact(extracted, excelCodes);
                    if (matches.Count > 0) break;
                }
            }

            var candidates = extracted.Keys.ToList();

            // Son çare: exact match yoksa, Excel'deki bilinen kodları OCR adaylarında
            // 1 karakter farkla ara. Sadece tek ve belirsiz olmayan bir sonuç varsa kabul et.
            if (matches.Count == 0 && candidates.Count > 0)
            {
                var fuzzy = TryFuzzyMatch(candidates, extracted, excelCodes);
                if (fuzzy is not null)
                    matches.Add(fuzzy);
            }

            return new ScanResult(matches, candidates,
                                  string.Join(" ", tokens.Keys));
        }
        finally
        {
            foreach (var p in prepared) File.Delete(p);
        }
    }

    private static void MergeTokens(Dictionary<string, float> into, Dictionary<string, float> from)
    {
        foreach (var (word, conf) in from)
            if (!into.TryGetValue(word, out var best) || conf > best)
                into[word] = conf;
    }

    /// <summary>Normal motor + rakam-only motor, her ikisi de SparseText/Auto ile — 4 geçiş.
    /// Aynı token birden fazla geçişte çıkarsa yüksek güven skoru tutulur.</summary>
    private Dictionary<string, float> CollectTokens(string preparedImagePath)
    {
        var tokens = new Dictionary<string, float>();
        foreach (var psm in new[] { PageSegMode.SparseText, PageSegMode.Auto })
        {
            foreach (var (word, conf) in ReadWords(_engine, preparedImagePath, psm))
                if (!tokens.TryGetValue(word, out var best) || conf > best)
                    tokens[word] = conf;

            foreach (var (word, conf) in ReadWords(_digitsEngine, preparedImagePath, psm))
                if (!tokens.TryGetValue(word, out var best) || conf > best)
                    tokens[word] = conf;
        }
        return tokens;
    }

    /// <summary>Kod, "code:317613" gibi bitişik bir önekle aynı token içinde gelebilir;
    /// tüm token yerine regex'in eşleştiği alt dizi aday olarak alınır. Ayrıca her token'ın
    /// harf/rakam-karışıklığı düzeltilmiş hâli de denenir (örn. "134S" -> "1345"), çünkü tek
    /// bir yanlış-okunan karakter regex'in tüm token'ı reddetmesine yol açar.</summary>
    private Dictionary<string, float> ExtractCandidates(Dictionary<string, float> tokens)
    {
        var extracted = new Dictionary<string, float>();
        foreach (var (word, conf) in tokens)
        {
            TryExtract(word, conf, extracted);

            var normalized = Normalize(word);
            if (normalized != word)
                TryExtract(normalized, conf * 0.95f, extracted);
        }
        return extracted;
    }

    private static List<CodeMatch> MatchExact(Dictionary<string, float> extracted, IReadOnlySet<string> excelCodes) =>
        // Excel listesi = nihai filtre. Birden fazla kod meşru bir durumdur
        // (aynı görsel iki yaş grubunu temsil edebilir: 4171 + 4172).
        extracted.Keys
            .Where(excelCodes.Contains)
            .Select(c => new CodeMatch(c, extracted[c]))
            .OrderByDescending(m => m.Confidence)
            .ToList();

    private void TryExtract(string word, float conf, Dictionary<string, float> extracted)
    {
        var m = _candidate.Match(word);
        if (!m.Success) return;
        if (!extracted.TryGetValue(m.Value, out var best) || conf > best)
            extracted[m.Value] = conf;
    }

    /// <summary>Excel'deki her kodu OCR adaylarıyla Levenshtein mesafesi &lt;=1 için karşılaştırır.
    /// Yalnızca tek ve belirsiz olmayan bir eşleşme bulunursa döner (aksi halde null —
    /// yanlış ürüne fiyat atama riskini almamak için). Güveni düşük adaylar hiç değerlendirilmez:
    /// kumaş/dantel dokusundan çıkan çöp token'lar (güven ~5) tesadüfen bir Excel koduna 1 mesafede
    /// olabiliyor (gerçek vaka: dantelden okunan "51005" -> 5005 yanlış eşleşmesi) ve fuzzy tek
    /// aday görüp kabul ediyordu. Gerçek tek-karakter OCR hataları ("134S" gibi) makul güvenle
    /// gelir; bu eşik onları etkilemez.</summary>
    private const float FuzzyMinConfidence = 40f;

    private static CodeMatch? TryFuzzyMatch(List<string> candidates, Dictionary<string, float> extracted, IReadOnlySet<string> excelCodes)
    {
        var found = new Dictionary<string, (float Conf, string Candidate)>();

        foreach (var code in excelCodes)
        {
            foreach (var candidate in candidates)
            {
                if (extracted[candidate] < FuzzyMinConfidence) continue;
                if (Math.Abs(candidate.Length - code.Length) > 1) continue;
                if (LevenshteinDistance(candidate, code) != 1) continue;

                if (!found.TryGetValue(code, out var existing) || extracted[candidate] > existing.Conf)
                    found[code] = (extracted[candidate], candidate);
            }
        }

        if (found.Count != 1) return null; // 0: hiç aday yok, >1: belirsiz -> ikisinde de reddet

        var only = found.First();
        return new CodeMatch(only.Key, only.Value.Conf * 0.6f, IsFuzzy: true);
    }

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

    private static IEnumerable<(string Word, float Conf)> ReadWords(TesseractEngine engine, string pngPath, PageSegMode psm)
    {
        using var pix = Pix.LoadFromFile(pngPath);
        using var page = engine.Process(pix, psm);
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            var raw = iter.GetText(PageIteratorLevel.Word);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var word = raw.Trim().ToUpperInvariant();
            var conf = iter.GetConfidence(PageIteratorLevel.Word);
            yield return (word, conf);
        } while (iter.Next(PageIteratorLevel.Word));
    }

    /// <summary>2x büyütme + gri tonlama + global kontrast germe.
    /// WhatsApp/JPEG sıkıştırmasının yumuşattığı harf kenarlarını telafi eder.</summary>
    private static string Preprocess(string imagePath)
    {
        using var src = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException($"Görsel açılamadı: {imagePath}");

        var info = new SKImageInfo(src.Width * 2, src.Height * 2);
        using var scaled = src.Resize(info, SKFilterQuality.High);

        // Tek geçişte gri tonlama + min/max tespiti
        var gray = new byte[info.Width * info.Height];
        byte min = 255, max = 0;
        int i = 0;
        for (int y = 0; y < info.Height; y++)
        for (int x = 0; x < info.Width; x++, i++)
        {
            var c = scaled.GetPixel(x, y);
            var g = (byte)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
            gray[i] = g;
            if (g < min) min = g;
            if (g > max) max = g;
        }

        // Kontrast germe (autocontrast): [min,max] aralığını [0,255]'e yay
        using var mono = new SKBitmap(info);
        float range = Math.Max(1, max - min);
        i = 0;
        for (int y = 0; y < info.Height; y++)
        for (int x = 0; x < info.Width; x++, i++)
        {
            var v = (byte)((gray[i] - min) / range * 255);
            mono.SetPixel(x, y, new SKColor(v, v, v));
        }

        return SavePng(mono);
    }

    /// <summary>Yerel (pencere bazlı) adaptif eşikleme + hafif dilation. Görselin geneli
    /// yüksek dinamik aralığa sahip olsa bile, kod bölgesindeki düşük YEREL kontrastı
    /// (örn. krem zemin üstü krem rakam) her pencerede kendi ortalamasına göre değerlendirerek
    /// ortaya çıkarır. Dilation, dekoratif "kesik çizgili" fontlardaki boşlukları köprüler.</summary>
    private static string PreprocessAdaptive(string imagePath)
    {
        using var src = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException($"Görsel açılamadı: {imagePath}");

        var info = new SKImageInfo(src.Width * 2, src.Height * 2);
        using var scaled = src.Resize(info, SKFilterQuality.High);
        using var processed = AdaptiveThreshold(scaled);
        return SavePng(processed);
    }

    /// <summary>Görselin alt VEYA üst %35'lik şeridini (tam genişlik) kırpıp 4x büyütür, sonra
    /// aynı adaptif eşikleme + dilation'ı uygular. Gerçek örneklerde ürün kodu hep bu şeritlerden
    /// birinde, kendi satırında duruyor (marka logosunun altında ya da fotoğrafın en altında);
    /// kırpma dikkat dağıtan diğer unsurları eler ve kalan küçük alanı çok daha yüksek oranda
    /// büyütmeyi mümkün kılar. Kod bu şeritte değilse bu basitçe aday üretmez, zararsızdır.</summary>
    private static string PreprocessEdgeStrip(string imagePath, bool fromTop)
    {
        using var src = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException($"Görsel açılamadı: {imagePath}");

        int stripHeight = Math.Max(1, (int)(src.Height * 0.35));
        int top = fromTop ? 0 : src.Height - stripHeight;
        using var crop = new SKBitmap(src.Width, stripHeight);
        using (var canvas = new SKCanvas(crop))
            canvas.DrawBitmap(src, new SKRect(0, top, src.Width, top + stripHeight), new SKRect(0, 0, src.Width, stripHeight));

        var info = new SKImageInfo(crop.Width * 4, crop.Height * 4);
        using var scaled = crop.Resize(info, SKFilterQuality.High);
        using var processed = AdaptiveThreshold(scaled);
        return SavePng(processed);
    }

    /// <summary>Üst veya alt %20'lik bandın, yatayda [xFrom, xTo] aralığındaki dilimini kırpıp
    /// 4x büyütür ve SADECE gri tonlama uygular — eşikleme/kontrast germe yok (kalın yuvarlak
    /// fontları her tür eşikleme bozuyor, bkz. dosya başındaki v6 notu; binarizasyon Tesseract'ın
    /// kendi iç mekanizmasına bırakılır). Dilim dar tutulur ki çevredeki fotoğraf içeriği
    /// Tesseract'ın segmentasyonunu bozmasın.</summary>
    private static string PreprocessTileGray(string imagePath, bool fromTop, double xFrom, double xTo)
    {
        using var src = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException($"Görsel açılamadı: {imagePath}");

        int bandHeight = Math.Max(1, (int)(src.Height * 0.20));
        int top = fromTop ? 0 : src.Height - bandHeight;
        int x1 = (int)(src.Width * xFrom);
        int x2 = Math.Max(x1 + 1, (int)(src.Width * xTo));

        using var crop = new SKBitmap(x2 - x1, bandHeight);
        using (var canvas = new SKCanvas(crop))
            canvas.DrawBitmap(src, new SKRect(x1, top, x2, top + bandHeight), new SKRect(0, 0, crop.Width, crop.Height));

        var info = new SKImageInfo(crop.Width * 4, crop.Height * 4);
        using var scaled = crop.Resize(info, SKFilterQuality.High);
        using var processed = ToGrayscale(scaled);
        return SavePng(processed);
    }

    /// <summary>Sadece gri tonlama — eşikleme/kontrast germe yok. Kalın yuvarlak (bubble)
    /// fontlarda her tür eşikleme rakam konturlarını bozduğu için görüntü "dokunulmamış"
    /// bırakılır, binarizasyon Tesseract'ın kendi iç mekanizmasına kalır.</summary>
    private static SKBitmap ToGrayscale(SKBitmap scaled)
    {
        var outBitmap = new SKBitmap(new SKImageInfo(scaled.Width, scaled.Height));
        for (int y = 0; y < scaled.Height; y++)
        for (int x = 0; x < scaled.Width; x++)
        {
            var c = scaled.GetPixel(x, y);
            var g = (byte)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
            outBitmap.SetPixel(x, y, new SKColor(g, g, g));
        }
        return outBitmap;
    }

    /// <summary>Zaten ölçeklenmiş bir bitmap üzerinde yerel (pencere bazlı) adaptif eşikleme +
    /// hafif dilation uygular ve sonucu siyah/beyaz bir bitmap olarak döner.</summary>
    private static SKBitmap AdaptiveThreshold(SKBitmap scaled)
    {
        int w = scaled.Width, h = scaled.Height;

        var gray = new int[w * h];
        int i = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++, i++)
        {
            var c = scaled.GetPixel(x, y);
            gray[i] = (int)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
        }

        // Integral image: pencere ortalamasını O(1)'de hesaplamak için.
        var integral = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += gray[y * w + x];
                integral[(y + 1) * (w + 1) + (x + 1)] = integral[y * (w + 1) + (x + 1)] + rowSum;
            }
        }

        int window = Math.Clamp(Math.Min(w, h) / 8, 15, 61);
        int half = window / 2;
        const double t = 0.12; // eşik hassasiyeti: pikselin yerel ortalamaya göre ne kadar koyu olması gerektiği

        // Koyu = 0 (yazı adayı), açık = 255 (zemin adayı)
        var binary = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int x1 = Math.Max(0, x - half), x2 = Math.Min(w - 1, x + half);
            int y1 = Math.Max(0, y - half), y2 = Math.Min(h - 1, y + half);
            long sum = integral[(y2 + 1) * (w + 1) + (x2 + 1)] - integral[y1 * (w + 1) + (x2 + 1)]
                     - integral[(y2 + 1) * (w + 1) + x1] + integral[y1 * (w + 1) + x1];
            long count = (long)(x2 - x1 + 1) * (y2 - y1 + 1);
            double mean = (double)sum / count;

            binary[y * w + x] = gray[y * w + x] < mean * (1 - t) ? (byte)0 : (byte)255;
        }

        var dilated = Dilate(binary, w, h, radius: 1);

        var outBitmap = new SKBitmap(new SKImageInfo(w, h));
        i = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++, i++)
            outBitmap.SetPixel(x, y, dilated[i] == 0 ? SKColors.Black : SKColors.White);

        return outBitmap;
    }

    /// <summary>Binary görüntüde koyu (0) pikselleri komşularına yayar — dekoratif
    /// fontlardaki kesik çizgi boşluklarını köprülemek için basit bir max-filtre.</summary>
    private static byte[] Dilate(byte[] binary, int w, int h, int radius)
    {
        var result = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte value = 255;
            for (int dy = -radius; dy <= radius && value != 0; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= h) continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = x + dx;
                    if (nx < 0 || nx >= w) continue;
                    if (binary[ny * w + nx] == 0) { value = 0; break; }
                }
            }
            result[y * w + x] = value;
        }
        return result;
    }

    private static string SavePng(SKBitmap bitmap)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"scan_{Guid.NewGuid():N}.png");
        using var img = SKImage.FromBitmap(bitmap);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(tmp);
        data.SaveTo(fs);
        return tmp;
    }

    public void Dispose()
    {
        _engine.Dispose();
        _digitsEngine.Dispose();
    }
}
