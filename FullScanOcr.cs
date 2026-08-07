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
//
// v8 (performans, 2026-07-28): OCR/eşleştirme davranışı değişmeden iç yapı hızlandırıldı —
//  (a) piksel işlemleri SKBitmap.GetPixel/SetPixel (çağrı başına yüksek ek yük) yerine
//      span/byte-dizisi üzerinden yapılır; tüm ara görüntüler Bgra8888'e normalize edilip
//      gri değerler düz byte[] tamponlarında işlenir,
//  (b) kaynak görsel geçiş başına değil, görsel başına BİR kez decode edilir; kırpma +
//      ölçekleme + renk dönüşümü tek canvas çiziminde birleşir,
//  (c) hazırlanan görüntüler geçici PNG dosyası yerine bellekten (Pix.LoadFromMemory)
//      Tesseract'a verilir ve her hazırlanan görüntü 4 motor/psm geçişi için bir kez
//      yüklenir (eskiden aynı PNG diskten 4 kez okunuyordu),
//  (d) paralel klasör taraması için dosyanın sonunda FullScanOcrPool eklendi —
//      TesseractEngine thread-safe DEĞİLDİR, her FullScanOcr örneği aynı anda tek iş
//      parçacığı tarafından kullanılmalıdır; havuz bunu garanti eder,
//  (e) rakam-only motor artık koşullu: yalnızca normal motorun token'ları hiçbir Excel
//      koduyla eşleşmediğinde çalışır (kolay görsellerde geçiş sayısı yarıya iner) ve
//      marka taramasında (CollectBrandTokens) hiç çalışmaz — saf rakam token'ları marka
//      eşleştirmesinde kullanılamaz (BrandMatcher rakam→harf düzeltmesini yalnızca
//      harf+rakam KARIŞIK kelimelere uygular, bkz. BrandMatcher.cs).
//
// v9 (çoklu-kod eksik damgalama düzeltmesi, 2026-08-03): tüm ara-geçiş erken-çıkışları
// ("matches.Count == 0" ise devam et) TEK kod bulmanın yeterli olduğu varsayımıyla
// yazılmıştı. PriceStamper'ın çoklu-kod damgalaması (bkz. commit c998e90) eklenince bu
// varsayım geçersiz kaldı: aynı görselde birden fazla kod (yaş/beden grubu) meşru olabilir
// ve ilk (ucuz) geçiş bunlardan sadece bir kısmını okuyabilir — "matches.Count > 0" olunca
// pipeline durduğu için kalan kod(lar) hiç aranmadan atlanıyordu (gerçek vaka: Cuento
// 3-yaş-grubu etiketleri, 3 koddan sadece 1-2'si damgalandı). Düzeltme: (1) iki UCUZ
// tam-görsel geçiş (global kontrast germe + yerel adaptif eşikleme) artık ilk eşleşmede
// durmadan HER ZAMAN ikisi de çalışıp token biriktiriyor; (2) fuzzy son çare artık "hiç
// eşleşme yoksa" değil, görselde BAŞKA kod(lar) zaten exact eşleşmiş olsa bile HENÜZ
// eşleşmemiş Excel kodları için çalışıyor. Daha pahalı kurtarma geçişleri (kenar-şerit,
// gri-dilim — dekoratif/bubble fontlar için) bilinçli olarak "hiç eşleşme yoksa" gated
// bırakıldı; koşulsuz çalıştırmak v8'in çözdüğü OCR performans sorununu geri getirir. Yani
// aynı görselde hem kolay hem de gerçekten zor (dekoratif font gerektiren) bir kod varsa bu
// kalan senaryo teorik olarak hâlâ mümkündür — pratikte görülürse aynı yaklaşım o geçişlere
// de uygulanabilir.
namespace PriceBotPipeline;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SkiaSharp;
using Tesseract;


public sealed record CodeMatch(string Code, float Confidence, bool IsFuzzy = false);

/// <summary>Tokens: OCR geçişlerinde toplanan TÜM ham kelimeler → en yüksek güven skoru.
/// Ürün kodu eşleştirmesinin yan ürünüdür ama marka tespiti de (BrandMatcher) ek bir OCR
/// maliyeti ödemeden bu havuzu kullanır. İlk geçiş her zaman görselin tamamını taradığı
/// için marka logosu (okunabildiyse) bu havuzda bulunur.</summary>
public sealed record ScanResult(List<CodeMatch> Matches, List<string> Candidates, Dictionary<string, float> Tokens);

public sealed class FullScanOcr : IOcrEngine
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

    /// <param name="descriptions">v11: kod -> Excel satırındaki diğer hücrelerin ham metni
    /// (bkz. ExcelPriceReader.CodeColumnCandidate.Descriptions). Opsiyonel — verilmezse (null)
    /// yaş-aralığı çapraz doğrulaması hiç denenmez, davranış v10'dan farksız kalır.</param>
    public ScanResult FindProductCodes(string imagePath, IReadOnlySet<string> excelCodes,
        IReadOnlyDictionary<string, string>? descriptions = null)
    {
        using var src = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException($"Görsel açılamadı: {imagePath}");

        // v9: aynı görselde birden fazla ürün kodu meşru olabilir (yaş/beden grupları, bkz.
        // PriceStamper'ın çoklu-kod damgalaması) ve her kod farklı netlikte basılmış olabilir —
        // biri ilk geçişte kolayca okunurken diğeri hafifçe daha düşük kontrastlı/küçük olabilir.
        // Bu yüzden iki UCUZ tam-görsel geçiş (global kontrast germe + yerel adaptif eşikleme)
        // artık ilk eşleşme bulunduğunda durmuyor, ikisi de HER ZAMAN çalışıp token biriktiriyor
        // — üretim vakası (2026-08-03, Cuento 3-yaş-grubu etiketleri): ilk geçiş 3 koddan
        // sadece 1-2'sini okuyunca "eşleşme var" denip ikinci geçiş hiç denenmeden diğer
        // kod(lar) atlanıyordu. Daha PAHALI kurtarma geçişleri (kenar-şerit/gri-dilim, aşağıda)
        // bilinçli olarak "hiç eşleşme yoksa" gated bırakıldı — koşulsuz çalıştırmak her
        // görselin OCR süresini belirgin artırır (v8'in çözdüğü performans sorununu geri getirir).
        var tokens = CollectTokens(Preprocess(src), excelCodes);
        MergeTokens(tokens, CollectTokens(PreprocessAdaptive(src), excelCodes));

        var extracted = ExtractCandidates(tokens);
        var matches = MatchExact(extracted, excelCodes);

        if (matches.Count == 0)
        {
            // Üçüncü deneme: alt %35'lik şerit, 4x büyütme + adaptif eşikleme.
            // Kod bu şeritte değilse zaten hiçbir aday çıkmaz, zararsız.
            MergeTokens(tokens, CollectTokens(PreprocessEdgeStrip(src, fromTop: false), excelCodes));

            extracted = ExtractCandidates(tokens);
            matches = MatchExact(extracted, excelCodes);
        }

        if (matches.Count == 0)
        {
            // Dördüncü deneme: aynısı ama üst %35'lik şerit için — kod bazı etiketlerde
            // alt yerine üstte de olabilir (örn. marka logosunun hemen altında).
            MergeTokens(tokens, CollectTokens(PreprocessEdgeStrip(src, fromTop: true), excelCodes));

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
                MergeTokens(tokens, CollectTokens(PreprocessTileGray(src, fromTop, xFrom, xTo), excelCodes));

                extracted = ExtractCandidates(tokens);
                matches = MatchExact(extracted, excelCodes);
                if (matches.Count > 0) break;
            }
        }

        var candidates = extracted.Keys.ToList();

        // Son çare: Excel'deki HENÜZ eşleşmemiş kodları OCR adaylarında 1 karakter farkla ara.
        // v9: artık sadece "hiç exact eşleşme yoksa" değil, görselde BAŞKA bir kod zaten exact
        // eşleşmiş olsa bile çalışır — aksi halde aynı görseldeki, OCR'ın 1 haneyi yanlış
        // okuduğu diğer kod(lar) için fuzzy kurtarma hiç denenmiyordu (bkz. yukarıdaki v9 notu).
        // Zaten bulunan kodları arama kümesinden çıkarmak fuzzy'nin belirsizlik riskini de azaltır.
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
                // v11 (2026-08-06, Cuento sınır-vaka takibi): v10'un sayısal-yakınlık daraltması
                // ardışık kod bloklarının SINIRINDA hâlâ simetrik kalabiliyordu (ör. 4224 vs 4227 —
                // ikisi de zaten eşleşmiş 4225/4226'ya eşit mesafede). İLK denenen "sadece yaş-
                // aralığı eşleşsin" fikri TEK BAŞINA yetersiz çıktı: aynı katalogda çok sayıda
                // FARKLI ürün ailesi aynı standart yaş dilimlerini ("2-5 yaş", "6-9 yaş", "10-13
                // yaş") paylaşıyor — 4224 VE 4227 ikisi de "2-5 yaş" ile eşleşiyor, sadece stil
                // metni farklı ("düşük kol patlı" vs "parçalı biyeli"). Asıl ayırt edici sinyal
                // AİLE: aynı ürün ailesinin 3 farklı yaş-grubu satırı, açıklama metninde yaş
                // aralığı DIŞINDAKİ kısımda birebir aynıdır. Bu yüzden iki koşul birden aranır:
                // (1) adayın açıklamasındaki yaş aralığı, görselde OKUNAN aralıklardan biriyle
                // eşleşmeli, VE (2) adayın yaş-aralığı çıkarılmış "stil metni", bu görselde ZATEN
                // kesin eşleşmiş kod(lar)ın stil metniyle birebir aynı olmalı (aynı aile kanıtı).
                // İkisi birden sadece TEK bir adayı işaret ediyorsa kabul edilir. Gerçek vaka
                // doğrulaması: 4224/4227/4228/4229 hepsi (1)'i geçiyor ama sadece 4224 (2)'yi de
                // geçiyor (4225/4226 ile aynı "flam pamuk düşük kol patlı" ailesi) -> doğru kod.
                // Zıt örnek (4191 vs 4193, anchor 4192): ikisi de HEM yaş-aralığı eşleşiyor HEM
                // aynı aile stil metnine sahip (üçü de aynı ürünün 3 yaş grubu) -> gerçekten
                // ayırt edilemez, iki aday da kalır, doğru şekilde reddedilir (yanlış tahmin yok).
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
                        // Üçlü çıkarım (fuzzy mesafe + yaş-aralığı + aile stil metni) olduğu için
                        // v10'un 0.6 katsayısından biraz daha ihtiyatlı bir güven skoru kullanılır.
                        matches.Add(new CodeMatch(code, conf * 0.55f, IsFuzzy: true));
                    }
                }
            }
        }

        return new ScanResult(matches, candidates, tokens);
    }

    /// <summary>Marka-yazısı kurtarma taraması (v7): normal kod taraması markayı bulamadığında
    /// çağrılır. Gerçek vaka (Baby Flamindo, 2026-07-27): marka yazısı iki yerde olabiliyor —
    /// üstteki logo (harflerinin bir kısmı RENKLİ: sarı I, turuncu N, mavi yıldız A; luminans
    /// gri tonlamasında beyaz zeminde kaybolurlar) ve kıyafetin üzerindeki ton-üstü-ton
    /// nakış/baskı ("FLAMINDO" — normal taramada ancak "FLAMI" olarak kesik okunabildi).
    /// Min-RGB ("en koyu kanal") dönüşümü her renkli harfin en az bir koyu kanalını kullanarak
    /// kontrastı artırır. Üç geçiş: üst %20 bandın iki örtüşen dilimi (logo) + tam görsel
    /// (nakış/baskı). v6 dersi geçerli: eşikleme YOK, binarizasyon Tesseract'a bırakılır.
    /// Rakam-only motor burada hiç çalıştırılmaz (v8e) — marka araması için anlamsızdır.</summary>
    public Dictionary<string, float> CollectBrandTokens(string imagePath)
    {
        using var src = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException($"Görsel açılamadı: {imagePath}");

        var tokens = new Dictionary<string, float>();
        foreach (var (xFrom, xTo) in new[] { (0.0, 0.55), (0.45, 1.0) })
            MergeTokens(tokens, CollectTokens(PreprocessTileMinChannel(src, xFrom, xTo), excelCodes: null));

        MergeTokens(tokens, CollectTokens(PreprocessFullMinChannel(src), excelCodes: null));
        return tokens;
    }

    private static void MergeTokens(Dictionary<string, float> into, Dictionary<string, float> from)
    {
        foreach (var (word, conf) in from)
            if (!into.TryGetValue(word, out var best) || conf > best)
                into[word] = conf;
    }

    /// <summary>Normal motor SparseText/Auto ile her zaman çalışır; rakam-only motor İSTEĞE BAĞLIDIR
    /// (v8e): yalnızca excelCodes verilmişse VE normal motorun token'ları henüz hiçbir Excel koduyla
    /// exact eşleşmemişse çalışır — kolay görsellerde motor geçişi yarıya iner. Marka taraması
    /// (excelCodes=null) rakam motorunu hiç çalıştırmaz: saf rakam token'ları marka eşleştirmesinde
    /// kullanılmaz (BrandMatcher rakam→harf düzeltmesini sadece harf+rakam karışık kelimelere uygular).
    /// Hazırlanan PNG bellekten bir kez Pix'e yüklenir ve tüm geçişlerde aynı Pix kullanılır;
    /// aynı token birden fazla geçişte çıkarsa yüksek güven skoru tutulur.</summary>
    private Dictionary<string, float> CollectTokens(byte[] preparedPng, IReadOnlySet<string>? excelCodes)
    {
        var tokens = new Dictionary<string, float>();
        using var pix = Pix.LoadFromMemory(preparedPng);
        foreach (var psm in new[] { PageSegMode.SparseText, PageSegMode.Auto })
            foreach (var (word, conf) in ReadWords(_engine, pix, psm))
                if (!tokens.TryGetValue(word, out var best) || conf > best)
                    tokens[word] = conf;

        // Rakam motoru, İngilizce sözlük varsayımının bozduğu rakam dizilerini kurtarmak için var
        // (v3 notu); normal motor kodu zaten okuduysa katkısı kalmaz. Kod rakam motorundan daha
        // yüksek güvenle de okunabilirdi ama kademeli tasarımın geri kalanıyla tutarlı davranılır:
        // eşleşme bulunduysa daha fazla arama yapılmaz.
        if (excelCodes is not null && MatchExact(ExtractCandidates(tokens), excelCodes).Count == 0)
        {
            foreach (var psm in new[] { PageSegMode.SparseText, PageSegMode.Auto })
                foreach (var (word, conf) in ReadWords(_digitsEngine, pix, psm))
                    if (!tokens.TryGetValue(word, out var best) || conf > best)
                        tokens[word] = conf;
        }

        return tokens;
    }

    /// <summary>Kod, "code:317613" gibi bitişik bir önekle aynı token içinde gelebilir;
    /// tüm token yerine regex'in eşleştiği alt dizi aday olarak alınır. Ayrıca her token'ın
    /// harf/rakam-karışıklığı düzeltilmiş hâli de denenir (örn. "134S" -> "1345"), çünkü tek
    /// bir yanlış-okunan karakter regex'in tüm token'ı reddetmesine yol açar. Tire ile ayrılmış
    /// beden/yaş aralığı token'ları ("134-140-146-152", "2-3-4-5", "0-12-18-24") tamamen elenir —
    /// üretim vakası (2026-08-03): bu tür rakam listeleri hem Excel'in YAŞ/BEDEN sütununda hem
    /// görselin üzerinde basılı beden tablosunda aynı rakamlarla göründüğü için kod adayı
    /// sanılabiliyordu (gerçek örnek: "0-12-18-24" tire kaybolunca "0121824" olarak birleşip
    /// rastgele bir Excel koduna denk gelme riski taşıyordu).</summary>
    private Dictionary<string, float> ExtractCandidates(Dictionary<string, float> tokens)
    {
        var extracted = new Dictionary<string, float>();
        foreach (var (word, conf) in tokens)
        {
            if (SizeOrAgeRangeToken.IsMatch(word)) continue;

            TryExtract(word, conf, extracted);

            var normalized = Normalize(word);
            if (normalized != word && !SizeOrAgeRangeToken.IsMatch(normalized))
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

    /// <summary>v10 (2026-08-05, Cuento çoklu-yaş-grubu vakası): belirsizlik kontrolü artık TÜM
    /// Excel kod evreni yerine, bu görselde ZATEN kesin eşleşmiş kod(lar)a sayıca YAKIN kodlarla
    /// sınırlı taranır. Gerçek vaka: 4224/4225/4226 üç-yaş-grubu etiketinde 4225 ve 4226 kesin
    /// okunuyor, 4224 sadece "4225" adayının 1-hane-yanlış-okunmuş hâli olarak kurtarılabilirdi —
    /// ama Cuento'nun kod aralığı ardışık olduğu için (4224-4229 bandında art arda ürünler var)
    /// aynı "4225" adayı 4224/4227/4228/4229'un HEPSİNE 1 mesafede düşüyor, TÜM evrende belirsiz
    /// sayılıp hepsi reddediliyordu. Bu görselin ZATEN bulduğu 4225/4226'ya yakın kodlarla
    /// sınırlamak, uzak/alakasız kodların (görselde asla geçmeyen, başka bir üründen gelen
    /// "gürültü" eşleşmeleri) belirsizliğe katkısını keser. NOT: bu sınırlama tek başına HER
    /// vakayı çözmez — 4224 ile 4227 ikisi de aynı mesafede (4225'in bir ucu, 4226'nın bir ucu)
    /// olduğu için ardışık iki üç'lü grup sınırında hâlâ simetrik bir belirsizlik kalabilir; bunu
    /// kesin çözmek için görselin okuduğu yaş aralığı metnini ("2.3.4.5" vb.) Excel'in ürün
    /// açıklamasıyla çapraz doğrulamak gerekir — bkz. v11 (ComputeFuzzyCandidates artık TEK bir
    /// karar vermek yerine TÜM yarışan adayları döner, çağıran taraf v11 tie-break'i dener).
    /// Kesin eşleşme yoksa (anchor yok — görsel tamamen fuzzy'ye dayanıyorsa) eski (tüm evren)
    /// davranış korunur; hiçbir bağlam sinyali olmadan taramayı daraltmak temelsiz olurdu. Bu
    /// değişiklik sadece evreni KÜÇÜLTÜR (asla büyütmez), bu yüzden önceden reddedilen bir
    /// belirsizlik hâlâ belirsizse yine reddedilir — yanlış koda atama riski artmaz.</summary>
    private const int NearbyAnchorDelta = 3;

    /// <summary>v9/v10'daki TryFuzzyMatch'in devamı: artık TEK bir CodeMatch? değil, yarışan TÜM
    /// (kod -> en iyi aday) eşleşmelerini döner. Çağıran taraf (FindProductCodes) Count==1 VE
    /// kazanan aday "self-referential" DEĞİLSE doğrudan kabul eder; aksi halde (Count&gt;1 veya
    /// self-referential) v11 yaş-aralığı/aile-stili çapraz doğrulamasıyla belirsizliği kırmayı
    /// dener, o da başarısız olursa hiçbiri kabul edilmez.</summary>
    /// <remarks>SelfReferential (v10.1, 2026-08-06, ERAY KIDS testinde bulundu): kazanan adayın
    /// METNİ, bu görselde ZATEN kesin eşleşmiş bir kodun kendisiyle birebir aynıysa true. Ham
    /// dışlama yerine (o zaman Cuento 4224 düzeltmesi de kırılıyordu — 4224'ün TEK kanıtı, zaten
    /// eşleşmiş "4225" adayıydı) bu bir bayrak: kazanan kanıt, doğru okunan bir kodun sırf sayısal
    /// komşusu olduğu için "kanıt" sayılmışsa (herhangi iki ardışık tam sayı için Levenshtein
    /// mesafesi her zaman 1'dir — "6541" vs "6542"), bu TEK BAŞINA yeterli değildir; ek bir bağımsız
    /// sinyal (v11) doğrulamadan kabul edilmemeli. Gerçek vaka: tek-ürünlü "6541, 3-4-5-6 years"
    /// etiketinde hiç var olmayan "6542" sırf "6541" adayı yüzünden fuzzy ekleniyordu; Cuento'da ise
    /// aynı mekanizma (4225'in kendisi 4224 için "kanıt") v11 ile doğrulanınca DOĞRU çıkıyor. Yani
    /// ayrım ham mesafede değil, v11'in bağımsız yaş-aralığı+aile-stili kanıtında.</remarks>
    private static Dictionary<string, (float Conf, string Candidate, bool SelfReferential)> ComputeFuzzyCandidates(
        List<string> candidates, Dictionary<string, float> extracted,
        IReadOnlySet<string> excelCodes, IReadOnlyList<CodeMatch> alreadyMatchedOnThisImage)
    {
        var anchors = alreadyMatchedOnThisImage
            .Select(m => long.TryParse(m.Code, out var n) ? (long?)n : null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();

        // Anchor yoksa (bu görselde hiç kesin eşleşme yok) daraltma için hiçbir bağlam sinyali
        // yok — eski, tüm evreni tarayan davranış korunur.
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

        return found; // 0: hiç aday yok, 1: tek/kesin, >1: belirsiz -> çağıran taraf karar verir
    }

    /// <summary>v11: görselin üzerinde OKUNAN yaş/beden aralığı metinlerini ("2.3.4.5 years",
    /// "6.7.8.9 years" gibi — Cuento tarzı etiketlerde nokta ile ayrılmış, TİRE ile ayrılmışsa
    /// zaten SizeOrAgeRangeToken tarafından kod adayı çıkarımından elenip burada da kullanılabilir)
    /// (Min,Max) çiftleri olarak çıkarır. SizeOrAgeRangeToken'ın aksine bu bir FİLTRE değil,
    /// POZİTİF bir sinyaldir — ComputeFuzzyCandidates'ın belirsiz bıraktığı adaylar arasından
    /// Excel açıklamasıyla eşleşeni bulmak için kullanılır (bkz. FindProductCodes v11 notu).
    /// Güven eşiği var: bu çapraz-doğrulama son bir "tie-break" sinyali olduğu için düşük güvenli
    /// gürültü token'larının yanlış aralık üretmesi istenmez.</summary>
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

    /// <summary>Excel'in kod/fiyat dışındaki hücrelerinden (bkz. ExcelPriceReader.Descriptions)
    /// "2-5 yaş", "6-9 yas" gibi bir yaş/beden aralığı ifadesi ararsa (Min,Max) döner. Aralık
    /// ayracı sadece TİRE kabul edilir (Excel'de serbest metin, nokta ayraçlı yazım beklenmez —
    /// o sadece görsel üzerindeki dekoratif OCR okuması için geçerli).</summary>
    private static readonly Regex DescriptionAgeRange =
        new(@"(\d{1,2})\s*-\s*(\d{1,2})\s*(yaş|yas|age)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (int Min, int Max)? ExtractAgeRangeFromDescription(string description)
    {
        var m = DescriptionAgeRange.Match(description);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var a) || !int.TryParse(m.Groups[2].Value, out var b)) return null;
        return (Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>Açıklama metninden yaş-aralığı ifadesini ("2-5 yaş" vb.) çıkarıp geri kalanı
    /// döner — aynı ürün ailesinin farklı yaş-grubu satırları (ör. "2-5 yaş flam pamuk düşük kol
    /// patlı" / "6-9 yaş flam pamuk düşük kol patlı" / "10-13 yaş flam pamuk düşük kol patlı")
    /// bu işlemden sonra BİREBİR aynı metne ("FLAM PAMUK DÜŞÜK KOL PATLI") indirgenir. Bu,
    /// aynı yaş dilimini paylaşan ama FARKLI bir ürünü temsil eden satırlardan ("2-5 yaş flam
    /// pamuk parçalı biyeli") ayırt etmek için kullanılır (bkz. FindProductCodes v11 notu).</summary>
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

    private static IEnumerable<(string Word, float Conf)> ReadWords(TesseractEngine engine, Pix pix, PageSegMode psm)
    {
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

    // ---- Ön işleme (v8): tüm yollar "RenderRegion -> gri byte[] tamponu -> Gray8 PNG" ----

    /// <summary>2x büyütme + gri tonlama + global kontrast germe.
    /// WhatsApp/JPEG sıkıştırmasının yumuşattığı harf kenarlarını telafi eder.</summary>
    private static byte[] Preprocess(SKBitmap src)
    {
        using var scaled = RenderRegion(src, new SKRectI(0, 0, src.Width, src.Height), src.Width * 2, src.Height * 2);
        var gray = ToGrayBuffer(scaled);

        byte min = 255, max = 0;
        foreach (var g in gray)
        {
            if (g < min) min = g;
            if (g > max) max = g;
        }

        // Kontrast germe (autocontrast): [min,max] aralığını [0,255]'e yay
        float range = Math.Max(1, max - min);
        for (int i = 0; i < gray.Length; i++)
            gray[i] = (byte)((gray[i] - min) / range * 255);

        return EncodeGrayPng(gray, scaled.Width, scaled.Height);
    }

    /// <summary>Yerel (pencere bazlı) adaptif eşikleme + hafif dilation. Görselin geneli
    /// yüksek dinamik aralığa sahip olsa bile, kod bölgesindeki düşük YEREL kontrastı
    /// (örn. krem zemin üstü krem rakam) her pencerede kendi ortalamasına göre değerlendirerek
    /// ortaya çıkarır. Dilation, dekoratif "kesik çizgili" fontlardaki boşlukları köprüler.</summary>
    private static byte[] PreprocessAdaptive(SKBitmap src)
    {
        using var scaled = RenderRegion(src, new SKRectI(0, 0, src.Width, src.Height), src.Width * 2, src.Height * 2);
        var binary = AdaptiveThreshold(ToGrayBuffer(scaled), scaled.Width, scaled.Height);
        return EncodeGrayPng(binary, scaled.Width, scaled.Height);
    }

    /// <summary>Görselin alt VEYA üst %35'lik şeridini (tam genişlik) kırpıp 4x büyütür, sonra
    /// aynı adaptif eşikleme + dilation'ı uygular. Gerçek örneklerde ürün kodu hep bu şeritlerden
    /// birinde, kendi satırında duruyor (marka logosunun altında ya da fotoğrafın en altında);
    /// kırpma dikkat dağıtan diğer unsurları eler ve kalan küçük alanı çok daha yüksek oranda
    /// büyütmeyi mümkün kılar. Kod bu şeritte değilse bu basitçe aday üretmez, zararsızdır.</summary>
    private static byte[] PreprocessEdgeStrip(SKBitmap src, bool fromTop)
    {
        int stripHeight = Math.Max(1, (int)(src.Height * 0.35));
        int top = fromTop ? 0 : src.Height - stripHeight;

        using var scaled = RenderRegion(src, new SKRectI(0, top, src.Width, top + stripHeight), src.Width * 4, stripHeight * 4);
        var binary = AdaptiveThreshold(ToGrayBuffer(scaled), scaled.Width, scaled.Height);
        return EncodeGrayPng(binary, scaled.Width, scaled.Height);
    }

    /// <summary>Üst veya alt %20'lik bandın, yatayda [xFrom, xTo] aralığındaki dilimini kırpıp
    /// 4x büyütür ve SADECE gri tonlama uygular — eşikleme/kontrast germe yok (kalın yuvarlak
    /// fontları her tür eşikleme bozuyor, bkz. dosya başındaki v6 notu; binarizasyon Tesseract'ın
    /// kendi iç mekanizmasına bırakılır). Dilim dar tutulur ki çevredeki fotoğraf içeriği
    /// Tesseract'ın segmentasyonunu bozmasın.</summary>
    private static byte[] PreprocessTileGray(SKBitmap src, bool fromTop, double xFrom, double xTo)
    {
        int bandHeight = Math.Max(1, (int)(src.Height * 0.20));
        int top = fromTop ? 0 : src.Height - bandHeight;
        int x1 = (int)(src.Width * xFrom);
        int x2 = Math.Max(x1 + 1, (int)(src.Width * xTo));

        using var scaled = RenderRegion(src, new SKRectI(x1, top, x2, top + bandHeight), (x2 - x1) * 4, bandHeight * 4);
        var gray = ToGrayBuffer(scaled);
        return EncodeGrayPng(gray, scaled.Width, scaled.Height);
    }

    /// <summary>Üst %20'lik bandın [xFrom, xTo] dilimini kırpıp büyütür ve min-RGB
    /// ("en koyu kanal") gri dönüşümü uygular: piksel değeri = min(R, G, B). Beyaz zemin
    /// üzerindeki RENKLİ logo harflerini (sarı, turuncu, mavi...) koyulaştırır — luminans
    /// gri tonlamasında bu harfler kaybolur (örn. sarı ≈ %89 parlaklık) ama en az bir
    /// kanalları her zaman koyudur. Eşikleme yok (bkz. v6 notu).</summary>
    private static byte[] PreprocessTileMinChannel(SKBitmap src, double xFrom, double xTo)
    {
        int bandHeight = Math.Max(1, (int)(src.Height * 0.20));
        int x1 = (int)(src.Width * xFrom);
        int x2 = Math.Max(x1 + 1, (int)(src.Width * xTo));
        int cropWidth = x2 - x1;

        // Sabit 4x yerine hedef genişliğe göre ölçek: kaynak görsel zaten yüksek çözünürlükse
        // 4x aşırı büyütür ve Tesseract'ın segmentasyonunu bozar (harf yüksekliği optimalin
        // çok üstüne çıkar); küçük görsellerde ise 4x'e kadar çıkılır.
        int factor = Math.Clamp((int)Math.Ceiling(2200.0 / cropWidth), 1, 4);
        using var scaled = RenderRegion(src, new SKRectI(x1, 0, x2, bandHeight), cropWidth * factor, bandHeight * factor);
        var mono = ToMinChannelBuffer(scaled);
        return EncodeGrayPng(mono, scaled.Width, scaled.Height);
    }

    /// <summary>Tam görsel, 2x + min-RGB gri — kıyafet üzerindeki ton-üstü-ton nakış/baskı
    /// marka yazısı için (konumu görselden görsele değiştiği için kırpılmaz).</summary>
    private static byte[] PreprocessFullMinChannel(SKBitmap src)
    {
        using var scaled = RenderRegion(src, new SKRectI(0, 0, src.Width, src.Height), src.Width * 2, src.Height * 2);
        var mono = ToMinChannelBuffer(scaled);
        return EncodeGrayPng(mono, scaled.Width, scaled.Height);
    }

    /// <summary>Kaynağın verilen bölgesini tek canvas çiziminde kırpar + hedef boyuta ölçekler
    /// ve renk tipini Bgra8888'e normalize eder (decode edilen görselin kendi renk tipi ne
    /// olursa olsun) — sonraki span okumaları bu sabit yerleşime güvenir.</summary>
    private static SKBitmap RenderRegion(SKBitmap src, SKRectI srcRect, int outWidth, int outHeight)
    {
        var dst = new SKBitmap(new SKImageInfo(outWidth, outHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(src, new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom),
            new SKRect(0, 0, outWidth, outHeight), paint);
        return dst;
    }

    /// <summary>Bgra8888 bitmap'ten luminans gri tamponu (satır başına RowBytes hizalamasına dikkat ederek).</summary>
    private static byte[] ToGrayBuffer(SKBitmap bgra)
    {
        int w = bgra.Width, h = bgra.Height, rowBytes = bgra.RowBytes;
        var span = bgra.GetPixelSpan();
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            var row = span.Slice(y * rowBytes, w * 4);
            int o = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = x * 4; // B, G, R, A
                gray[o + x] = (byte)((299 * row[p + 2] + 587 * row[p + 1] + 114 * row[p]) / 1000);
            }
        }
        return gray;
    }

    /// <summary>Bgra8888 bitmap'ten min-RGB ("en koyu kanal") gri tamponu.</summary>
    private static byte[] ToMinChannelBuffer(SKBitmap bgra)
    {
        int w = bgra.Width, h = bgra.Height, rowBytes = bgra.RowBytes;
        var span = bgra.GetPixelSpan();
        var mono = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            var row = span.Slice(y * rowBytes, w * 4);
            int o = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = x * 4;
                mono[o + x] = Math.Min(row[p], Math.Min(row[p + 1], row[p + 2]));
            }
        }
        return mono;
    }

    /// <summary>Gri tamponu Gray8 bitmap'e yazıp PNG olarak belleğe encode eder (disk yok).
    /// Gray8 PNG encode'u desteklenmezse Bgra8888'e çevirip tekrar dener.</summary>
    private static byte[] EncodeGrayPng(byte[] gray, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
        var ptr = bmp.GetPixels();
        int rowBytes = bmp.RowBytes;
        if (rowBytes == w)
        {
            Marshal.Copy(gray, 0, ptr, w * h);
        }
        else
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(gray, y * w, IntPtr.Add(ptr, y * rowBytes), w);
        }

        using (var img = SKImage.FromBitmap(bmp))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
        {
            if (data is not null) return data.ToArray();
        }

        using var converted = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(converted))
            canvas.DrawBitmap(bmp, 0, 0);
        using var img2 = SKImage.FromBitmap(converted);
        using var data2 = img2.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("PNG encode başarısız.");
        return data2.ToArray();
    }

    /// <summary>Gri tampon üzerinde yerel (pencere bazlı) adaptif eşikleme + hafif dilation;
    /// sonuç 0 (yazı adayı) / 255 (zemin) değerli bir tampondur.</summary>
    private static byte[] AdaptiveThreshold(byte[] gray, int w, int h)
    {
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

        return Dilate(binary, w, h, radius: 1);
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

    public void Dispose()
    {
        _engine.Dispose();
        _digitsEngine.Dispose();
    }
}

/// <summary>Sabit boyutlu FullScanOcr havuzu. TesseractEngine thread-safe DEĞİLDİR; paralel
/// görsel taraması, her biri aynı anda tek iş parçacığınca kullanılan bağımsız motor örnekleri
/// gerektirir. Motor kurulumu pahalı olduğu için örnekler bir kez (başlangıçta) oluşturulur;
/// Run() bir örneği ödünç alır, iş bitince havuza geri koyar.</summary>
public sealed class FullScanOcrPool : IDisposable
{
    private readonly BlockingCollection<FullScanOcr> _pool = [];

    public int Size { get; }

    public FullScanOcrPool(string tessdataPath, string candidatePattern, int size)
    {
        Size = Math.Max(1, size);
        for (int i = 0; i < Size; i++)
            _pool.Add(new FullScanOcr(tessdataPath, candidatePattern));
    }

    public T Run<T>(Func<FullScanOcr, T> work)
    {
        var ocr = _pool.Take();
        try { return work(ocr); }
        finally { _pool.Add(ocr); }
    }

    public void Dispose()
    {
        // BlockingCollection'ın enumerator'ı tüketmeyen bir anlık görüntü döner.
        foreach (var ocr in _pool) ocr.Dispose();
        _pool.Dispose();
    }
}
