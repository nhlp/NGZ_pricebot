// PaddleScanOcrMatching.cs (2026-08-12) — PaddleScanOcr'ın motor-bağımsız aday çıkarma/eşleştirme/
// fuzzy/yaş-aralığı mantığı (eskiden PaddleScanOcr.cs içindeydi, "Aşağısı motor-bağımsız..." yorumuyla
// işaretliydi). Buraya, GeminiVisionClassifierLabelResolver.cs ile AYNI gerekçeyle ayrı bir dosyaya
// taşındı: OpenCvSharp/Sdcb.PaddleOCR'a hiç bağımlı değil, bu yüzden Tests projesine <Compile Include>
// ile doğrudan dahil edilip gerçek native OCR/model kurmadan birim testlerle doğrulanabiliyor —
// PaddleScanOcr.cs'teki Scan/constructor (native bağımlı) kısmı hâlâ orada.
//
// FUZZY KENDİ-KANIT (self-referential) AÇIĞI — "57-099/57-097" vakası (2026-08-12, gerçek üretim
// vakası, ALİSA PİYASA 3 İP / MESSINHO klasörü): v10.1 (bkz. FindProductCodes çağıran tarafındaki
// eski not) sadece "fuzzy adayın METNİ, zaten eşleşmiş bir kodun METNİYLE birebir aynı mı" kontrol
// ediyordu. Ama TryExtractHyphen/TryExtractLetterPrefix aynı OCR token'ından ("57-099") KISALTILMIŞ
// önek aileleri de üretiyor ("57-0", "57-09", "57-099") — bunlardan "57-09" (5 karakter), Excel'in
// GERÇEKTEN var olan ama görselde hiç yazmayan komşu bir stil kodu olan "57-097"ye (6 karakter)
// Levenshtein mesafe 1 çıkıyordu. "57-09" != "57-099" olduğu için eski selfRef kontrolü bunu
// BAĞIMSIZ kanıt sanıp "57-097"yi sessizce (güven 60, fuzzy) ikinci bir ürün kodu olarak ekledi —
// oysa görselde ikinci bir kod YOKTU, sadece "57-099"un aynı okumasının bir kısaltmasıydı. Fix:
// IsPrefixFamily — aynı tire konumunda, aynı önek metniyle, biri diğerinin kısaltması olan iki dizeyi
// de "aynı kanıt" sayar (bkz. aşağıdaki XML yorum). Ayrıca `anchors`/`scope` hesaplaması eskiden SADECE
// `long.TryParse` ile saf rakam kodlarda çalışıyordu — tire'li/harf önekli HERHANGİ bir kod
// (`long.TryParse("57-099", ...)` başarısız olduğu için) `anchors.Count == 0`'a düşürüp
// NearbyAnchorDelta kısıtlamasını (fuzzy aramayı SADECE numaraca yakın kodlarla sınırlama) sessizce
// TAMAMEN devre dışı bırakıyordu — SplitCodeKey bunu da tire'li/harf önekli kodlar için geri getirdi.
namespace PriceBotPipeline;

using System.Text.RegularExpressions;

public sealed partial class PaddleScanOcr
{
    // Tesseract->Paddle geçişinden kalma karışıklık-düzeltme tablosu ("O"->"0" gibi) — sadece
    // Normalize() kullanıyor, o da sadece bu dosyadaki saf mantık tarafından çağrılıyor; bu yüzden
    // (eskiden PaddleScanOcr.cs'in native kısmındaydı) buraya taşındı — orada artık kullanılmıyordu.
    private static readonly (char From, char To)[] Confusions =
    [
        ('O', '0'), ('D', '0'), ('Q', '0'),
        ('I', '1'), ('L', '1'),
        ('S', '5'),
        ('B', '8'),
        ('Z', '2'),
        ('G', '6'),
    ];

    /// <summary>`candidatePattern` (appsettings'ten gelen `\d{3,7}` derlenmiş Regex'i, PaddleScanOcr'ın
    /// `_candidate` alanı) parametre olarak alınır — bu dosyanın instance alanlarına (native kurulum
    /// PaddleScanOcr.cs'te) hiç bağımlı olmaması için bilinçli olarak STATİK tutuldu; Tests projesi bu
    /// dosyayı PaddleScanOcr.cs olmadan derliyor (bkz. dosya başı yorumu).</summary>
    private static Dictionary<string, float> ExtractCandidates(Dictionary<string, float> tokens, Regex candidatePattern)
    {
        var extracted = new Dictionary<string, float>();
        foreach (var (word, conf) in tokens)
        {
            // Tire'li token'ın kendisi ("26-158", "98-104" gibi) HER ZAMAN ayrı bir aday olarak
            // denenir — aşağıdaki SizeOrAgeRangeToken filtresinden bilinçli olarak ETKİLENMEZ
            // (bkz. TryExtractHyphen yorumu).
            TryExtractHyphen(word, conf, extracted);

            // Harf önekli kod ("V-029" gibi) da HER ZAMAN ayrı bir aday olarak denenir — yapısal
            // olarak bir harfle başladığı için SizeOrAgeRangeToken (saf rakam) ile asla çakışmaz,
            // aşağıdaki filtreden bilinçli olarak ETKİLENMEZ (bkz. TryExtractLetterPrefix yorumu).
            TryExtractLetterPrefix(word, conf, extracted);

            // Beden/yaş listesi ("134-140-146-152", "98-104" gibi) SAF RAKAM alt-dizisi
            // çıkarımına asla girmez (2026-08-03 vakası: "98-104" -> "104" gibi tesadüfi bir
            // rakam dizisi başka bir ürün koduyla yanlışlıkla eşleşebiliyordu).
            if (SizeOrAgeRangeToken.IsMatch(word)) continue;

            TryExtract(word, conf, extracted, candidatePattern);

            var normalized = Normalize(word);
            if (normalized != word)
            {
                TryExtractHyphen(normalized, conf * 0.95f, extracted);
                TryExtractLetterPrefix(normalized, conf * 0.95f, extracted);
                if (!SizeOrAgeRangeToken.IsMatch(normalized))
                    TryExtract(normalized, conf * 0.95f, extracted, candidatePattern);
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

    /// <summary>Tire'li kod hemen ardından başka bir rakam grubuyla (aynı OCR kutusuna birleşmiş
    /// bir alt satır — ör. "26-347" rozetinin hemen altındaki "6-9-12-18 AY/MONTH" yaş aralığı)
    /// boşluksuz bitişik gelirse `HyphenCandidate`'in açgözlü `\d{1,4}` grubu tire sonrasını
    /// olması gerekenden fazla yutar: "26-3476-9-12-18" içinden TEK eşleşme olarak "26-3476"
    /// çıkar, gerçek kod "26-347" hiç aday olmaz (gerçek vaka, 2026-08-09: DECO KIDS WEAR ürün
    /// fotoğrafı, Excel'de "26-347" varken hiçbir görsel eşleşmedi). Çözüm: `Matches` ile TÜM
    /// tire'li eşleşmeleri gez ve tire sonrası rakam grubunun her ÖNEKİNİ de ayrı bir aday olarak
    /// ekle ("26-3", "26-34", "26-347", "26-3476" gibi) — hangisinin gerçek kod olduğuna yine
    /// MatchExact'ın Excel'e karşı tam-eşitlik kontrolü karar verir, burada sadece doğru önek de
    /// aday havuzuna girsin diye fazladan olasılıklar üretiliyor.
    ///
    /// NOT (2026-08-12): bu üretilen önekler ("57-0"/"57-09"/"57-099" gibi), tam eşleşmesi
    /// bulunan bir kodun ("57-099") AYNI OCR okumasının kısaltmalarıdır — bağımsız kanıt DEĞİLDİR.
    /// `ComputeFuzzyCandidates`'taki `IsPrefixFamily` bunu tanıyıp bu tür önekleri "kendi-kanıt"
    /// (self-referential) sayar; bkz. dosya başı 57-099/57-097 notu.</summary>
    private static void TryExtractHyphen(string word, float conf, Dictionary<string, float> extracted)
    {
        foreach (Match m in HyphenCandidate.Matches(word))
        {
            var dash = m.Value.IndexOf('-');
            var tail = m.Value[(dash + 1)..];
            for (int len = 1; len <= tail.Length; len++)
            {
                var candidate = m.Value[..(dash + 1 + len)];
                if (!extracted.TryGetValue(candidate, out var best) || conf > best)
                    extracted[candidate] = conf;
            }
        }
    }

    /// <summary>Harf önekli stil-numarası kodları ("V-029", "A-102" gibi — gerçek vaka, 2026-08-10:
    /// "mini pakel" KIŞLIK ALİSA-PİYASA listesi, Excel kodları "V-025".."V-072") için ek bir aday
    /// deseni. `_candidate` (appsettings'ten gelen `\d{3,7}`) harf içermeyen SAF rakam dizisi arar;
    /// "V-029" token'ından yalnızca öneksiz "029" çıkar. Ama Excel'de kod TAM "V-029" string'i
    /// olarak saklandığı için (MatchExact tam string eşitliği kontrol eder, alt-dize değil)
    /// öneksiz "029" o kodla ASLA eşleşemez — bu şekil hiç ele alınmadığı sürece bu tür kod
    /// ailesindeki HİÇBİR görsel eşleşmez (gerçek vakada bir Excel'in 33 görselinin TAMAMI
    /// "ATANAMADI" kaldı). `HyphenCandidate`'in (rakam-tire-rakam) harf önekli sürümü; aynı
    /// bitişik-token riski (bkz. TryExtractHyphen yorumu — kod rozetinin hemen altına yaş aralığı
    /// boşluksuz bitişebilir) burada da geçerli olduğu için aynı önek-üretme stratejisi
    /// kullanılır. Bilinçli olarak SizeOrAgeRangeToken filtresinin DIŞINDA (TryExtractHyphen ile
    /// aynı gerekçe) — ama pratikte hiçbir zaman onunla çakışmaz, çünkü SizeOrAgeRangeToken saf
    /// rakamla başlamak zorundadır, bu desen ise bir harfle. Aynı "kendi-kanıt" notu TryExtractHyphen
    /// için geçerlidir, buradaki üretilen önekler için de geçerlidir.</summary>
    private static readonly Regex LetterPrefixCandidate = new(@"[A-Z]{1,3}-\d{1,5}", RegexOptions.Compiled);

    private static void TryExtractLetterPrefix(string word, float conf, Dictionary<string, float> extracted)
    {
        foreach (Match m in LetterPrefixCandidate.Matches(word))
        {
            var dash = m.Value.IndexOf('-');
            var prefix = m.Value[..dash];
            var tail = m.Value[(dash + 1)..];
            for (int len = 1; len <= tail.Length; len++)
            {
                var candidate = prefix + "-" + tail[..len];
                if (!extracted.TryGetValue(candidate, out var best) || conf > best)
                    extracted[candidate] = conf;
            }
        }
    }

    private static void TryExtract(string word, float conf, Dictionary<string, float> extracted, Regex candidatePattern)
    {
        var m = candidatePattern.Match(word);
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

    /// <summary>Bir kodu "harf/tire öneki + sondaki sayısal parça" olarak ayırır — hem saf rakam
    /// kodlarda ("6541") hem tire'li/harf önekli stil numaralarında ("57-099", "V-029") "numaraca
    /// yakın komşu" karşılaştırmasını mümkün kılar. Önceki sürüm `ComputeFuzzyCandidates`'ta
    /// SADECE `long.TryParse(code, ...)` kullanıyordu; bu, tire/harf içeren HER kodda başarısız
    /// olup `anchors.Count == 0`'a düşüyor ve aşağıdaki `NearbyAnchorDelta` kısıtlamasını (fuzzy
    /// aramayı sadece numaraca yakın kodlarla sınırlama) o kod ailesi için TAMAMEN devre dışı
    /// bırakıyordu (bkz. dosya başı 2026-08-12 notu — "57-099" anchor'ken scope, 3 delta içindeki
    /// komşular yerine Excel'deki eşleşmemiş TÜM 53 kodu kapsıyordu).</summary>
    private static (string Prefix, long? Number) SplitCodeKey(string code)
    {
        int dash = code.LastIndexOf('-');
        var numPart = dash >= 0 ? code[(dash + 1)..] : code;
        var prefix = dash >= 0 ? code[..(dash + 1)] : "";
        return long.TryParse(numPart, out var n) ? (prefix, (long?)n) : (prefix, null);
    }

    /// <summary>İki kod dizesi aynı tire-grubundan (TryExtractHyphen/TryExtractLetterPrefix'in
    /// ürettiği büyüyen-önek ailesi, ör. "57-0"/"57-09"/"57-099") mi geliyor — biri diğerinin
    /// kısaltılmış öneki mi? Gerçek vaka (2026-08-12): tam eşleşmeyle Excel'in "57-099" koduna
    /// bağlanan doğru okuma, aynı token'dan üretilen kısaltılmış "57-09" adayı üzerinden komşu bir
    /// Excel kodu olan "57-097"ye Levenshtein mesafe 1 çıkıyordu — "57-09" tam olarak "57-099"a
    /// eşit OLMADIĞI için eski selfRef kontrolü (salt string eşitliği) bunu bağımsız kanıt sanıp
    /// görselde hiç yazmayan "57-097"yi sessizce ikinci bir kod olarak kabul ediyordu. Tire'nin
    /// AYNI konumda olması ve önek metninin (tire dahil) birebir eşleşmesi şartı, alakasız kısa
    /// önek çakışmalarını ("5" ile "510" gibi) kasıtlı olarak dışarıda bırakır — sadece gerçekten
    /// TryExtractHyphen/TryExtractLetterPrefix'in ürettiği türden aileler eşleşir.</summary>
    private static bool IsPrefixFamily(string matchedCode, string candidate)
    {
        int dashA = matchedCode.IndexOf('-');
        int dashB = candidate.IndexOf('-');
        if (dashA < 0 || dashB < 0 || dashA != dashB) return false;
        if (!string.Equals(matchedCode[..dashA], candidate[..dashB], StringComparison.OrdinalIgnoreCase))
            return false;
        return matchedCode.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(matchedCode, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`internal` (private değil): OpenCvSharp/Paddle'a bağımlı olmadığı için Tests
    /// projesine bu dosya <Compile Include> ile doğrudan dahil ediliyor — testler bu metodu
    /// gerçek OCR/Scan hiç çalıştırmadan, sentetik aday/kod kümeleriyle doğrudan çağırıyor (bkz.
    /// PaddleScanOcrMatchingTests.cs). `private` olsaydı aynı derlemedeki test sınıfından dahi
    /// erişilemezdi (C#'ta private, kapsayan TÜR'e özeldir, derlemeye değil).</summary>
    internal static Dictionary<string, (float Conf, string Candidate, bool SelfReferential)> ComputeFuzzyCandidates(
        List<string> candidates, Dictionary<string, float> extracted,
        IReadOnlySet<string> excelCodes, IReadOnlyList<CodeMatch> alreadyMatchedOnThisImage)
    {
        var anchorKeys = alreadyMatchedOnThisImage
            .Select(m => SplitCodeKey(m.Code))
            .Where(k => k.Number.HasValue)
            .ToList();

        var scope = anchorKeys.Count == 0
            ? excelCodes
            : excelCodes
                .Where(code =>
                {
                    var key = SplitCodeKey(code);
                    return key.Number.HasValue && anchorKeys.Any(a =>
                        string.Equals(a.Prefix, key.Prefix, StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(a.Number!.Value - key.Number!.Value) <= NearbyAnchorDelta);
                })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var alreadyMatchedCodes = new HashSet<string>(
            alreadyMatchedOnThisImage.Select(m => m.Code), StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<string, (float Conf, string Candidate, bool SelfReferential)>();

        foreach (var code in scope)
        {
            // Bağımsız (self-referential OLMAYAN) bir aday VARSA — güveni ne olursa olsun,
            // kendi-kanıt adaylarından daha DÜŞÜK bile olsa — onu tercih ediyoruz: "SelfReferential"
            // bayrağının anlamı "bu kod için TEK açıklama, zaten eşleşmiş bir kodun kendi
            // okumasının bir parçası mı" — bağımsız TEK bir kanıt bile bunu yanlışlar. Sadece HİÇ
            // bağımsız aday yoksa en güvenli kendi-kanıt adayına düşülür (o zaman gerçekten
            // SelfReferential=true, yaş/stil çapraz doğrulaması olmadan kabul edilmemeli). Bu,
            // 2026-08-12 öncesi Dictionary ekleme sırasına bağlı örtük/kırılgan davranışın
            // ("57-09" mü "57-099" mu önce bulunur sıra selfRef sonucunu belirliyordu) yerini alır.
            (float Conf, string Candidate)? bestIndependent = null;
            (float Conf, string Candidate)? bestSelfReferential = null;

            foreach (var candidate in candidates)
            {
                if (extracted[candidate] < FuzzyMinConfidence) continue;
                if (Math.Abs(candidate.Length - code.Length) > 1) continue;
                if (LevenshteinDistance(candidate, code) != 1) continue;

                bool selfRef = alreadyMatchedCodes.Contains(candidate)
                    || alreadyMatchedCodes.Any(m => IsPrefixFamily(m, candidate));
                var conf = extracted[candidate];

                if (selfRef)
                {
                    if (bestSelfReferential is null || conf > bestSelfReferential.Value.Conf)
                        bestSelfReferential = (conf, candidate);
                }
                else
                {
                    if (bestIndependent is null || conf > bestIndependent.Value.Conf)
                        bestIndependent = (conf, candidate);
                }
            }

            if (bestIndependent is { } independent)
                found[code] = (independent.Conf, independent.Candidate, false);
            else if (bestSelfReferential is { } selfReferential)
                found[code] = (selfReferential.Conf, selfReferential.Candidate, true);
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
}
