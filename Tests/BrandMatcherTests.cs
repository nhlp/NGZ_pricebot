using PriceBotPipeline;
using Xunit;

/// <summary>Marka adları ve NetCarpan değerleri, canlı AS_PWB_MarkaCarpan view'inden alınmış
/// GERÇEK satırlardır (2026-07-27 itibarıyla) — testler gerçek veri desenlerini
/// (Türkçe karakter, mükerrer satır, jenerik kelimeler, & işareti) birebir yansıtır.</summary>
public class BrandMatcherTests
{
    private static readonly List<BrandMultiplier> Brands =
    [
        new("LILA",  "LİLAX",         1149.5375m),
        new("PEPE",  "PEPE",          124.4865m),
        new("PEPEL", "PEPELİNO",      111.0285m),
        new("FLAM",  "BABY FLAMİNDO", 117.7575m),
        new("BEBLY", "BEBLY KİDS",    114.72945m),
        new("CLUP",  "MİNİ CLUP",     1149.5375m),
        new("HIPP",  "HİPPIL",        125.608m),
        new("HIPP",  "HİPPIL",        125.608m),      // view'de gerçekten mükerrer
        new("MOTHE", "MOTHER&ÇOJOK",  1149.5375m),
        new("TIX",   "MINITIX",       125.608m),
        new("MTIX",  "MİNİTİX",       125.608m),      // normalize edilince MINITIX ile aynı ada düşer
        new("CODE",  "CODE TEKSTİL",  126.7295m),
        new("COOL",  "COOL BABY",     123.365m),      // ayırt edici kelimesi kısa (4 harf) marka
        new("MINICE","MİNİCE BEBE",   124.4865m),     // önek-kurtarma belirsizlik çifti (MINIC...)
        new("MCIX",  "MİNİCİX",       117.19675m),
    ];

    private static Dictionary<string, float> Tokens(params (string Word, float Conf)[] tokens) =>
        tokens.ToDictionary(t => t.Word, t => t.Conf);

    // ---- Normalizasyon ----

    [Theory]
    [InlineData("LİLAX", "LILAX")]
    [InlineData("lilax", "LILAX")]
    [InlineData("ŞEKER BEBE", "SEKERBEBE")]
    [InlineData("MOTHER&ÇOJOK", "MOTHERCOJOK")]
    [InlineData("BY ÇELİKS", "BYCELIKS")]
    [InlineData("AL-GİY", "ALGIY")]
    public void NormalizeJoined_TurkceKarakterVeNoktalama(string input, string expected) =>
        Assert.Equal(expected, BrandMatcher.NormalizeJoined(input));

    [Fact]
    public void NormalizeToWords_NoktalamaKelimeAyracidir()
    {
        Assert.Equal(new[] { "MOTHER", "COJOK" }, BrandMatcher.NormalizeToWords("MOTHER&ÇOJOK"));
        Assert.Equal(new[] { "CODE", "317613" }, BrandMatcher.NormalizeToWords("code:317613"));
    }

    // ---- OCR eşleştirme ----

    [Fact]
    public void Ocr_TekKelimeliMarka_Eslesir()
    {
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("LILAX", 85f)), Brands);
        Assert.NotNull(outcome.Brand);
        Assert.Equal(1149.5375m, outcome.Brand!.NetCarpan);
    }

    [Fact]
    public void Ocr_NoktalamaliToken_Eslesir()
    {
        // OCR gerçekte "LILAX®" gibi token'lar üretebilir.
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("LILAX®", 85f)), Brands);
        Assert.Equal("LİLAX", outcome.Brand?.FullName);
    }

    [Fact]
    public void Ocr_CokKelimeliMarka_TumKelimelerVarsa_Eslesir()
    {
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("BABY", 80f), ("FLAMINDO", 75f)), Brands);
        Assert.Equal("BABY FLAMİNDO", outcome.Brand?.FullName);
    }

    [Fact]
    public void Ocr_BitisikYazim_Eslesir()
    {
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("BABYFLAMINDO", 70f)), Brands);
        Assert.Equal("BABY FLAMİNDO", outcome.Brand?.FullName);
    }

    [Fact]
    public void Ocr_SadeceJenerikKelime_KanitSayilmaz()
    {
        // "BABY" tek başına hangi marka olduğunu kanıtlayamaz.
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("BABY", 90f)), Brands);
        Assert.Null(outcome.Brand);
        Assert.Empty(outcome.AmbiguousNames);
    }

    [Fact]
    public void Ocr_TamamiJenerikOlanMarka_AslaOcrIleEslesmez()
    {
        // "MİNİ CLUP"un iki kelimesi de jenerik: OCR ikisini de okusa bile eşleşme olmamalı,
        // akış kullanıcıya sormaya düşmeli.
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("MINI", 90f), ("CLUP", 90f)), Brands);
        Assert.Null(outcome.Brand);
    }

    [Fact]
    public void Ocr_DusukGuvenliToken_KanitSayilmaz()
    {
        // Kumaş/dantel dokusundan çıkan çöp token'lar düşük güvenle gelir.
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("LILAX", 20f)), Brands);
        Assert.Null(outcome.Brand);
    }

    [Fact]
    public void Ocr_AltDizeDegilKelimeEsitligi_PepelinoPepeyeEslesmez()
    {
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("PEPELINO", 90f)), Brands);
        Assert.Equal("PEPELİNO", outcome.Brand?.FullName);
    }

    [Fact]
    public void Ocr_FarkliCarpanliBirdenFazlaMarka_BelirsizDoner()
    {
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("LILAX", 90f), ("PEPE", 90f)), Brands);
        Assert.Null(outcome.Brand);
        Assert.Contains("LİLAX", outcome.AmbiguousNames);
        Assert.Contains("PEPE", outcome.AmbiguousNames);
    }

    [Fact]
    public void Ocr_MukerrerSatirlar_BelirsizlikYaratmaz()
    {
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("HIPPIL", 90f)), Brands);
        Assert.Equal("HİPPIL", outcome.Brand?.FullName);
    }

    [Fact]
    public void Ocr_KidswearTagline_BaskaMarkayiYanlisEslestirmez()
    {
        // Gerçek vaka (2026-08-11): DECO SPORT + MESSINHO ürünlerinin karışık geldiği bir
        // klasörde, ikisinin de logosundaki jenerik "... Kids Wear" tagline'ı OCR'dan boşluksuz
        // "KIDSWEAR" tek token olarak okununca, listede TAMAMEN ilgisiz bir "ARD KİDSWEAR"
        // markası (tek ayırt edici kelimesi KIDSWEAR) farklı NetCarpan'la üçüncü çelişkili aday
        // olarak devreye giriyordu. "DECO SPORT" gerçek kanıtla (DECO+SPORT aynı satırda) tek
        // başına net eşleşmeli, ARD KİDSWEAR'ın bu tesadüfi tagline'dan bulaşmaması gerekir.
        var brands = new List<BrandMultiplier>
        {
            new("DECO", "DECO SPORT", 1.126151m),
            new("ARD", "ARD KİDSWEAR", 1.211154m),
        };
        var outcome = BrandMatcher.MatchFromOcrTokens(
            Tokens(("DECO SPORT", 95f), ("KIDSWEAR", 99f)), brands);
        Assert.Equal("DECO SPORT", outcome.Brand?.FullName);
        Assert.Empty(outcome.AmbiguousNames);
    }

    [Fact]
    public void Ocr_AyniAdaNormalizeOlanAyniCarpanlilar_BelirsizlikYaratmaz()
    {
        // MINITIX ve MİNİTİX normalize edilince aynı ad; çarpanları da aynı.
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("MINITIX", 90f)), Brands);
        Assert.NotNull(outcome.Brand);
        Assert.Equal(125.608m, outcome.Brand!.NetCarpan);
    }

    [Fact]
    public void Ocr_TurkceKarakterliMarka_AsciiTokenlaEslesir()
    {
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("MOTHER", 80f), ("COJOK", 75f)), Brands);
        Assert.Equal("MOTHER&ÇOJOK", outcome.Brand?.FullName);
    }

    // ---- OCR eşleştirme: Baby Flamindo vakasıyla gelen gevşetme/kurtarma kuralları ----

    [Fact]
    public void Ocr_AyirtEdiciKelimeYeterli_JenerikKelimeSartDegil()
    {
        // Gerçek vaka: "BABY" hep okunuyor ama jenerik; "FLAMINDO" tek başına yetmeli.
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("FLAMINDO", 70f)), Brands);
        Assert.Equal("BABY FLAMİNDO", outcome.Brand?.FullName);
        Assert.False(outcome.Approximate);
    }

    [Fact]
    public void Ocr_KisaAyirtEdiciKelime_TekBasinaYetmez_TumAdGerekir()
    {
        // "COOL" 4 harf: tişört baskısından da çıkabilir, tek başına marka kanıtı olamaz...
        var alone = BrandMatcher.MatchFromOcrTokens(Tokens(("COOL", 90f)), Brands);
        Assert.Null(alone.Brand);

        // ...ama adın tamamı ("COOL" + "BABY") görüldüyse eşleşir.
        var full = BrandMatcher.MatchFromOcrTokens(Tokens(("COOL", 90f), ("BABY", 90f)), Brands);
        Assert.Equal("COOL BABY", full.Brand?.FullName);
    }

    [Fact]
    public void Ocr_RakamHarfKarisikligi_Duzeltilir()
    {
        // Dekoratif fontta I → 1 okunabilir.
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("FLAM1NDO", 70f)), Brands);
        Assert.Equal("BABY FLAMİNDO", outcome.Brand?.FullName);
    }

    [Fact]
    public void Ocr_KesikOkuma_OnekKurtarmasiylaYaklasikEslesir()
    {
        // Gerçek vaka: renkli logo harfleri kaybolunca Tesseract "FLAMI" okudu (güven 56).
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("FLAMI", 56f)), Brands);
        Assert.Equal("BABY FLAMİNDO", outcome.Brand?.FullName);
        Assert.True(outcome.Approximate);
    }

    [Fact]
    public void Ocr_BirHarfHataliOkuma_YaklasikEslesir()
    {
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("FLAMINDU", 60f)), Brands);
        Assert.Equal("BABY FLAMİNDO", outcome.Brand?.FullName);
        Assert.True(outcome.Approximate);
    }

    [Fact]
    public void Ocr_OnekKurtarma_BirdenFazlaFarkliCarpanliMarkayiIsaretEdiyorsa_Reddedilir()
    {
        // "MINIC" hem MİNİCE'nin hem MİNİCİX'in öneki, çarpanları farklı → risk alınmaz.
        var outcome = BrandMatcher.MatchFromOcrTokens(Tokens(("MINIC", 80f)), Brands);
        Assert.Null(outcome.Brand);
    }

    [Fact]
    public void Ocr_OnekKurtarma_DusukGuvenliVeyaKisaTokenIleCalismaz()
    {
        Assert.Null(BrandMatcher.MatchFromOcrTokens(Tokens(("FLAMI", 20f)), Brands).Brand);
        Assert.Null(BrandMatcher.MatchFromOcrTokens(Tokens(("FLAM", 90f)), Brands).Brand);
    }

    // ---- Marka önerisi (tahmin, SuggestBrandsFromOcrTokens) ----

    [Fact]
    public void Oneri_JenerikKelimeninBirebirOkunmasi_MarkayiOnerir()
    {
        // Gerçek vaka (2026-08-08): "DECO SPORT"un tek ayırt edici kelimesi "DECO" hiç
        // okunamadı ama jenerik kelimesi "SPORT" görselde net (birebir) okunmuştu. Eski kod
        // sadece ayırt edici kelimelere baktığı için bu markayı hiç önermiyordu.
        var brands = new List<BrandMultiplier> { new("DS", "DECO SPORT", 100m) };
        var suggestions = BrandMatcher.SuggestBrandsFromOcrTokens(Tokens(("SPORT", 90f)), brands);
        Assert.Contains("DECO SPORT", suggestions);
    }

    [Fact]
    public void Oneri_JenerikKelimeYaklasikOkunmus_Onerilmez()
    {
        // Jenerik kelimeler sadece neredeyse birebir okunduğunda kanıt sayılır — gevşek (ayırt
        // edici kelimelerdeki gibi 0.4 oranlı) eşleşme yeterli değildir, aksi halde onlarca
        // markada geçen bu kelimeler rastgele markaları öne çıkarır.
        var brands = new List<BrandMultiplier> { new("DS", "DECO SPORT", 100m) };
        var suggestions = BrandMatcher.SuggestBrandsFromOcrTokens(Tokens(("SPORY", 90f)), brands);
        Assert.DoesNotContain("DECO SPORT", suggestions);
    }

    [Fact]
    public void Oneri_CokluKelimeKaniti_TekKelimeKanitindanOnceSiralanir()
    {
        var brands = new List<BrandMultiplier>
        {
            new("DS", "DECO SPORT", 100m),   // DECO (ayırt edici) + SPORT (jenerik) — ikisi de eşleşir
            new("SL", "SPORT LINE", 50m),    // sadece SPORT (jenerik) eşleşir, LINE hiç okunmadı
        };
        var suggestions = BrandMatcher.SuggestBrandsFromOcrTokens(
            Tokens(("DECO", 80f), ("SPORT", 90f)), brands);

        Assert.Contains("DECO SPORT", suggestions);
        Assert.True(suggestions.IndexOf("DECO SPORT") < suggestions.IndexOf("SPORT LINE"),
            "iki kelimesi de eşleşen marka, tek kelimesi eşleşen markadan önce sıralanmalı");
    }

    // ---- Kullanıcı cevabı eşleştirme ----

    [Theory]
    [InlineData("LİLAX")]
    [InlineData("lilax")]
    [InlineData(" Lilax ")]
    public void Kullanici_BirebirAd_Eslesir(string answer)
    {
        var outcome = BrandMatcher.MatchFromUserText(answer, Brands);
        Assert.Equal("LİLAX", outcome.Brand?.FullName);
        Assert.False(outcome.Approximate);
    }

    [Fact]
    public void Kullanici_JenerikKelimeliTamAd_Eslesir()
    {
        // Kullanıcı tam adı yazdıysa jenerik kelime şartı aranmaz.
        var outcome = BrandMatcher.MatchFromUserText("mini clup", Brands);
        Assert.Equal("MİNİ CLUP", outcome.Brand?.FullName);
    }

    [Fact]
    public void Kullanici_AyirtEdiciParcaYeterli()
    {
        var outcome = BrandMatcher.MatchFromUserText("FLAMİNDO", Brands);
        Assert.Equal("BABY FLAMİNDO", outcome.Brand?.FullName);
    }

    [Fact]
    public void Kullanici_JenerikTekKelime_EslesmezOneriDoner()
    {
        var outcome = BrandMatcher.MatchFromUserText("BABY", Brands);
        Assert.Null(outcome.Brand);
        Assert.NotEmpty(outcome.Suggestions);
    }

    [Fact]
    public void Kullanici_KucukYazimHatasi_YaklasikEslesir()
    {
        var outcome = BrandMatcher.MatchFromUserText("LİLAK", Brands);
        Assert.Equal("LİLAX", outcome.Brand?.FullName);
        Assert.True(outcome.Approximate);
    }

    [Fact]
    public void Kullanici_UzunAddaIkiHarfHata_YaklasikEslesir()
    {
        var outcome = BrandMatcher.MatchFromUserText("BEBLY KIDZ", Brands);
        Assert.Equal("BEBLY KİDS", outcome.Brand?.FullName);
        Assert.True(outcome.Approximate);
    }

    [Fact]
    public void Kullanici_AlakasizMetin_OneriListesiDoner()
    {
        var outcome = BrandMatcher.MatchFromUserText("XQWZY", Brands);
        Assert.Null(outcome.Brand);
        Assert.True(outcome.Suggestions.Count is > 0 and <= 5);
    }

    [Fact]
    public void Kullanici_BosMetin_BosDoner()
    {
        var outcome = BrandMatcher.MatchFromUserText("   ", Brands);
        Assert.Null(outcome.Brand);
        Assert.Empty(outcome.Suggestions);
    }
}
