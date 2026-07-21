using System.Globalization;
using ClosedXML.Excel;

public class ExcelPriceReader
{
    public static Dictionary<string, decimal> LoadPricesFromExcel(string excelPath) =>
        LoadPricesFromExcel(excelPath, out _);

    /// <summary><paramref name="skippedRows"/>, kod hücresi boş olmayan ama fiyatı sayıya
    /// çevrilemeyen satırları (satır no, kod, ham fiyat metni) listeler — üretimde bazı
    /// hücrelerin "228 TL" gibi para birimi son ekiyle METİN olarak girilmesi (bazılarının ise
    /// düz sayı olması) satırların sessizce kaybolmasına yol açmıştı; artık bu liste sayesinde
    /// hangi satırın neden atlandığı loglanabilir.</summary>
    public static Dictionary<string, decimal> LoadPricesFromExcel(string excelPath, out List<(int Row, string Code, string RawPrice)> skippedRows)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        skippedRows = [];

        using var workbook = new XLWorkbook(excelPath);
        var sheet = workbook.Worksheets.First();

        // Başlık satırı ilk kullanılan satır olmayabilir (örn. üstte birleşik bir başlık/logo
        // hücresi olabilir); "kod" ve "fiyat" sütunlarını içeren satır aranarak bulunur.
        IXLRow? headerRow = null;
        int codeCol = -1;
        int priceCol = -1;

        foreach (var row in sheet.RowsUsed())
        {
            int hCode = -1, hPrice = -1;
            foreach (var cell in row.CellsUsed())
            {
                string colName = cell.GetString().Trim().ToLower();
                if (colName.Contains("kod") || colName.Contains("sku") || colName.Contains("id")) hCode = cell.Address.ColumnNumber;
                if (colName.Contains("fiyat") || colName.Contains("tutar") || colName.Contains("price")) hPrice = cell.Address.ColumnNumber;
            }

            if (hCode != -1 && hPrice != -1)
            {
                headerRow = row;
                codeCol = hCode;
                priceCol = hPrice;
                break;
            }
        }

        if (headerRow is null)
        {
            headerRow = sheet.FirstRowUsed();
            if (headerRow is null) return prices;
            codeCol = headerRow.FirstCell().Address.ColumnNumber;
            priceCol = codeCol + 1;
        }

        foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() > headerRow.RowNumber()))
        {
            try
            {
                string productCode = row.Cell(codeCol).GetString().Trim();
                if (string.IsNullOrEmpty(productCode)) continue;

                string priceRaw = row.Cell(priceCol).GetString();
                if (TryParsePrice(priceRaw, out decimal priceTry))
                {
                    prices[productCode] = priceTry;
                }
                else
                {
                    skippedRows.Add((row.RowNumber(), productCode, priceRaw));
                }
            }
            catch
            {
                // Hatalı satırları atla
            }
        }

        return prices;
    }

    /// <summary>Fiyat hücreleri iki farklı biçimde gelebiliyor: düz sayı (204,25) veya insan
    /// tarafından para birimi son ekiyle girilmiş metin ("228 TL", "218,50 TL"). Ayrıca ondalık
    /// ayırıcı virgül (Türkçe, "204,25") veya nokta ("204.25") olabilir; ortam kültürüne
    /// (CurrentCulture) güvenmek yerine ayırıcının kendisine bakarak açıkça yorumlanır — bu,
    /// worker'ın hangi hesap/kültür altında çalıştığından bağımsız, deterministik bir sonuç verir.</summary>
    private static bool TryParsePrice(string raw, out decimal price)
    {
        raw = raw.Trim();

        // Para birimi son ekini/sembolünü ve boşlukları temizle (ör. "228 TL" -> "228",
        // "218,50 TL" -> "218,50", "₺190" -> "190").
        raw = raw.Replace("TL", "", StringComparison.OrdinalIgnoreCase)
                  .Replace("TRY", "", StringComparison.OrdinalIgnoreCase)
                  .Replace("₺", "")
                  .Trim();

        bool hasComma = raw.Contains(',');
        bool hasDot = raw.Contains('.');

        if (hasComma && !hasDot)
        {
            // Sadece virgül var: Türkçe ondalık ayırıcı ("204,25" -> 204.25).
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.GetCultureInfo("tr-TR"), out price);
        }

        if (hasComma && hasDot)
        {
            // İkisi de var: Türkçe biçim varsayılır — nokta binlik, virgül ondalık (ör. "1.234,56").
            var normalized = raw.Replace(".", "").Replace(",", ".");
            return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out price);
        }

        // Sadece nokta var ya da hiç ayırıcı yok: nokta-ondalık / düz tam sayı.
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out price);
    }
}
