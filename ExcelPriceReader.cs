using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ExcelDataReader;

/// <summary>Tire ile ayrılmış rakam listelerini (beden/yaş/ay aralığı: "134-140-146-152",
/// "2-3-4-5", "0-12-18-24") tespit eder. Bu değerler ürün kodu DEĞİLDİR, ama hem Excel'in
/// YAŞ/BEDEN gibi sütunlarında hem de görsellerin üzerine basılı beden tablolarında AYNI
/// rakamlar göründüğü için (2026-08-03 vakası), kod adayı çıkarımında (hem Excel sütunu seçiminde
/// hem OCR aday çıkarımında) elenmeleri gerekir — aksi halde OCR'ın bir beden tablosundan okuduğu
/// rastgele bir rakam dizisi, tesadüfen bir ürün koduyla veya yanlış bir Excel sütunuyla eşleşebilir.
/// FullScanOcr.cs bu sınıfı (global namespace, using gerekmeden) doğrudan kullanır.</summary>
internal static class SizeOrAgeRangeToken
{
    private static readonly Regex Pattern = new(@"^\d{1,4}(-\d{1,4}){1,}$", RegexOptions.Compiled);

    public static bool IsMatch(string token) => Pattern.IsMatch(token.Trim());
}

/// <summary>Excel'deki OLASI bir ürün kodu sütunu: başlık metni, öncelik puanı ve o sütuna göre
/// yüklenmiş kod->fiyat sözlüğü. Birden fazla sütun "kod" gibi görünebilir (ör. "KOD-B" +
/// "BARKOD", ya da kod sütunu "MODEL" başlıklı olabilir) — başlık metni tek başına güvenilir
/// değildir, bu yüzden Worker.cs birden fazla aday üretip klasördeki gerçek OCR kanıtıyla
/// (hangi sütunun kodları görsellerde fiilen okunuyor) hangisinin doğru olduğuna karar verir.</summary>
public sealed record CodeColumnCandidate(
    string HeaderName,
    int ColumnNumber,
    int HeaderPriority,
    Dictionary<string, decimal> Prices,
    List<(int Row, string Code, string RawPrice)> SkippedRows,
    Dictionary<string, string> Descriptions);

public class ExcelPriceReader
{
    static ExcelPriceReader()
    {
        // Eski .xls (BIFF) dosyaları genelde Unicode değil, ANSI kod sayfası (ör. Türkçe için
        // Windows-1254) kullanır; .NET Core bu sağlayıcı kayıtlı olmadan bu kod sayfalarını
        // çözemez. ExcelDataReader'ın .xls okurken ihtiyaç duyduğu tek seferlik kayıt burada yapılır.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Dictionary<string, decimal> LoadPricesFromExcel(string excelPath) =>
        LoadPricesFromExcel(excelPath, out _);

    /// <summary>Geriye dönük uyumluluk için tek-sütunlu sarmalayıcı: <see cref="LoadCandidateCodeColumns"/>'ın
    /// bulduğu adaylar arasından öncelik puanı en yüksek olanı (eşitlikte en çok ürün içereni) seçer.
    /// OCR kanıtına göre karşılaştırmalı seçim YAPMAZ — bunun için Worker.cs, klasördeki görsellerin
    /// OCR sonuçlarıyla adaylar arasında oylama yapar (bkz. Worker.ResolveCodeColumnAsync benzeri akış).
    /// <paramref name="skippedRows"/>, kod hücresi boş olmayan ama fiyatı sayıya çevrilemeyen satırları
    /// (satır no, kod, ham fiyat metni) listeler — üretimde bazı hücrelerin "228 TL" gibi para birimi
    /// son ekiyle METİN olarak girilmesi satırların sessizce kaybolmasına yol açmıştı.</summary>
    public static Dictionary<string, decimal> LoadPricesFromExcel(string excelPath, out List<(int Row, string Code, string RawPrice)> skippedRows)
    {
        var best = LoadCandidateCodeColumns(excelPath)
            .OrderByDescending(c => c.HeaderPriority)
            .ThenByDescending(c => c.Prices.Count)
            .FirstOrDefault();

        skippedRows = best?.SkippedRows ?? [];
        return best?.Prices ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Başlık satırındaki TÜM olası kod sütunlarını (bir tane değil) adaylar olarak döner.
    /// Öncelik puanı: 2 = "kod"/"sku"/"id" içeren ama "barkod"/"yaş"/"yas"/"beden" OLMAYAN başlık
    /// (en güvenilir sinyal), 1 = "model"/"stok"/"ürün" içeren başlık, 0 = hiçbir başlık eşleşmediği
    /// için ilk sütunu kod sayan eski geriye dönük davranış. "barkod" veya "yaş"/"yas"/"beden"
    /// içeren başlıklar HİÇBİR ZAMAN aday olarak eklenmez (sütun içeriği ne olursa olsun) —
    /// üretim vakası (ERAY KIDS, 2026-08-03): "KOD-B" + "BARKOD" aynı başlık satırında, eski kod
    /// "kod" alt-dizesi için satırı soldan sağa tararken barkod sütununü son eşleşen olarak üzerine
    /// yazıyordu; tüm 145 satır 13 haneli EAN değeriyle anahtarlanmış, OCR'ın görselden doğru
    /// okuduğu 4 haneli stil numarasıyla (regex \d{3,7}) hiçbir zaman tam olarak eşleşemedi (string
    /// eşitliği, alt-dize değil) ve klasördeki HİÇBİR görsel damgalanamadı. Barkod bir EAN olarak
    /// yapısal olarak asla gerçek ürün kodu OLAMAYACAĞI için (uzunluk uyuşmazlığı yapısal, tesadüfi
    /// değil), OCR oylamasına bırakılmadan kaynakta tamamen elenir. Yaş/beden sütunları da aynı
    /// şekilde elenir çünkü ürün kodu değil beden/yaş tablosu değeridir — değer bazlı
    /// <see cref="LooksLikeSizeOrAgeColumn"/> kontrolü sadece tire'li ARALIK ("134-140") içeren
    /// sütunları yakalar, tek bir yaş/beden sayısı ("3", "5") içeren sütunları yakalamaz; başlık
    /// kontrolü bu boşluğu kapatır. Değerleri çoğunlukla tire ile ayrılmış rakam listesi (beden/yaş
    /// aralığı) olan sütunlar da, başlığı ne olursa olsun tamamen elenir — bunlar asla ürün kodu
    /// olamaz (bkz. <see cref="SizeOrAgeRangeToken"/>).</summary>
    public static List<CodeColumnCandidate> LoadCandidateCodeColumns(string excelPath) =>
        LoadCandidateCodeColumns(LoadGrid(excelPath));

    /// <summary>Excel'i düz bir satır/sütun metin ızgarasına yükler — aşağıdaki kod/fiyat sütunu
    /// tespiti mantığı, veriyi hangi kütüphanenin ürettiğiyle ilgilenmeden bu ızgara üzerinden
    /// çalışır. Önce ClosedXML denenir (gerçek .xlsx/.xlsm, OOXML/ZIP tabanlı). ClosedXML bir
    /// dosyayı ZIP olarak açamayınca istisna fırlatır — bu neredeyse her zaman dosyanın aslında
    /// eski .xls (BIFF, ikili) formatında olduğu anlamına gelir (üretim vakası, 2026-08-03: müşteri
    /// fiyat listesini .xls olarak göndermişti; yalnızca uzantısını .xlsx'e çevirmek dosyanın iç
    /// biçimini değiştirmez, ClosedXML yine açamaz). Bu durumda ExcelDataReader'a düşülür — hem
    /// .xls hem .xlsx okuyabilen, formatı dosya içeriğinden (uzantıdan değil) tespit eden ayrı bir
    /// kütüphane.</summary>
    private static List<GridRow> LoadGrid(string excelPath)
    {
        try
        {
            using var workbook = new XLWorkbook(excelPath);
            var sheet = workbook.Worksheets.First();
            return sheet.RowsUsed()
                .Select(r => new GridRow(r.RowNumber(),
                    r.CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => c.GetString().Trim())))
                .ToList();
        }
        catch (Exception)
        {
            return LoadGridWithExcelDataReader(excelPath);
        }
    }

    private static List<GridRow> LoadGridWithExcelDataReader(string excelPath)
    {
        using var stream = File.Open(excelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var table = reader.AsDataSet().Tables[0];

        var rows = new List<GridRow>();
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var cells = new Dictionary<int, string>();
            for (int c = 0; c < table.Columns.Count; c++)
            {
                var text = table.Rows[r][c]?.ToString()?.Trim() ?? "";
                if (text.Length > 0) cells[c + 1] = text;
            }
            // ClosedXML'in RowsUsed()'ı gibi tamamen boş satırlar atlanır — aşağıdaki mantık zaten
            // sadece "kod"/"fiyat" içeren dolu hücrelere bakıyor, boş satırlar gürültüden ibaret.
            if (cells.Count > 0) rows.Add(new GridRow(r + 1, cells));
        }
        return rows;
    }

    /// <summary>Tek bir Excel satırının 1-tabanlı sütun numarasından hücre metnine eşlemesi —
    /// ClosedXML'in <c>IXLRow</c>/<c>IXLCell</c>'inin format-bağımsız karşılığı.</summary>
    private sealed record GridRow(int RowNumber, IReadOnlyDictionary<int, string> Cells)
    {
        public string Cell(int col) => Cells.TryGetValue(col, out var v) ? v : "";
    }

    /// <summary>Başlık satırı ilk kullanılan satır olmayabilir (örn. üstte birleşik bir başlık/logo
    /// hücresi olabilir); "kod" ve "fiyat" sütunlarını içeren satır aranarak bulunur.</summary>
    private static List<CodeColumnCandidate> LoadCandidateCodeColumns(List<GridRow> rows)
    {
        GridRow? headerRow = null;
        int priceCol = -1;
        var codeColumnHeaders = new List<(int Col, string Name, int Priority)>();

        foreach (var row in rows)
        {
            var rowCandidates = new List<(int Col, string Name, int Priority)>();
            var rowPriceCandidates = new List<(int Col, int Priority)>();
            foreach (var (col, raw) in row.Cells)
            {
                string colName = raw.ToLower();

                // "barkod" da alt dize olarak "kod" içerir, "beden kodu"/"yaş kodu" gibi başlıklar
                // da "kod" içerebilir — bu yüzden ikisi de "kod" kontrolünden ÖNCE ayrıca kontrol
                // edilip tamamen elenir (priority -1), sütun içeriği ne olursa olsun. Barkod
                // (EAN, 13 hane) yapısal olarak OCR'ın görselden okuduğu kısa ürün koduyla (regex
                // \d{3,7}) asla tam string eşitliğiyle eşleşemez; aday bırakılırsa (üretim vakası,
                // ERAY KIDS 2026-08-03: "KOD-B" + "BARKOD" aynı başlık satırında) eski kod bu
                // yüzden barkod sütununu gerçek kod sütununun üzerine yazıyordu. Yaş/beden sütunları
                // ürün kodu DEĞİL beden/yaş tablosu değeridir (bkz. SizeOrAgeRangeToken) — değer
                // bazlı LooksLikeSizeOrAgeColumn kontrolü sadece verisi tire'li ARALIK ("134-140")
                // olan sütunları yakalar, tek bir yaş/beden sayısı ("3", "5") içeren sütunları
                // yakalamaz; başlıkta "yaş"/"yas"/"beden" geçmesi tek başına yeterli bir sinyal
                // olduğu için burada da kaynakta tamamen dışlanır. "model" gibi daha belirsiz
                // başlıklar da (başka bir üretim vakası: gerçek kod "MODEL" sütununda basılıydı)
                // orta öncelikli aday sayılır.
                int priority = (colName.Contains("barkod") || colName.Contains("yaş") || colName.Contains("yas") || colName.Contains("beden")) ? -1
                    : (colName.Contains("kod") || colName.Contains("sku") || colName.Contains("id")) ? 2
                    : (colName.Contains("model") || colName.Contains("stok") || colName.Contains("ürün") || colName.Contains("urun")) ? 1
                    : -1;
                if (priority >= 0) rowCandidates.Add((col, raw, priority));

                // "Tutarı" (fiyat × miktar) da "fiyat" gibi bir para sütunu görünür ama miktar
                // girilmediğinde 0'dır (üretim vakası, 2026-08-03: "Fiyatı" + "Tutarı" aynı
                // başlık satırında, Tutarı sonda geldiği için hPrice'ı eziyordu, tüm fiyatlar
                // 0 basıldı). "fiyat"/"price" her zaman "tutar"dan önceliklidir; sütun sırası
                // bağımsız — ama öncelik tek başına yeterli değil, aşağıda tamamı boş/sıfır olan
                // adaylar da elenir (bkz. ColumnHasAnyNonZeroValue).
                int pricePriority = (colName.Contains("fiyat") || colName.Contains("price")) ? 2
                    : colName.Contains("tutar") ? 1
                    : -1;
                if (pricePriority >= 0) rowPriceCandidates.Add((col, pricePriority));
            }

            // Fiyat sütunu adayları arasından en yüksek öncelikliden başlanarak, veri satırlarının
            // TAMAMI boş/sıfır olmayan ilk aday seçilir. Miktar girilmemiş "Tutarı" gibi hep-sıfır
            // bir sütun, başlığı "fiyat"tan düşük öncelikli olsa bile yanlışlıkla tek aday kalırsa
            // (ör. gerçek "Fiyatı" sütunu bir nedenle elenmişse) sessizce $0.00 üretmek yerine
            // reddedilir — bu satır hiç başlık satırı olarak kabul edilmez, tarama bir sonraki
            // satırla devam eder.
            int hPrice = rowPriceCandidates
                .OrderByDescending(p => p.Priority)
                .ThenBy(p => p.Col)
                .Where(p => ColumnHasAnyNonZeroValue(rows, row.RowNumber, p.Col))
                .Select(p => (int?)p.Col)
                .FirstOrDefault() ?? -1;

            if (rowCandidates.Count > 0 && hPrice != -1)
            {
                headerRow = row;
                priceCol = hPrice;
                codeColumnHeaders = rowCandidates;
                break;
            }
        }

        if (headerRow is null)
        {
            headerRow = rows.FirstOrDefault();
            if (headerRow is null) return [];
            int codeCol = headerRow.Cells.Keys.Min();
            priceCol = codeCol + 1;
            codeColumnHeaders = [(codeCol, headerRow.Cell(codeCol), 0)];
        }

        var result = new List<CodeColumnCandidate>();
        foreach (var (col, name, priority) in codeColumnHeaders)
        {
            if (LooksLikeSizeOrAgeColumn(rows, headerRow.RowNumber, col)) continue;

            var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var skippedRows = new List<(int Row, string Code, string RawPrice)>();

            foreach (var row in rows.Where(r => r.RowNumber > headerRow.RowNumber))
            {
                try
                {
                    string productCode = row.Cell(col).Trim();
                    if (string.IsNullOrEmpty(productCode)) continue;

                    string priceRaw = row.Cell(priceCol);
                    if (TryParsePrice(priceRaw, out decimal priceTry))
                    {
                        prices[productCode] = priceTry;
                    }
                    else
                    {
                        skippedRows.Add((row.RowNumber, productCode, priceRaw));
                    }

                    // Kod/fiyat dışındaki hücreler (ör. "Malın Cinsi", "Ürün Adı" gibi serbest-
                    // metin sütunlar) birleştirilip saklanır — hangi sütunun "açıklama" olduğunu
                    // başlıktan tahmin etmeye çalışmak yerine (kırılgan, layout'a göre değişir),
                    // FullScanOcr'daki yaş-aralığı/aile-stili çapraz doğrulaması için satırın
                    // serbest metnini kullanmak yeterli ve daha sağlam (bkz. FullScanOcr v11).
                    // SADECE RAKAMDAN oluşan hücreler (Sıra No, Miktar gibi) dışlanır — bunlar
                    // satırdan satıra değişen gürültüdür ve aynı ürün ailesinin farklı yaş-grubu
                    // satırlarının "yaş aralığı hariç" birebir aynı metne indirgenmesini bozar.
                    var otherCells = row.Cells
                        .Where(c => c.Key != col && c.Key != priceCol)
                        .OrderBy(c => c.Key)
                        .Select(c => c.Value)
                        .Where(v => !string.IsNullOrWhiteSpace(v) && v.Any(ch => !char.IsDigit(ch)));
                    descriptions[productCode] = string.Join(" | ", otherCells);
                }
                catch
                {
                    // Hatalı satırları atla
                }
            }

            if (prices.Count > 0)
                result.Add(new CodeColumnCandidate(name, col, priority, prices, skippedRows, descriptions));
        }

        return result;
    }

    /// <summary>Bir sütunun değerleri çoğunlukla tire ile ayrılmış rakam listesiyse (beden/yaş/ay
    /// aralığı) bu bir kod sütunu OLAMAZ, başlığı yanıltıcı biçimde "kod" içerse bile (kenar durumu,
    /// ama ucuz bir güvenlik önlemi). İlk birkaç dolu hücre örneklenir, tamamını taramaya gerek yok.</summary>
    private static bool LooksLikeSizeOrAgeColumn(List<GridRow> rows, int headerRowNumber, int col, int sampleSize = 15)
    {
        int total = 0, rangeLike = 0;
        foreach (var row in rows.Where(r => r.RowNumber > headerRowNumber))
        {
            var val = row.Cell(col).Trim();
            if (string.IsNullOrEmpty(val)) continue;

            total++;
            if (SizeOrAgeRangeToken.IsMatch(val)) rangeLike++;
            if (total >= sampleSize) break;
        }

        return total > 0 && rangeLike * 2 >= total;
    }

    /// <summary>Bir fiyat sütunu adayının veri satırlarında en az bir sıfırdan farklı, geçerli
    /// fiyat olup olmadığını kontrol eder. "Tutarı" gibi formül sütunları (fiyat × miktar) miktar
    /// hiç girilmediğinde her satırda 0 döner — böyle bir sütun ne kadar "fiyat" gibi görünen bir
    /// başlığa sahip olursa olsun gerçek bir fiyat kaynağı DEĞİLDİR, seçilirse her ürün $0.00
    /// basılır (üretim vakası, 2026-08-03). Tamamı boş/sıfır olan bir sütun asla seçilmemeli.</summary>
    private static bool ColumnHasAnyNonZeroValue(List<GridRow> rows, int headerRowNumber, int col)
    {
        foreach (var row in rows.Where(r => r.RowNumber > headerRowNumber))
        {
            string raw = row.Cell(col);
            if (TryParsePrice(raw, out decimal value) && value != 0m) return true;
        }

        return false;
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
