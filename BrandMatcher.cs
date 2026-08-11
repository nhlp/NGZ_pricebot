namespace PriceBotPipeline;

/// <summary>AS_PWB_MarkaCarpan view'inden bir marka satırı. Fiyat formülü:
/// USD = ExcelFiyatı × NetCarpan ÷ Kur. Aynı marka adı view'de birden fazla satırda
/// olabilir (örn. HIPP, TABU — çarpanları aynı, zararsız); tüketici taraf tekilleştirir.
/// (Bu record, test projesi BrandMatcher.cs'i DB bağımlılığı olmadan derleyebilsin diye
/// NebimBrandProvider.cs'te değil burada tanımlı.)</summary>
public sealed record BrandMultiplier(string OnEk, string FullName, decimal NetCarpan);

/// <summary>OCR token'larından klasör markası tespiti sonucu. Brand null ise ya hiç eşleşme
/// yoktur (AmbiguousNames boş) ya da birbiriyle çelişen (farklı NetCarpan'lı) birden fazla
/// marka eşleşmiştir (AmbiguousNames dolu) — iki durumda da çağıran taraf kullanıcıya sorar.
/// Approximate=true: eşleşme kesik/hatalı okunmuş bir token'ın önek veya 1-harf-hata
/// kurtarmasıyla bulundu (rapora/loga ayrıca yansıtılmalı).</summary>
public sealed record OcrBrandOutcome(BrandMultiplier? Brand, List<string> MatchedWords, List<string> AmbiguousNames, bool Approximate = false);

/// <summary>WhatsApp'tan gelen marka cevabının eşleştirme sonucu. Brand null ise Suggestions,
/// tekrar sorulacak mesaja konacak en yakın marka adlarını içerir. Approximate=true, cevabın
/// birebir değil küçük yazım farkıyla (Levenshtein) eşleştiğini belirtir (rapora yazılır).</summary>
public sealed record UserBrandOutcome(BrandMultiplier? Brand, bool Approximate, List<string> Suggestions);

/// <summary>Marka adı eşleştirme mantığı — tamamen saf/deterministik, DB ve OCR bağımsız.
///
/// Temel ilkeler:
/// - OCR motoru İngilizce olduğu için etiketteki "LİLAX" OCR'dan "LILAX" olarak çıkar; bu
///   yüzden hem marka adları hem token'lar Türkçe karakterleri ASCII'ye indirger (İ→I, Ş→S...).
/// - Eşleşme KELİME bazlı tam eşitliktir, alt-dize değil: "PEPE" markası "PEPELINO" token'ına
///   eşleşmez (PEPELİNO ayrı bir markadır). Bitişik yazım için ayrıca adın boşluksuz hâli de
///   tek token olarak denenir ("BABYFLAMINDO").
/// - "BABY", "KİDS", "MİNİ", "MİSS" gibi onlarca markada geçen jenerik kelimeler tek başına
///   kanıt sayılmaz: eşleşen kelimelerden en az biri >= 4 harfli ve jenerik-dışı olmalı.
///   Tüm kelimeleri jenerik olan markalar ("MİNİ CLUP" gibi) OCR ile hiç eşleşemez —
///   bunlar için akış WhatsApp'tan sormaya düşer.
/// - Düşük güvenli OCR token'ları (kumaş/dantel dokusundan çıkan çöp) marka kanıtı olamaz
///   (FullScanOcr'daki fuzzy güven eşiğiyle aynı felsefe).</summary>
public static class BrandMatcher
{
    private const float MinWordConfidence = 40f;
    private const int MinDistinctiveLength = 4;

    /// <summary>Çocuk giyim markalarında o kadar yaygın kelimeler ki tek başlarına hangi
    /// markanın etikette olduğunu kanıtlayamazlar (normalize edilmiş hâlleriyle).</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "BABY", "BEBE", "BEBEK", "KIDS", "KID", "MINI", "MISS", "MY", "BY",
        "CLUB", "CLUP", "GIRL", "GIRLS", "BOY", "BOYS", "WEAR", "COLLECTION",
        "CLASS", "LOVE", "JUNIOR", "GOLD", "STAR", "SPORT", "LIFE", "TIME",
        "HAPPY", "LITTLE", "FAMILY",
        // "KIDSWEAR" (2026-08-11, gerçek vaka): "KIDS" ve "WEAR" ayrı ayrı jenerik, ama
        // birleşik yazıldığında ("Kids Wear" logosunun altındaki tagline'ı OCR boşluksuz tek
        // kelime olarak okuyunca, ör. Messinho/Deco etiketlerinde) kelime-eşitliği kontrolü
        // bunu görmüyordu. Onlarca üretici logosunda "... Kids Wear" jenerik alt-yazı olarak
        // geçiyor (marka adının kendisi değil); bu yüzden "ARD KİDSWEAR" gibi TEK ayırt edici
        // kelimesi "KIDSWEAR" olan bir marka, kendi ürünü hiç gelmese bile başka bir markanın
        // tag'indeki bu jenerik tagline'dan yanlışlıkla "bulundu" sayılıyordu (gerçek vaka:
        // ALİSA PİYASA MEVSİMLİK klasöründe DECO SPORT + MESSINHO ürünleri karışık geldi, her
        // ikisinin de logosu "... Kids Wear" tagline'ı taşıyor, OCR birini "KIDSWEAR" tek
        // token olarak okuyunca ARD KİDSWEAR de üçüncü çelişkili aday olarak sisteme girdi).
        // Not: bu, ARD KİDSWEAR'ı MİNİ CLUP ile aynı kategoriye sokuyor (tüm kelimeleri
        // jenerik marka, OCR ile asla otomatik eşleşmez, akış WhatsApp sorusuna düşer) — bilinçli
        // bir taviz, yanlış markaya yönlendirmemek yanlış hiç bulamamaktan daha güvenli.
        "KIDSWEAR",
    };

    private static bool IsDistinctive(string word) =>
        word.Length >= MinDistinctiveLength && !GenericWords.Contains(word);

    /// <summary>Metni büyük harfe çevirip Türkçe karakterleri ASCII'ye indirger ve
    /// harf/rakam-dışı her karakteri kelime ayracı sayar. "MOTHER&amp;ÇOJOK" → [MOTHER, COJOK],
    /// "BEBEDEX'S" → [BEBEDEX, S], "code:317613" → [CODE, 317613].</summary>
    public static List<string> NormalizeToWords(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var raw in text.ToUpperInvariant())
        {
            var ch = raw switch
            {
                'İ' or 'ı' => 'I',
                'Ş' or 'ş' => 'S',
                'Ç' or 'ç' => 'C',
                'Ö' or 'ö' => 'O',
                'Ü' or 'ü' => 'U',
                'Ğ' or 'ğ' => 'G',
                _ => raw,
            };

            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    public static string NormalizeJoined(string text) => string.Concat(NormalizeToWords(text));

    /// <summary>Klasördeki TÜM görsellerin OCR token'larının birleşiminden klasör markasını
    /// bulur (bir Gonderim klasörü her zaman tek markadır). Token sözlüğü FullScanOcr'ın
    /// topladığı ham kelime → güven eşlemesidir.
    ///
    /// Eşleşme kuralı (gerçek Baby Flamindo vakasıyla kalibre edildi, 2026-07-27): markanın
    /// TÜM ayırt edici kelimeleri token'larda bulunmalı; jenerik kelimeler ("BABY" vb.)
    /// zorunlu DEĞİL — ama ayırt edici kelimelerin en uzunu 5 harften kısaysa (örn. "COOL
    /// BABY"deki COOL) tesadüfi baskı/yazı eşleşmesine karşı adın tüm kelimeleri aranır.
    /// Hiç tam eşleşme yoksa son çare: kesik okunmuş önek ("FLAMI" → FLAMINDO) veya 1 harf
    /// hatalı okuma, SADECE tek bir markayı işaret ediyorsa Approximate=true ile kabul edilir.</summary>
    public static OcrBrandOutcome MatchFromOcrTokens(
        IReadOnlyDictionary<string, float> rawTokens,
        IReadOnlyList<BrandMultiplier> brands)
    {
        // Ham token'lar noktalama içerebilir ve parçalanabilir; her parça, geldiği token'ın
        // güveniyle (aynı kelime birden çok token'dan gelirse en yükseğiyle) kaydedilir.
        // Harf içeren kelimelerin rakam→harf düzeltilmiş varyantı da eklenir ("FLAM1NDO" →
        // "FLAMINDO") — dekoratif fontlarda Tesseract harfleri rakam sanabiliyor.
        var tokenWords = new Dictionary<string, float>(StringComparer.Ordinal);
        void AddWord(string word, float conf)
        {
            if (!tokenWords.TryGetValue(word, out var best) || conf > best)
                tokenWords[word] = conf;
        }
        foreach (var (raw, conf) in rawTokens)
        {
            foreach (var word in NormalizeToWords(raw))
            {
                AddWord(word, conf);
                if (word.Any(char.IsAsciiLetter) && word.Any(char.IsAsciiDigit))
                    AddWord(LetterizeDigits(word), conf * 0.95f);
            }
        }

        bool HasToken(string word) =>
            tokenWords.TryGetValue(word, out var conf) && conf >= MinWordConfidence;

        var matches = new List<(BrandMultiplier Brand, string JoinedName, List<string> Evidence)>();
        foreach (var brand in brands)
        {
            var nameWords = NormalizeToWords(brand.FullName).Where(w => w.Length >= 2).ToList();
            if (nameWords.Count == 0) continue;
            var joined = string.Concat(nameWords);
            var distinctive = nameWords.Where(IsDistinctive).ToList();

            if (distinctive.Count > 0 && distinctive.All(HasToken) &&
                (distinctive.Any(w => w.Length >= 5) || nameWords.All(HasToken)))
                matches.Add((brand, joined, nameWords.Where(HasToken).ToList()));
            else if (IsDistinctive(joined) && HasToken(joined))
                matches.Add((brand, joined, [joined]));
        }

        if (matches.Count > 0)
        {
            // Aynı ad birden fazla satırda olabilir (HIPP, TABU) — ada göre tekilleştir. Farklı
            // adlar kalsa bile NetCarpan'ları aynıysa fiyat açısından belirsizlik yoktur, devam
            // edilir; farklı çarpanlar kaldıysa klasör tek-marka olduğu için bu bir OCR gürültüsü
            // işaretidir ve kullanıcıya sormak üzere belirsiz dönülür.
            var distinct = matches
                .GroupBy(m => m.JoinedName)
                .Select(g => g.First())
                .ToList();

            var carpans = distinct.Select(m => m.Brand.NetCarpan).Distinct().ToList();
            if (carpans.Count > 1)
                return new OcrBrandOutcome(null, [], distinct.Select(m => m.Brand.FullName).ToList());

            var winner = distinct.OrderByDescending(m => m.Evidence.Sum(w => w.Length)).First();
            return new OcrBrandOutcome(winner.Brand, winner.Evidence, []);
        }

        // Son çare (yaklaşık): renkli/dekoratif logo harflerinin bir kısmı okunamadığında
        // token kesik çıkar (gerçek vaka: "FLAMI", güven 56). Yeterince uzun bir token,
        // yeterince uzun bir ayırt edici kelimenin ÖNEKİ ise veya 1 harf hatayla eşleşiyorsa
        // aday olur. Yanlış markaya çarpan uygulamamak için: tüm adaylar TEK bir NetCarpan'da
        // birleşmiyorsa sonuç yok sayılır (akış kullanıcıya sormaya düşer, risk alınmaz).
        var approx = new List<(BrandMultiplier Brand, string JoinedName, string Evidence)>();
        foreach (var brand in brands)
        {
            foreach (var word in NormalizeToWords(brand.FullName).Where(w => IsDistinctive(w) && w.Length >= 6))
            {
                foreach (var (token, conf) in tokenWords)
                {
                    if (conf < MinWordConfidence || token.Length < 5) continue;

                    if (token.Length < word.Length && word.StartsWith(token, StringComparison.Ordinal) &&
                        token.Length >= word.Length - 3)
                        approx.Add((brand, NormalizeJoined(brand.FullName), $"{token}→{word}"));
                    else if (Math.Abs(token.Length - word.Length) <= 1 && Levenshtein(token, word) == 1)
                        approx.Add((brand, NormalizeJoined(brand.FullName), $"{token}≈{word}"));
                }
            }
        }

        if (approx.Count == 0) return new OcrBrandOutcome(null, [], []);

        var approxDistinct = approx.GroupBy(a => a.JoinedName).Select(g => g.First()).ToList();
        if (approxDistinct.Select(a => a.Brand.NetCarpan).Distinct().Count() > 1)
            return new OcrBrandOutcome(null, [], []);

        return new OcrBrandOutcome(approxDistinct[0].Brand, [approxDistinct[0].Evidence], [], Approximate: true);
    }

    /// <summary>Marka hiç tespit edilemediğinde (kesin de yaklaşık da eşleşme yok) İLK soruya
    /// eklenecek "tahmini" öneriler: OCR token'larının markaların kelimelerine ne kadar yakın
    /// olduğuna bakar (MatchFromOcrTokens'ın aksine burada kesinlik aranmaz, sadece en yakın
    /// adaylar listelenir). Hiçbir token hiçbir markaya makul yakınlıkta değilse boş döner —
    /// anlamsız/gürültü öneri göstermemek tercih edilir.
    ///
    /// Gerçek vaka (2026-08-08): "DECO SPORT" markasının tek ayırt edici kelimesi "DECO" (4
    /// harf) OCR'da hiç okunamayınca eski kod (SADECE ayırt edici kelimelere bakan) bu marka
    /// için hiçbir öneri üretmiyordu — oysa görselde "SPORT" net okunmuştu, sadece jenerik
    /// olduğu için (bkz. GenericWords) kanıt sayılmıyordu; sonuçta ilgisiz markalar (kanıtları
    /// tesadüfi 0.4-oranlı eşleşmelerdi) önerildi, doğru marka listede hiç yer almadı. Artık
    /// jenerik kelimeler de kanıt sayılıyor ama SIKI eşikle (yaklaşık birebir okunmuş olmalı —
    /// rastgele bir OCR çöpünün "SPORT" gibi gerçek bir kelimeye kelimenin tamamına yakın
    /// uzunlukta rastlaması, gevşek ayırt-edici eşiğine kıyasla çok daha düşük ihtimaldir).
    /// Sıralama birincil olarak EŞLEŞEN KELİME SAYISI: "DECO" hiç okunamasa bile "SPORT"un tek
    /// başına neredeyse birebir eşleşmesi markayı listeye sokar; iki kelimesi de (DECO+SPORT)
    /// yakın çıkan bir marka, tek jenerik kelimesi yakın çıkan rakiplerinden önce sıralanır.</summary>
    public static List<string> SuggestBrandsFromOcrTokens(
        IReadOnlyDictionary<string, float> rawTokens,
        IReadOnlyList<BrandMultiplier> brands,
        int maxSuggestions = 5)
    {
        // Rakam→harf düzeltilmiş varyant da eklenir ("DEC0" → "DECO"), MatchFromOcrTokens'taki
        // AddWord ile aynı gerekçeyle: dekoratif fontlarda Tesseract/Paddle harfleri rakam sanabiliyor.
        var tokens = rawTokens
            .SelectMany(kv => NormalizeToWords(kv.Key).Select(w => (Word: w, Conf: kv.Value)))
            .Where(t => t.Word.Length >= 3 && t.Conf >= MinWordConfidence && t.Word.Any(char.IsAsciiLetter))
            .SelectMany(t => t.Word.Any(char.IsAsciiDigit) ? [t.Word, LetterizeDigits(t.Word)] : (string[])[t.Word])
            .Distinct()
            .ToList();
        if (tokens.Count == 0 || brands.Count == 0) return [];

        const double DistinctiveThreshold = 0.4;
        const double GenericThreshold = 0.15;

        var scored = new List<(string FullName, int MatchedWords, double AvgRatio)>();
        foreach (var group in brands.GroupBy(b => NormalizeJoined(b.FullName)))
        {
            var brand = group.First();
            var words = NormalizeToWords(brand.FullName).Where(w => w.Length >= 2).ToList();
            if (words.Count == 0) continue;

            // Marka bu turda zaten eşleşmiş olamaz (buraya sadece eşleşme bulunamadığında
            // girilir). Her kelime için en yakın token'a olan mesafe oranı ayrı ayrı eşiklenir;
            // eşiği geçen kelime SAYISI (çoklu kanıt = daha güvenilir) birincil sıralama, ortalama
            // oran ikincil sıralama ölçütüdür.
            var matched = new List<double>();
            foreach (var w in words)
            {
                var best = tokens.Min(t => (double)Levenshtein(w, t) / Math.Max(w.Length, t.Length));
                if (best <= (IsDistinctive(w) ? DistinctiveThreshold : GenericThreshold))
                    matched.Add(best);
            }
            if (matched.Count > 0)
                scored.Add((brand.FullName, matched.Count, matched.Average()));
        }

        return scored
            .OrderByDescending(x => x.MatchedWords)
            .ThenBy(x => x.AvgRatio)
            .Take(maxSuggestions)
            .Select(x => x.FullName)
            .ToList();
    }

    /// <summary>Rakamları görsel olarak benzedikleri harflere çevirir ("FLAM1NDO" → "FLAMINDO").
    /// FullScanOcr'daki Confusions tablosunun tersi — orada harfler rakama, burada rakamlar harfe.</summary>
    private static string LetterizeDigits(string word)
    {
        var chars = word.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch
            {
                '0' => 'O', '1' => 'I', '2' => 'Z', '5' => 'S', '6' => 'G', '8' => 'B',
                _ => chars[i],
            };
        return new string(chars);
    }

    /// <summary>WhatsApp'tan gelen serbest metin marka cevabını listeyle eşleştirir.
    /// Sıra: birebir (boşluk/noktalama/Türkçe-karakter bağımsız) → kelime alt kümesi
    /// (örn. "FLAMİNDO" → "BABY FLAMİNDO") → küçük yazım hatası (Levenshtein). Hiçbiri
    /// tutmazsa en yakın adlar öneri olarak döner (tekrar sorulacak mesaj için).</summary>
    public static UserBrandOutcome MatchFromUserText(string userText, IReadOnlyList<BrandMultiplier> brands)
    {
        var userWords = NormalizeToWords(userText);
        var userJoined = string.Concat(userWords);
        if (userJoined.Length == 0 || brands.Count == 0)
            return new UserBrandOutcome(null, false, []);

        var byName = brands
            .Select(b => (Brand: b, Words: NormalizeToWords(b.FullName).Where(w => w.Length >= 2).ToList()))
            .Where(x => x.Words.Count > 0)
            .Select(x => (x.Brand, x.Words, Joined: string.Concat(x.Words)))
            .GroupBy(x => x.Joined)
            .Select(g => g.First())
            .ToList();

        // 1) Birebir eşitlik (normalize edilmiş, boşluksuz karşılaştırma).
        var exact = byName.Where(x => x.Joined == userJoined).ToList();
        if (exact.Count == 1)
            return new UserBrandOutcome(exact[0].Brand, false, []);

        // 2) Kullanıcı adın ayırt edici bir parçasını yazdıysa (tam adı yazmak yerine):
        //    yazılan tüm kelimeler tek bir markanın adında geçiyorsa ve en az biri ayırt
        //    ediciyse kabul et. "BABY" gibi jenerik tek kelime burada asla yeterli olmaz.
        if (userWords.Any(IsDistinctive))
        {
            var subset = byName
                .Where(x => userWords.All(x.Words.Contains) ||
                            (x.Words.All(userWords.Contains) && x.Words.Any(IsDistinctive)))
                .ToList();
            if (subset.Count == 1)
                return new UserBrandOutcome(subset[0].Brand, false, []);
            if (subset.Count > 1 && subset.Select(x => x.Brand.NetCarpan).Distinct().Count() == 1)
                return new UserBrandOutcome(subset[0].Brand, false, []);
            if (subset.Count > 1)
                return new UserBrandOutcome(null, false, subset.Select(x => x.Brand.FullName).Take(5).ToList());
        }

        // 3) Küçük yazım hatası: boşluksuz formlar arasında Levenshtein. Kısa adlarda 1,
        //    uzunlarda 2 harflik farka izin verilir; en yakın mesafede TEK marka varsa kabul.
        var scored = byName
            .Select(x => (x.Brand, x.Joined, Distance: Levenshtein(userJoined, x.Joined)))
            .OrderBy(x => x.Distance)
            .ToList();

        var bestDistance = scored[0].Distance;
        int allowed = Math.Min(userJoined.Length, scored[0].Joined.Length) >= 6 ? 2 : 1;
        if (bestDistance <= allowed)
        {
            var atBest = scored.TakeWhile(x => x.Distance == bestDistance).ToList();
            if (atBest.Count == 1 || atBest.Select(x => x.Brand.NetCarpan).Distinct().Count() == 1)
                return new UserBrandOutcome(atBest[0].Brand, Approximate: true, []);
        }

        // 4) Eşleşme yok: tekrar sorulacak mesaj için en yakın adları öner.
        var suggestions = scored.Take(5).Select(x => x.Brand.FullName).ToList();
        return new UserBrandOutcome(null, false, suggestions);
    }

    private static int Levenshtein(string a, string b)
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
}
