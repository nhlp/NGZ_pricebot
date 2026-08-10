using System.Globalization;
using ClosedXML.Excel;
using Xunit;

/// <summary>SizeOrAgeRangeToken hem Excel sütunu eleme (LooksLikeSizeOrAgeColumn) hem de
/// FullScanOcr'ın OCR aday çıkarımında (ExtractCandidates) kullandığı paylaşılan filtredir; burada
/// doğrudan test edilir çünkü FullScanOcr.cs, Tesseract/SkiaSharp bağımlılığı yüzünden Tests
/// projesine dahil değil (bkz. CLAUDE.md).</summary>
public class SizeOrAgeRangeTokenTests
{
    [Theory]
    [InlineData("134-140-146-152")]
    [InlineData("2-3-4-5")]
    [InlineData("0-12-18-24")]
    [InlineData("98-104")]
    public void IsMatch_TireIleAyrilmisRakamListesi_DogruTespitEdilir(string token) =>
        Assert.True(SizeOrAgeRangeToken.IsMatch(token));

    [Theory]
    [InlineData("6250")] // tek başına bir ürün kodu, tire yok
    [InlineData("code:317613")] // önekli kod, tire yok
    [InlineData("134-S")] // rakam olmayan segment içeriyor
    [InlineData("")]
    public void IsMatch_GercekKodAdaylari_YanlisPozitifVermez(string token) =>
        Assert.False(SizeOrAgeRangeToken.IsMatch(token));
}

public class ExcelPriceReaderTests
{
    /// <summary>Kullanıcının gerçek Excel'indeki tam satır seti: bazı fiyatlar virgüllü
    /// ondalık ("204,25"), bazıları düz tam sayı ("228"). Üretimde "13 satırdan sadece 7'si
    /// yüklendi" raporlandı — bu test o gerçek veriyle aynı senaryoyu üretir.</summary>
    private static readonly (string Code, string Price)[] RealSampleRows =
    [
        ("1374", "228"),
        ("1375", "228"),
        ("1376", "218,50"),
        ("1378", "252,70"),
        ("1379", "247"),
        ("1382", "222,30"),
        ("1383", "235,60"),
        ("1345", "204,25"),
        ("1349", "205,2"),
        ("1350", "171"),
        ("1354", "185,25"),
        ("1380", "190"),
        ("1381", "190"),
        ("1384", "190"),
    ];

    private static string WriteWorkbook((string Code, string Price)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "Kod";
        ws.Cell(1, 2).Value = "Fiyat";
        for (int i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Code;
            // Excel'de fiyat hücreleri metin değil, sayısal olarak girildiği için
            // GetString() çağrısının davranışını gerçekçi yansıtmak adına burada da
            // sayısal değer olarak yazıyoruz (ClosedXML hücre formatına göre biçimlendirir).
            ws.Cell(i + 2, 2).Value = decimal.Parse(rows[i].Price.Replace(',', '.'), CultureInfo.InvariantCulture);
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void LoadPricesFromExcel_TumSatirlarYuklenir()
    {
        var path = WriteWorkbook(RealSampleRows);
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);

            Assert.Equal(RealSampleRows.Length, prices.Count);
            foreach (var (code, priceStr) in RealSampleRows)
            {
                var expected = decimal.Parse(priceStr.Replace(',', '.'), CultureInfo.InvariantCulture);
                Assert.True(prices.ContainsKey(code), $"Kod {code} sözlükte yok — satır sessizce düşürüldü.");
                Assert.Equal(expected, prices[code]);
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>Ondalık ayırıcının hücrede METİN olarak virgülle geldiği (ör. kullanıcı
    /// Excel'e "204,25" yazıp hücre biçimi Genel/Metin olduğunda) durumu da kapsar —
    /// bu durumda ClosedXML'in GetString()'i doğrudan "204,25" döner.</summary>
    [Fact]
    public void LoadPricesFromExcel_MetinHucreVirgulluFiyat_Yuklenir()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "Kod";
        ws.Cell(1, 2).Value = "Fiyat";
        ws.Cell(2, 1).Value = "1345";
        var priceCell = ws.Cell(2, 2);
        priceCell.Style.NumberFormat.Format = "@"; // hücreyi metin olarak biçimlendir
        priceCell.Value = "204,25";

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            Assert.True(prices.ContainsKey("1345"), "Virgüllü metin fiyat hücresi sessizce düşürüldü.");
            Assert.Equal(204.25m, prices["1345"]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>GERÇEK KÖK NEDEN (üretimdeki dosyada bulundu): fiyat sütununun bazı hücreleri
    /// insan tarafından "228 TL" / "218,50 TL" gibi METİN olarak, para birimi son ekiyle
    /// girilmiş; bazıları ise düz sayı ("204,25"). decimal.TryParse("228 TL", ...) başarısız
    /// olur ve mevcut kod bunu sessizce atlar — "13 satırdan sadece 7'si yüklendi" budur.</summary>
    [Fact]
    public void LoadPricesFromExcel_TLSonEkliMetinFiyatlar_Yuklenir()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "KOD";
        ws.Cell(1, 2).Value = "FİYAT";

        var rows = new (string Code, string RawPrice, decimal Expected)[]
        {
            ("1374", "228 TL", 228m),
            ("1376", "218,50 TL", 218.50m),
            ("1345", "204,25", 204.25m), // saf sayı hücre, karşılaştırma için
        };

        for (int i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Code;
            var cell = ws.Cell(i + 2, 2);
            if (rows[i].RawPrice.Contains("TL"))
            {
                cell.Style.NumberFormat.Format = "@";
                cell.Value = rows[i].RawPrice;
            }
            else
            {
                cell.Value = decimal.Parse(rows[i].RawPrice.Replace(',', '.'), CultureInfo.InvariantCulture);
            }
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            foreach (var (code, rawPrice, expected) in rows)
            {
                Assert.True(prices.ContainsKey(code), $"Kod {code} (ham fiyat '{rawPrice}') sözlükte yok — TL son ekli satır sessizce düşürüldü.");
                Assert.Equal(expected, prices[code]);
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>GERÇEK VAKA (Eray Kids kışlık fiyat listesi, 2026-08-03): başlık satırında hem
    /// "KOD-B" (A sütunu, OCR'ın görselden okuduğu 4 haneli stil kodu) hem de "BARKOD" (C sütunu,
    /// 13 haneli EAN) var — ikisi de "kod" alt dizesini içeriyor. Eski kod, satırdaki hücreleri
    /// soldan sağa tararken kod sütunu eşleşmesini SON bulduğu hücreyle eziyordu, yani "BARKOD"
    /// (KOD-B'den sonra geldiği için) kazanıyordu. Sonuç: fiyat sözlüğü 13 haneli barkodlarla
    /// anahtarlanıyor, ama OCR `\d{3,7}` regex'iyle asla 7 haneden uzun bir aday çıkarmıyor —
    /// bu yüzden 33 görselin TAMAMI "Excel'deki kodlardan biriyle eşleşen bulunamadı" diyerek
    /// atlanıyordu (kur/marka doğruydu, sorun sessizce yanlış sütun seçimindeydi).</summary>
    [Fact]
    public void LoadPricesFromExcel_KodVeBarkodSutunuBirlikte_KodSutunuTercihEdilir()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "KOD-B";
        ws.Cell(1, 2).Value = "MODEL";
        ws.Cell(1, 3).Value = "BARKOD";
        ws.Cell(1, 4).Value = "YAŞ";
        ws.Cell(1, 5).Value = "FİYAT TL";

        ws.Cell(2, 1).Value = "6250";
        ws.Cell(2, 2).Value = "DESENLI PELUŞ KÜRK";
        ws.Cell(2, 3).Value = "2023965262502";
        ws.Cell(2, 4).Value = "134-140-146-152";
        ws.Cell(2, 5).Value = 490.00m;

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            Assert.True(prices.ContainsKey("6250"), "KOD-B sütunu yerine BARKOD sütunu okunmuş — OCR'ın bulduğu kısa kod hiçbir zaman eşleşemez.");
            Assert.False(prices.ContainsKey("2023965262502"), "Fiyat, 13 haneli barkod anahtarıyla yüklenmiş.");
            Assert.Equal(490.00m, prices["6250"]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>GERÇEK VAKA (2026-08-03): başlık satırında hem "Fiyatı" (Sıra No, Stok Kodu, Malın
    /// Cinsi'nden sonra 4. sütun) hem de "Tutarı" (Fiyatı × Miktar formülü, Miktar boş bırakıldığı
    /// için hep 0) var — ikisi de "fiyat"/"tutar" alt dizesini içeriyor. Eski kod, hücreleri soldan
    /// sağa tararken fiyat sütunu eşleşmesini SON bulduğu hücreyle eziyordu, yani "Tutarı" (sağda
    /// olduğu için) kazanıyordu. Sonuç: her satırın fiyatı 0 okunuyor, kur ve marka çarpanı doğru
    /// olsa bile basılan USD fiyatı hep $0.00 çıkıyordu.</summary>
    [Fact]
    public void LoadPricesFromExcel_FiyatVeTutarSutunuBirlikte_FiyatSutunuTercihEdilir()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "Stok Kodu";
        ws.Cell(1, 2).Value = "Malın Cinsi";
        ws.Cell(1, 3).Value = "Fiyatı";
        ws.Cell(1, 4).Value = "Miktar";
        ws.Cell(1, 5).Value = "Tutarı";

        ws.Cell(2, 1).Value = "4317";
        ws.Cell(2, 2).Value = "2-5 yaş vual brode garnili";
        ws.Cell(2, 3).Value = 295.00m;
        ws.Cell(2, 4).Value = 0;
        ws.Cell(2, 5).Value = 0;

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            Assert.True(prices.ContainsKey("4317"));
            Assert.Equal(295.00m, prices["4317"]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Genel güvenlik ağı: öncelik puanı tek başına yeterli değildir — en yüksek öncelikli
    /// aday (burada "Fiyatı") tamamen boş/sıfırsa (ör. fiyat sütunu doldurulmadan gönderilmiş bir
    /// taslak liste), gerçek veri içeren düşük öncelikli "Tutarı" sütununa geri düşülmeli, sessizce
    /// $0.00 üretilmemeli.</summary>
    [Fact]
    public void LoadPricesFromExcel_FiyatSutunuTamamenSifir_TutaraGeriDuser()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "Stok Kodu";
        ws.Cell(1, 2).Value = "Fiyatı";
        ws.Cell(1, 3).Value = "Tutarı";

        ws.Cell(2, 1).Value = "4317";
        ws.Cell(2, 2).Value = 0;
        ws.Cell(2, 3).Value = 295.00m;
        ws.Cell(3, 1).Value = "4318";
        ws.Cell(3, 2).Value = 0;
        ws.Cell(3, 3).Value = 199.00m;

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            Assert.Equal(295.00m, prices["4317"]);
            Assert.Equal(199.00m, prices["4318"]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Worker bir Windows Service olarak farklı bir hesap altında (ör. LocalSystem)
    /// çalışabilir; o hesabın bölgesel ayarları geliştirme makinesindeki interaktif kullanıcı
    /// hesabınınkinden FARKLI olabilir (ör. en-US). decimal.TryParse ortam kültürüne bağlıysa,
    /// bu test onu en-US'a sabitleyerek gerçek koşulu simüle eder ve virgüllü fiyatların
    /// sessizce mi düşürüldüğünü (ya da 100x yanlış mı ayrıştırıldığını) ortaya çıkarır.</summary>
    [Fact]
    public void LoadPricesFromExcel_EnUsOrtamKulturunde_YineDogruYuklenir()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var path = WriteWorkbook(RealSampleRows);
            try
            {
                var prices = ExcelPriceReader.LoadPricesFromExcel(path);

                Assert.Equal(RealSampleRows.Length, prices.Count);
                foreach (var (code, priceStr) in RealSampleRows)
                {
                    var expected = decimal.Parse(priceStr.Replace(',', '.'), CultureInfo.InvariantCulture);
                    Assert.True(prices.ContainsKey(code), $"Kod {code} en-US ortamında sözlükte yok — kültüre bağlı satır kaybı.");
                    Assert.Equal(expected, prices[code]);
                }
            }
            finally { File.Delete(path); }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    /// <summary>LoadCandidateCodeColumns'ın KOD-B + BARKOD ikilemini nasıl çözdüğünü doğrular:
    /// "barkod" içeren başlıklar HİÇBİR ZAMAN aday olarak dönmez — öncelik puanıyla geride
    /// bırakılmak yerine kaynakta tamamen dışlanır. EAN yapısal olarak (13 hane) OCR'ın \d{3,7}
    /// adaylarıyla asla tam string eşitliğiyle eşleşemeyeceği için OCR oylamasına bırakmanın bir
    /// anlamı yok; kullanıcı isteği üzerine netleştirildi (2026-08-03, ERAY KIDS vakasından sonra).</summary>
    [Fact]
    public void LoadCandidateCodeColumns_KodVeBarkodSutunuBirlikte_BarkodHicbirZamanAdayOlmaz()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "KOD-B";
        ws.Cell(1, 2).Value = "BARKOD";
        ws.Cell(1, 3).Value = "FİYAT TL";

        ws.Cell(2, 1).Value = "6250";
        ws.Cell(2, 2).Value = "2023965262502";
        ws.Cell(2, 3).Value = 490.00m;

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);

            var kodB = Assert.Single(candidates);
            Assert.Equal("KOD-B", kodB.HeaderName);
            Assert.True(kodB.Prices.ContainsKey("6250"));
            Assert.DoesNotContain(candidates, c => c.HeaderName == "BARKOD");
        }
        finally { File.Delete(path); }
    }

    /// <summary>Kullanıcı isteği (2026-08-03), barkod'la aynı mantık: "YAŞ"/"BEDEN" (ya da ASCII
    /// "YAS") geçen başlıklar da hiçbir zaman kod sütunu adayı olmamalı — sütun içeriği ne olursa
    /// olsun. Başlıklar bilerek "YAŞ KODU"/"BEDEN KODU" seçildi: "kod" alt-dizesini de içerdikleri
    /// için bu düzeltme olmasaydı priority 2 alıp gerçek "KOD" sütunuyla birlikte aday sayılırlardı.
    /// Değerleri de bilerek TEK sayı ("3", "92" — tire'li aralık değil) verildi, çünkü değer bazlı
    /// LooksLikeSizeOrAgeColumn kontrolü sadece "134-140" gibi tire'li aralıkları yakalar; bu vaka
    /// başlık bazlı dışlamanın o kontrolün boşluğunu kapattığını gösteriyor.</summary>
    [Fact]
    public void LoadCandidateCodeColumns_YasVeBedenBasliklariHicbirZamanAdayOlmaz()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "KOD";
        ws.Cell(1, 2).Value = "YAŞ KODU";
        ws.Cell(1, 3).Value = "BEDEN KODU";
        ws.Cell(1, 4).Value = "FİYAT";

        ws.Cell(2, 1).Value = "6250";
        ws.Cell(2, 2).Value = "3";
        ws.Cell(2, 3).Value = "92";
        ws.Cell(2, 4).Value = 490.00m;

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);

            var kod = Assert.Single(candidates);
            Assert.Equal("KOD", kod.HeaderName);
            Assert.DoesNotContain(candidates, c => c.HeaderName == "YAŞ KODU");
            Assert.DoesNotContain(candidates, c => c.HeaderName == "BEDEN KODU");
        }
        finally { File.Delete(path); }
    }

    /// <summary>GERÇEK SENARYO (2026-08-03, kullanıcıdan): bazı fiyat listelerinde gerçek ürün kodu
    /// "KOD"/"SKU"/"ID" gibi bir başlık altında DEĞİL, doğrudan "MODEL" başlıklı sütunda basılı —
    /// bu sütun eskiden hiç aday sayılmıyordu (hiçbir keyword eşleşmiyordu), yani o Excel için hiçbir
    /// zaman doğru sütun bulunamazdı. Artık "model" de orta öncelikli bir aday sinyali.</summary>
    [Fact]
    public void LoadCandidateCodeColumns_SadeceModelBasligiKodIcerdiginde_AdayOlarakBulunur()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "MODEL";
        ws.Cell(1, 2).Value = "AÇIKLAMA";
        ws.Cell(1, 3).Value = "FİYAT";

        ws.Cell(2, 1).Value = "6570";
        ws.Cell(2, 2).Value = "Desenli triko takım";
        ws.Cell(2, 3).Value = 350.00m;

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);

            var model = Assert.Single(candidates, c => c.HeaderName == "MODEL");
            Assert.True(model.Prices.ContainsKey("6570"));
            Assert.Equal(350.00m, model.Prices["6570"]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>GERÇEK SENARYO (2026-08-03, kullanıcıdan): görsellerin üzerinde basılı "2-3-4-5 yaş"
    /// gibi tire ile ayrılmış beden/yaş aralıkları, Excel'in YAŞ/BEDEN sütunundaki AYNI rakamlarla
    /// karışabiliyor. Bu sütun başlığı yanıltıcı biçimde "kod" içerse bile (kenar durumu), değerleri
    /// çoğunlukla tire-aralık deseninde olduğu için kod adayı olarak ASLA dönmemeli.</summary>
    [Fact]
    public void LoadCandidateCodeColumns_BedenYasAraligiSutunu_AdayOlarakElenir()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "GERÇEK KOD"; // yanıltıcı başlık: "kod" içeriyor ama içerik beden aralığı
        ws.Cell(1, 2).Value = "FİYAT";

        var ranges = new[] { "134-140-146-152", "2-3-4-5", "0-12-18-24", "98-104-110-116" };
        for (int i = 0; i < ranges.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = ranges[i];
            ws.Cell(i + 2, 2).Value = 100m + i;
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);
            Assert.Empty(candidates);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Üretim vakası (DECO KİDS, 2026-08-07): ürün kodlarının kendisi "26-158", "27-958"
    /// gibi TEK-tireli "NN-NNN" biçiminde. Bu şekil "98-104" gibi gerçek bir tek-tireli beden
    /// aralığıyla BİREBİR aynı görünüyor (SizeOrAgeRangeToken ayırt edemez), ama başlık açıkça
    /// "KOD" diyorsa (öncelik 2, en güvenilir sinyal) sütun ARTIK elenmemeli — eski davranışta
    /// KOD sütunu tamamen elenip geriye ÜRÜN ADI kalıyor, 65/65 görsel eşleşmiyordu.</summary>
    [Fact]
    public void LoadCandidateCodeColumns_TekTireliStilKoduAcikKodBasligiyla_AdayKalir()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "KOD";
        ws.Cell(1, 2).Value = "ÜRÜN ADI";
        ws.Cell(1, 3).Value = "YAŞ";
        ws.Cell(1, 4).Value = "FİYAT";

        var rows = new[]
        {
            ("26-158", "MİKİ BASKILI ERKEK İNTERLOK TAKIM", "06-18 AY", 179.95m),
            ("26-165", "BASKILI WAFFLE ERKEK TAKIM", "06-18 AY", 156.95m),
            ("26-178", "BASKILI CEPLİ İNTERLOK EKEK TAKIM", "09-24 AY", 223.45m),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            var (kod, ad, yas, fiyat) = rows[i];
            ws.Cell(i + 2, 1).Value = kod;
            ws.Cell(i + 2, 2).Value = ad;
            ws.Cell(i + 2, 3).Value = yas;
            ws.Cell(i + 2, 4).Value = fiyat;
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);

            // YAŞ, başlık kelimesiyle zaten elenir; KOD (öncelik 2) ve ÜRÜN ADI (öncelik 1, "ürün"
            // içeriyor) ikisi de aday kalır — asıl regresyon testi, eski hatanın (KOD'un yanlışlıkla
            // elenip geriye SADECE ÜRÜN ADI kalması) artık olmadığını doğrulamak.
            var kodColumn = Assert.Single(candidates, c => c.HeaderName == "KOD");
            Assert.Equal(2, kodColumn.HeaderPriority);
            Assert.Equal(3, kodColumn.Prices.Count);
            Assert.True(kodColumn.Prices.ContainsKey("26-158"));
            Assert.Equal(179.95m, kodColumn.Prices["26-158"]);

            // Nihai (tek-sütunlu) seçici de KOD'u (öncelik 2), ÜRÜN ADI'nı (öncelik 1) DEĞİL
            // seçmeli — LoadPricesFromExcel gerçek Worker akışının (tek-aday geriye dönük uyumluluk
            // sarmalayıcısı) kullandığı yol.
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            Assert.True(prices.ContainsKey("26-158"));
            Assert.False(prices.ContainsKey("MİKİ BASKILI ERKEK İNTERLOK TAKIM"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Aynı tek-tireli "NN-NNN" şekli, başlık ZAYIF/belirsiz olduğunda (örn. "ÜRÜN ADI"
    /// gibi asıl kod sütunu değil, başlıksız-tablo senaryosunda öncelik 0/1) eski güvenlik-öncelikli
    /// davranış korunmalı: şekil belirsizken başlık güçlü bir sinyal vermiyorsa yine elenir. Bu,
    /// bir önceki testle birlikte "başlık hakemdir, ama sadece güçlüyse" kuralını iki yönden de
    /// doğrular.</summary>
    [Fact]
    public void LoadCandidateCodeColumns_TekTireliDegerZayifBasliklaEleniyor()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "ÜRÜN"; // "ürün" -> öncelik 1 (zayıf/belirsiz), "kod" değil
        ws.Cell(1, 2).Value = "FİYAT";

        var ranges = new[] { "98-104", "104-110", "110-116" };
        for (int i = 0; i < ranges.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = ranges[i];
            ws.Cell(i + 2, 2).Value = 100m + i;
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);
            Assert.Empty(candidates);
        }
        finally { File.Delete(path); }
    }

    /// <summary>GERÇEK VAKA ("MİNA MİNO" / "2026 3 İP ALİSA FİYAT LİSTESİ.xlsx", 2026-08-10):
    /// tablo TAMAMEN başlıksız — A1:C1 birleşik hücresi marka adı + banner başlığı + " FİYAT"
    /// içeriyor, "KOD" diye bir sütun başlığı YOK. Kodlar DECO-KIDS tarzı tek-tireli ("26-400")
    /// — bu şekil gerçek bir beden/yaş aralığıyla (LooksLikeSizeOrAgeColumn) ayırt edilemez, o
    /// fonksiyonun kurtarma kuralı headerPriority>=2 gerektiriyor ama başlıksız yoldaki TEK aday
    /// hep priority=0. Önceki davranışta bu, TEK adayı sessizce eleyip LoadCandidateCodeColumns'ı
    /// BOŞ döndürüyordu — Worker.cs "olası bir ürün kodu sütunu bulunamadı" deyip klasörü HER
    /// turda atlıyordu, islendi.txt hiç yazılmadığı için klasör sonsuza dek "hiç işlenmemiş" gibi
    /// kalıyordu. Bir önceki test (zayıf ama VAR olan "ÜRÜN" başlığı) hâlâ elenmeli — bu ikisi
    /// birlikte "güvenlik ağı sadece gerçekten başlıksız tabloda devreye girer" kuralını
    /// doğrular.</summary>
    [Fact]
    public void LoadCandidateCodeColumns_BasliksizTekTireliKod_TekAdayEnAzGeriDonulur()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        // Banner satırı: marka adı + liste başlığı + " FİYAT" — hiçbiri "kod" içermiyor.
        ws.Cell(1, 1).Value = "MİNA MİNO";
        ws.Cell(1, 2).Value = "2026 MEVSİMLİK FİYAT LİSTESİ";
        ws.Cell(1, 3).Value = "FİYAT";

        var rows = new (string Code, string Desc, decimal Price)[]
        {
            ("26-400", "2-5 YAŞ 3 İP BASKILI AKSESUARLI KIZ TAKIM", 267.75m),
            ("26-401", "2-5 YAŞ 3 İP BASKILI AKSESUARLI KIZ TAKIM", 272.00m),
            ("26-402", "9-24 AY 3 İP BASKILI ERKEK TAKIM", 255.00m),
            ("26-403", "2-5 YAŞ 3 İP ÖN ROBA FİSTOLU AKSESUARLI KIZ TAKIM", 272.00m),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 5, 1).Value = rows[i].Code;
            ws.Cell(i + 5, 2).Value = rows[i].Desc;
            ws.Cell(i + 5, 3).Value = rows[i].Price;
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);

            var kod = Assert.Single(candidates);
            foreach (var (code, _, price) in rows)
            {
                Assert.True(kod.Prices.ContainsKey(code), $"Kod {code} bulunamadı — sütun sessizce elendi.");
                Assert.Equal(price, kod.Prices[code]);
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>GERÇEK VAKA (PODİUMİNİ "Price tl.xlsx", 2026-08-10): İngilizce bir sipariş-formu
    /// şablonu ("Code"/"Description"/"Qty"/"Unit Price"/"Price"). "Code" Türkçe "kod" alt-dizesini
    /// içermediği için hiçbir sütun kod adayı bulunamıyor, satır başlık satırı sayılmıyor ve akış
    /// başlıksız-tablo sezgisine düşüyordu; o sezgi de "Price" (=Qty×Unit Price, Qty hiç
    /// doldurulmadığı için HER satırda 0) sütununu "Unit Price" (gerçek fiyat) yerine seçiyordu —
    /// hiçbir sütun ondalık içermediği için (tam sayı fiyatlar) eski kod basitçe en sağdaki sayısal
    /// sütunu alıyordu. Sonuç: kod doğru bulunuyor ama fiyat hep 0, üretimde 27 gerçek ürün
    /// fotoğrafı müşteriye $0,00 damgalanıp gönderildi. Bu test hem "Code" başlığının artık
    /// tanınmasını hem de sıfır-sütun güvenlik ağının başlıksız yola da uygulandığını doğrular.</summary>
    [Fact]
    public void LoadPricesFromExcel_IngilizceSiparisFormuSablonu_UnitPriceSutunuSeçilirPriceDegil()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Code";
        ws.Cell(1, 2).Value = "Description";
        ws.Cell(1, 3).Value = "Qty";
        ws.Cell(1, 4).Value = "Unit Price";
        ws.Cell(1, 5).Value = "Price";

        var rows = new (string Code, int UnitPrice)[]
        {
            ("2512", 218),
            ("1010", 190),
            ("922", 190),
            ("943", 274),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Code;
            // Description ve Qty gerçek dosyada olduğu gibi BOŞ bırakılıyor.
            ws.Cell(i + 2, 4).Value = rows[i].UnitPrice;
            ws.Cell(i + 2, 5).Value = 0; // "Price" = Qty(boş/0) × Unit Price -> her zaman 0
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            foreach (var (code, unitPrice) in rows)
            {
                Assert.True(prices.ContainsKey(code), $"Kod {code} sözlükte yok.");
                Assert.Equal(unitPrice, prices[code]);
                Assert.NotEqual(0m, prices[code]);
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>"Barcode" (İngilizce) da tıpkı "Barkod" gibi hiçbir zaman kod sütunu adayı
    /// olmamalı — "code" alt-dizesini içerdiği için barkod istisnası eklenmezse yanlışlıkla
    /// öncelik 2 alıp gerçek "Code" sütunuyla birlikte (hatta onun önünde) aday sayılırdı.</summary>
    [Fact]
    public void LoadCandidateCodeColumns_BarcodeBasligiHicbirZamanAdayOlmaz()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Code";
        ws.Cell(1, 2).Value = "Barcode";
        ws.Cell(1, 3).Value = "Price";

        ws.Cell(2, 1).Value = "6250";
        ws.Cell(2, 2).Value = "2023965262502";
        ws.Cell(2, 3).Value = 490.00m;

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);

            var code = Assert.Single(candidates);
            Assert.Equal("Code", code.HeaderName);
            Assert.True(code.Prices.ContainsKey("6250"));
            Assert.DoesNotContain(candidates, c => c.HeaderName == "Barcode");
        }
        finally { File.Delete(path); }
    }

    /// <summary>DetectPriceColumn'ın sıfır-sütun güvenlik ağını İZOLE test eder: başlıklar
    /// ("Item"/"Notes"/"Count"/"Rate"/"Amount") "kod"/"code"/"fiyat"/"price"/"tutar" gibi HİÇBİR
    /// anahtar kelimeye uymuyor, bu yüzden akış kaçınılmaz olarak başlıksız-tablo sezgisine
    /// düşüyor (yukarıdaki PODİUMİNİ testi "Code"/"Price" ile Fix#1'i de devreye soktuğu için
    /// bu yolu tam izole etmiyordu). "Amount" (=Count×Rate, Count boş olduğu için hep 0) en
    /// sağda, gerçek fiyat "Rate" ondan önce — DetectPriceColumn'ın "en sağdaki sayısal sütun"
    /// tahmini sıfır-sütun kontrolü olmadan "Amount"ı seçerdi.</summary>
    [Fact]
    public void LoadPricesFromExcel_BasliksizTabloEnSagdakiSutunHepSifir_SolundakiGercekFiyataDuser()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Item";
        ws.Cell(1, 2).Value = "Notes";
        ws.Cell(1, 3).Value = "Count";
        ws.Cell(1, 4).Value = "Rate";
        ws.Cell(1, 5).Value = "Amount";

        var rows = new (string Code, int Rate)[]
        {
            ("2512", 218),
            ("1010", 190),
            ("922", 190),
            ("943", 274),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Code;
            ws.Cell(i + 2, 4).Value = rows[i].Rate;
            ws.Cell(i + 2, 5).Value = 0;
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            foreach (var (code, rate) in rows)
            {
                Assert.True(prices.ContainsKey(code), $"Kod {code} sözlükte yok.");
                Assert.Equal(rate, prices[code]);
                Assert.NotEqual(0m, prices[code]);
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>v11 (2026-08-06): Descriptions, kod/fiyat dışındaki serbest-metin hücrelerini
    /// (ör. "Malın Cinsi") birleştirip saklar — FullScanOcr'ın yaş-aralığı/aile-stili çapraz
    /// doğrulaması bunu kullanır. SADECE RAKAMDAN oluşan hücreler ("Sıra No", "Miktar") hariç
    /// tutulmalı — aksi halde aynı ürün ailesinin farklı yaş-grubu satırları (yaş aralığı hariç
    /// birebir aynı olması gereken) sıra numarası gibi satırdan satıra değişen gürültü yüzünden
    /// hiçbir zaman eşleşmez (gerçek vaka: Cuento 4224/4225/4226).</summary>
    [Fact]
    public void LoadCandidateCodeColumns_Descriptions_SadeceMetinHucreleriBirlesirRakamHariçTutulur()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Fiyat Listesi");
        ws.Cell(1, 1).Value = "Sıra No";
        ws.Cell(1, 2).Value = "Stok Kodu";
        ws.Cell(1, 3).Value = "Malın Cinsi";
        ws.Cell(1, 4).Value = "Fiyatı";
        ws.Cell(1, 5).Value = "Miktar";

        ws.Cell(2, 1).Value = 20;
        ws.Cell(2, 2).Value = "4224";
        ws.Cell(2, 3).Value = "2-5 yaş flam pamuk düşük kol patlı";
        ws.Cell(2, 4).Value = 295.00m;
        ws.Cell(2, 5).Value = 0;

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}.xlsx");
        wb.SaveAs(path);
        try
        {
            var candidates = ExcelPriceReader.LoadCandidateCodeColumns(path);

            var stokKodu = Assert.Single(candidates);
            Assert.True(stokKodu.Descriptions.TryGetValue("4224", out var desc));
            Assert.Equal("2-5 yaş flam pamuk düşük kol patlı", desc);
            Assert.DoesNotContain("20", desc);
            Assert.DoesNotContain(" 0", desc);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>ClosedXML sadece OOXML (.xlsx/.xlsm, ZIP tabanlı) formatını açabilir; eski .xls (BIFF,
/// ikili) formatında gelen bir dosyada ZIP olarak açmaya çalışırken istisna fırlatır — bu durumda
/// ExcelDataReader'a düşülür (üretim vakası, 2026-08-03: müşteri fiyat listesini .xls olarak
/// göndermişti; sadece dosya adını .xlsx'e çevirmek içeriği değiştirmediği için ClosedXML yine
/// açamadı). Burada NPOI'nin HSSFWorkbook'u ile gerçek bir .xls dosyası üretilip bu geri düşüşün
/// çalıştığı doğrulanıyor (NPOI sadece test projesinde, fixture üretmek için kullanılıyor).</summary>
public class ExcelPriceReaderXlsFallbackTests
{
    private static string WriteXlsFixture((string Code, double Price)[] rows, string extension)
    {
        var wb = new NPOI.HSSF.UserModel.HSSFWorkbook();
        var sheet = wb.CreateSheet("Fiyat Listesi");

        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue("Kod");
        header.CreateCell(1).SetCellValue("Fiyat");

        for (int i = 0; i < rows.Length; i++)
        {
            var row = sheet.CreateRow(i + 1);
            row.CreateCell(0).SetCellValue(rows[i].Code);
            row.CreateCell(1).SetCellValue(rows[i].Price);
        }

        var path = Path.Combine(Path.GetTempPath(), $"pricebot_test_{Guid.NewGuid():N}{extension}");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        wb.Write(fs);
        return path;
    }

    [Fact]
    public void LoadPricesFromExcel_GercekXlsDosyasi_ExcelDataReaderIleOkunur()
    {
        var path = WriteXlsFixture([("1374", 228.0), ("1375", 204.25), ("1376", 218.50)], ".xls");
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path, out var skipped);

            Assert.Empty(skipped);
            Assert.Equal(228.0m, prices["1374"]);
            Assert.Equal(204.25m, prices["1375"]);
            Assert.Equal(218.50m, prices["1376"]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Bir kullanıcı sadece dosya adını ".xlsx" olarak değiştirip içeriği hâlâ eski .xls
    /// biçiminde bırakırsa, karar UZANTIYA değil dosyanın GERÇEK içeriğine dayanmalı — ClosedXML
    /// yine açamaz ve ExcelDataReader'a düşülür, uzantı yanıltıcı olsa da okuma başarılı olur.</summary>
    [Fact]
    public void LoadPricesFromExcel_XlsIcerigiYanlislikla_XlsxUzantisiyla_YineOkunur()
    {
        var path = WriteXlsFixture([("5212", 234.0)], ".xlsx");
        try
        {
            var prices = ExcelPriceReader.LoadPricesFromExcel(path);
            Assert.Equal(234.0m, prices["5212"]);
        }
        finally { File.Delete(path); }
    }
}
