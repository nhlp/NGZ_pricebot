using System.Globalization;
using ClosedXML.Excel;
using Xunit;

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
}
