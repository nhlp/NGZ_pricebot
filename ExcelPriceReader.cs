using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ExcelDataReader;

/// <summary>Tire ile ayrılmış rakam listelerini (beden/yaş/ay aralığı: "134-140-146-152",
/// "2-3-4-5", "0-12-18-24", ayrıca TEK-tireli iki-sayı bir "beden" çifti olan "98-104" gibi)
/// tespit eder. Bu değerler ürün kodu DEĞİLDİR, ama hem Excel'in YAŞ/BEDEN gibi sütunlarında hem
/// de görsellerin üzerine basılı beden tablolarında AYNI rakamlar göründüğü için (2026-08-03
/// vakası), OCR aday çıkarımında SAF RAKAM alt-dizisi çıkarılmadan önce elenmeleri gerekir — aksi
/// halde OCR'ın bir beden tablosundan okuduğu rastgele bir rakam dizisi ("98-104" -> "104"),
/// tesadüfen bir ürün koduyla eşleşebilir. Bu dosyanın PaddleScanOcr.cs (eski FullScanOcr.cs)
/// tarafından kullanımı: SADECE saf-rakam alt-dizisi çıkarımını (TryExtract) engeller — tire
/// dahil ham token'ın kendisi hâlâ ayrı bir aday olarak denenir (bkz. PaddleScanOcr.TryExtract),
/// çünkü DECO KİDS vakası (2026-08-07) TEK-tireli "26-158"/"27-958" biçiminde gerçek ürün
/// kodları da olabildiğini gösterdi — "98-104" (gerçek beden aralığı) ile "26-158" (gerçek kod)
/// AYNI şekle sahip, şekilden ayırt edilemezler; MatchExact'ın Excel'e karşı tam-eşitlik kontrolü
/// nihai hakemdir. Excel sütunu seçiminde (ExcelPriceReader.LooksLikeSizeOrAgeColumn) de aynı
/// belirsizlik var — orada başlık metni (açık "kod"/"sku"/"id") tek-tireli belirsiz çiftler için
/// hakem olarak kullanılır, bkz. o fonksiyonun yorumu.</summary>
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
    /// Öncelik puanı: 2 = "kod"/"code"/"sku"/"id" içeren ama "barkod"/"barcode"/"yaş"/"yas"/"beden"
    /// OLMAYAN başlık (en güvenilir sinyal), 1 = "model"/"stok"/"ürün" içeren başlık, 0 = hiçbir
    /// başlık eşleşmediği için ilk sütunu kod sayan eski geriye dönük davranış. "barkod"/"barcode"
    /// veya "yaş"/"yas"/"beden" içeren başlıklar HİÇBİR ZAMAN aday olarak eklenmez (sütun içeriği ne
    /// olursa olsun) — üretim vakası (ERAY KIDS, 2026-08-03): "KOD-B" + "BARKOD" aynı başlık
    /// satırında, eski kod "kod" alt-dizesi için satırı soldan sağa tararken barkod sütununü son
    /// eşleşen olarak üzerine yazıyordu; tüm 145 satır 13 haneli EAN değeriyle anahtarlanmış, OCR'ın
    /// görselden doğru okuduğu 4 haneli stil numarasıyla (regex \d{3,7}) hiçbir zaman tam olarak
    /// eşleşemedi (string eşitliği, alt-dize değil) ve klasördeki HİÇBİR görsel damgalanamadı.
    /// Barkod bir EAN olarak yapısal olarak asla gerçek ürün kodu OLAMAYACAĞI için (uzunluk
    /// uyuşmazlığı yapısal, tesadüfi değil), OCR oylamasına bırakılmadan kaynakta tamamen elenir.
    /// Yaş/beden sütunları da aynı şekilde elenir çünkü ürün kodu değil beden/yaş tablosu değeridir —
    /// değer bazlı <see cref="LooksLikeSizeOrAgeColumn"/> kontrolü sadece tire'li ARALIK ("134-140")
    /// içeren sütunları yakalar, tek bir yaş/beden sayısı ("3", "5") içeren sütunları yakalamaz;
    /// başlık kontrolü bu boşluğu kapatır. Değerleri çoğunlukla tire ile ayrılmış rakam listesi
    /// (beden/yaş aralığı) olan sütunlar da, başlığı ne olursa olsun tamamen elenir — bunlar asla
    /// ürün kodu olamaz (bkz. <see cref="SizeOrAgeRangeToken"/>). "code"/"barcode" (İngilizce)
    /// 2026-08-10'da eklendi — üretim vakası (PODİUMİNİ "Price tl.xlsx"): başlıklar İngilizce
    /// bir sipariş-formu şablonundan ("Code"/"Description"/"Qty"/"Unit Price"/"Price") geliyordu;
    /// "Code" Türkçe "kod" alt-dizesini içermediği için hiçbir sütun kod adayı bulunamıyor, tüm
    /// satır başlık tespitinden reddediliyor ve akış başlıksız-tablo sezgisine düşüyordu — o
    /// sezginin fiyat sütunu tahmini de ayrı bir hataya yol açtı (bkz. <see cref="DetectPriceColumn"/>
    /// yorumu), sonuç: 27 görsel gerçek müşteriye $0,00 damgalanıp gönderildi.</summary>
    public static List<CodeColumnCandidate> LoadCandidateCodeColumns(string excelPath) =>
        LoadCandidateCodeColumns(LoadGrid(excelPath));

    /// <summary>Kod/fiyat başlık satırından ÖNCEKİ satırlardaki (firma adı/logo yanı/adres/
    /// telefon/site gibi "letterhead" alanı) tüm hücre metnini, <see cref="BrandMatcher.
    /// MatchFromOcrTokens"/>'a doğrudan verilebilecek bir token sözlüğüne çevirir. Gerçek vaka
    /// (2026-08-11, PRETTY LİFE): bazı üretici Excel'lerinde firma adı üst bilgide ("PRETTY
    /// LİFE TEKSTİL İNŞ...") gerçek hücre metni olarak duruyor ama HİÇBİR ürün görselinde marka
    /// adı basılı değil (görsellerde sadece "new adress"/"COUTURE" gibi jenerik tasarım
    /// ibareleri var) — bu yüzden Worker.ResolveFolderBrandAsync, OCR (kod taraması + renk
    /// kurtarma) markayı bulamadığında/çelişkili bulduğunda, Gemini'ye (ağ çağrısı,
    /// gecikme+kota) başvurmadan önce bu ücretsiz/deterministik kaynağı dener.
    ///
    /// Kapsam BİLİNÇLİ olarak SADECE başlık satırından önceki satırlarla sınırlı — ürün
    /// açıklama sütunları hiç taranmaz. Gerekçe: CLAUDE.md'deki "KIDSWEAR"/ALİSA karışık-katalog
    /// vakasıyla aynı risk sınıfı — bir Excel'in ürün açıklamaları birden fazla üreticinin
    /// ürününü karıştırabilir (jenerik bir tagline üçüncü, alakasız bir markayla tesadüfen
    /// çakışabilir), oysa firma letterhead'i (gönderen/üreten firma) tek ve sabittir, çok daha
    /// güvenilir bir kanıt kaynağıdır. Her hücre metni tek bir "token" olarak, sabit yüksek
    /// güvenle (100 — gerçek dijital metin, OCR gürültüsü yok) eklenir; BrandMatcher zaten kendi
    /// içinde kelimelere ayırıp (NormalizeToWords) aynı kelime-bazlı-tam-eşleşme + jenerik-kelime
    /// filtresi + çelişkili-NetCarpan güvenlik ağını uygular. Eşleşme bulunamazsa (marka bu
    /// alanda hiç geçmiyorsa) davranış hiç değişmez, akış Gemini'ye/WhatsApp sorusuna düşer.</summary>
    public static Dictionary<string, float> ExtractLetterheadTokens(string excelPath)
    {
        var rows = LoadGrid(excelPath);
        var (_, headerRowNumber) = LoadCandidateCodeColumnsCore(rows);

        var tokens = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.RowNumber < headerRowNumber))
            foreach (var text in row.Cells.Values)
                if (!string.IsNullOrWhiteSpace(text))
                    tokens[text] = 100f;

        return tokens;
    }

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
        // ClosedXML'in yol-tabanlı XLWorkbook(string) kurucusu, içerik gerçekte .xls (BIFF) olup
        // ZIP olarak açılamadığında kendi içeride açtığı dosya akışını HER ZAMAN deterministik
        // kapatmıyor — handle'ın sadece bir GC finalizer geçişiyle serbest kaldığı gözlemlendi
        // (2026-08-11, ExcelPriceReaderXlsFallbackTests'te tespit edildi: xls-içerikli-ama-.xlsx-
        // uzantılı fixture'da LoadPricesFromExcel doğru sonucu döndürüyor ama testin finally
        // bloğundaki File.Delete "dosya başka bir process tarafından kullanılıyor" IOException'ıyla
        // başarısız oluyordu — sızıntı bu metodun kendisinde değil, aşağıya düşülen
        // LoadGridWithExcelDataReader tarafında da değil, sadece ClosedXML'in path-kurucusunda).
        // Çözüm: dosyayı KENDİ açtığımız, `using` ile garantili kapanan bir FileStream üzerinden
        // XLWorkbook'a veriyoruz — ClosedXML içeride ne yaparsa yapsın, akışın OS seviyesindeki
        // handle'ı bu using bloğu çıkışında (başarı ya da istisna fark etmez) kesin olarak kapanır.
        try
        {
            using var stream = File.Open(excelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var workbook = new XLWorkbook(stream);
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
    private static List<CodeColumnCandidate> LoadCandidateCodeColumns(List<GridRow> rows) =>
        LoadCandidateCodeColumnsCore(rows).Candidates;

    /// <summary>Asıl tespit mantığı — <see cref="LoadCandidateCodeColumns(List{GridRow})"/> ve
    /// <see cref="ExtractLetterheadTokens"/> tarafından paylaşılır. HeaderRowNumber, çağıranın
    /// "bu satırdan ÖNCEKİ satırlar firma letterhead'i" diye yorumlayabileceği sınırdır (başlıksız
    /// tablolarda anchorRow.RowNumber - 1 olarak sentetik üretilir — bkz. aşağıdaki yorum).</summary>
    private static (List<CodeColumnCandidate> Candidates, int HeaderRowNumber) LoadCandidateCodeColumnsCore(List<GridRow> rows)
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
                int priority = (colName.Contains("barkod") || colName.Contains("barcode") || colName.Contains("yaş") || colName.Contains("yas") || colName.Contains("beden")) ? -1
                    : (colName.Contains("kod") || colName.Contains("code") || colName.Contains("sku") || colName.Contains("id")) ? 2
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

        // Aşağıdaki şekil-bazlı eleme (LooksLikeSizeOrAgeColumn) güvenlik ağının GERÇEKTEN
        // başlıksız bir tabloda (bu bayrak true) mı yoksa açık ama zayıf bir başlıkla (öncelik
        // 0/1) mı karşı karşıya olduğumuzu ayırt etmesi gerekiyor — bkz. aşağıdaki BuildCandidates
        // notu ve "MİNA MİNO" vakası.
        bool wasHeaderless = headerRow is null;

        if (headerRow is null)
        {
            // Hiçbir satırda "kod"/"fiyat" gibi başlık metni yok — bazı listeler doğrudan veriyle
            // başlıyor (gerçek vaka, 2026-08-07: sadece "MARKA FİYAT LİSTESİ" yazan dar bir
            // başlık/banner satırı, asıl veri sonraki satırdan başlıyor ve DAHA FAZLA sütun
            // kullanıyor). Eski "ilk kullanılan satır, en soldaki sütun kod" varsayımı bu durumda
            // banner satırının dar sütun aralığı yüzünden yanlış sütunu (örn. açıklama) kod
            // sanıyordu. Referans satır artık ilk birkaç satır arasında EN SIK görülen kullanılan-
            // sütun-sayısına sahip olanı (asıl veri düzeni) — banner gibi tek seferlik satırlar
            // elenir.
            var sample = rows.Take(11).ToList();
            var modeCount = sample
                .GroupBy(r => r.Cells.Count)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .Select(g => (int?)g.Key)
                .FirstOrDefault();
            var anchorRow = (modeCount.HasValue ? sample.FirstOrDefault(r => r.Cells.Count == modeCount) : null)
                ?? rows.FirstOrDefault();
            if (anchorRow is null) return ([], 0);

            // headerRow.RowNumber, "bu satırdan SONRAKİ satırlar veridir" sınırı olarak kullanılıyor
            // (aşağıdaki paylaşılan döngü) — anchorRow genelde GERÇEK bir veri satırı olduğu için
            // (banner satırı değil) kendisini dışarıda bırakmamak adına bir eksiği referans alınır.
            headerRow = anchorRow with { RowNumber = anchorRow.RowNumber - 1 };

            var usedCols = anchorRow.Cells.Keys.OrderBy(c => c).ToList();
            int? priceGuess = DetectPriceColumn(rows, headerRow.RowNumber, usedCols);
            priceCol = priceGuess ?? (usedCols.Count > 1 ? usedCols[^1] : usedCols[0] + 1);

            // Fiyat sütunu dışında kalan ve "açıklama" gibi görünmeyen (kısa, boşluksuz değerli)
            // her sütun bir kod ADAYI sayılır — hangisinin GERÇEK kod sütunu olduğuna, biraz
            // aşağıda zaten var olan paylaşılan mekanizma (Worker.cs'in OCR oylaması) karar verir;
            // burada sadece bariz metin/açıklama sütunları elenir (kullanıcı isteği, 2026-08-07:
            // "görsellerde okunanların bulunduğu sütun kod olsun").
            codeColumnHeaders = usedCols
                .Where(c => c != priceCol && LooksLikeCodeColumn(rows, headerRow.RowNumber, c))
                .Select(c => (c, $"[{c}]. sütun (başlıksız)", 0))
                .ToList();

            if (codeColumnHeaders.Count == 0)
            {
                // Son çare: hiçbir sütun makul bir kod adayı gibi görünmüyorsa (ör. tablo gerçekten
                // tek sütunluysa) eski davranışa dön — sessizce boş dönmek yerine.
                int codeCol = usedCols.Min();
                priceCol = codeCol + 1;
                codeColumnHeaders = [(codeCol, headerRow.Cell(codeCol), 0)];
            }
        }

        var result = BuildCandidates(applySizeOrAgeFilter: true);

        // GÜVENLİK AĞI (2026-08-10, gerçek vaka: "MİNA MİNO" / "2026 3 İP ALİSA FİYAT LİSTESİ"):
        // gerçekten başlıksız bir tabloda (marka adı + banner başlığı var ama "KOD" diye bir
        // sütun başlığı YOK) tek-tireli stil kodları ("26-400" gibi) ile gerçek beden/yaş
        // aralıkları AYNI şekle sahip (bkz. LooksLikeSizeOrAgeColumn). O fonksiyonun kurtarma
        // kuralı ("başlık açıkça KOD/SKU/ID diyorsa elenmez") headerPriority>=2 gerektiriyor —
        // ama başlıksız-tablo yolunda ÜRETİLEN adaylar hep priority=0'dır (sentetik, gerçek bir
        // başlık metni yok), bu yüzden kurtarma KURALI hiçbir zaman devreye giremez. Sonuç: TEK
        // aday (bu şekle sahip kod sütunu) sessizce elenir, LoadCandidateCodeColumns BOŞ liste
        // döner, Worker.cs "olası bir ürün kodu sütunu bulunamadı" deyip klasörü HER turda atlar
        // — islendi.txt hiç yazılmadığı için klasör sonsuza dek hiç işlenmemiş gibi kalır (gerçek
        // vakada worker'ın bu klasöre hiç dokunmamasının kök nedeni buydu). Başlıklı bir tabloda
        // aynı ambiguity için güvenlik-öncelikli davranış (zayıf/yok başlıkta ELE) hâlâ doğru
        // (bkz. LoadCandidateCodeColumns_TekTireliDegerZayifBasliklaEleniyor testi) — o yüzden bu
        // geri düşüş SADECE gerçekten başlıksız tabloda VE eleme tüm adayları sildiğinde çalışır.
        if (result.Count == 0 && wasHeaderless && codeColumnHeaders.Count > 0)
            result = BuildCandidates(applySizeOrAgeFilter: false);

        return (result, headerRow.RowNumber);

        List<CodeColumnCandidate> BuildCandidates(bool applySizeOrAgeFilter)
        {
            var candidates = new List<CodeColumnCandidate>();
            foreach (var (col, name, priority) in codeColumnHeaders)
            {
                if (applySizeOrAgeFilter && LooksLikeSizeOrAgeColumn(rows, headerRow.RowNumber, col, priority)) continue;

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
                    candidates.Add(new CodeColumnCandidate(name, col, priority, prices, skippedRows, descriptions));
            }

            return candidates;
        }
    }

    /// <summary>Başlıksız tabloda fiyat sütununu değer BİÇİMİNE bakarak tahmin eder (kullanıcı
    /// isteği, 2026-08-07): virgül/nokta ondalık ayırıcılı değerler ("204,25", "218.50") en güçlü
    /// sinyaldir — ürün kodları neredeyse hiç ondalık içermez, fiyatlar sıkça içerir. Hiçbir aday
    /// sütun ondalık içermiyorsa (ör. tüm fiyatlar tam sayı, "325" gibi — testFOt1 vakası), en
    /// SAĞDAKİ çoğunlukla-sayısal VE en az bir sıfırdan farklı değer içeren sütun fiyat kabul edilir
    /// (yaygın tablo kuralı: fiyat genelde en sonda). Hiçbir sütun çoğunlukla sayısal değilse null
    /// döner (çağıran taraf kendi son çare mantığına düşer).
    ///
    /// SIFIR-SÜTUN GÜVENLİK AĞI (2026-08-10, üretim vakası PODİUMİNİ "Price tl.xlsx"): İngilizce bir
    /// sipariş-formu şablonunda "Unit Price" (gerçek birim fiyat, E sütunu) hemen solunda, "Price"
    /// (F sütunu, ="Qty × Unit Price" formülü) sağında duruyordu; Qty hiç doldurulmadığı için F
    /// her satırda 0'dı. Hiçbir sütun ondalık içermediğinden (tüm fiyatlar tam sayı) eski kod
    /// doğrudan "en sağdaki sayısal sütun" olan F'yi seçiyordu — ColumnHasAnyNonZeroValue kontrolü
    /// (aşağıda) o zamana kadar SADECE başlıklı yol için vardı (bkz. LoadCandidateCodeColumns'taki
    /// "Tutarı" notu), bu başlıksız-tahmin yoluna hiç uygulanmıyordu. Sonuç: 27 gerçek ürün fotoğrafı
    /// müşteriye $0,00 damgalanıp gönderildi. Düzeltme: "en sağdaki" seçimi artık sadece gerçekten
    /// sıfırdan farklı veri taşıyan sütunlar arasından yapılıyor; tamamı sıfır olan bir formül
    /// sütunu (başlığı ne kadar "fiyat gibi" görünürse görünsün) hiçbir zaman tercih edilmez.</summary>
    private static int? DetectPriceColumn(List<GridRow> rows, int headerRowNumber, IReadOnlyList<int> candidateCols, int sampleSize = 20)
    {
        int? bestDecimalCol = null;
        int bestDecimalCount = 0;
        var numericCols = new List<int>();

        foreach (var col in candidateCols)
        {
            int sampled = 0, numeric = 0, decimalLike = 0;
            foreach (var row in rows.Where(r => r.RowNumber > headerRowNumber))
            {
                var val = row.Cell(col).Trim();
                if (string.IsNullOrEmpty(val)) continue;

                sampled++;
                if (TryParsePrice(val, out _)) numeric++;
                if (Regex.IsMatch(val, @"\d[.,]\d")) decimalLike++;
                if (sampled >= sampleSize) break;
            }
            if (sampled == 0 || numeric * 2 < sampled) continue; // çoğunluk sayısal değilse aday değil

            numericCols.Add(col);
            if (decimalLike > bestDecimalCount) { bestDecimalCount = decimalLike; bestDecimalCol = col; }
        }

        if (bestDecimalCol is not null) return bestDecimalCol;

        var nonZeroNumericCols = numericCols.Where(c => ColumnHasAnyNonZeroValue(rows, headerRowNumber, c)).ToList();
        if (nonZeroNumericCols.Count > 0) return nonZeroNumericCols.Max();

        // Hiçbir aday sıfırdan farklı veri taşımıyorsa (ör. gerçekten tamamı boş bir taslak liste)
        // eski davranışa dön — en azından şekil bazlı bir tahminde bulunmak, null dönüp tabloyu
        // tamamen reddetmekten daha iyi; ColumnHasAnyNonZeroValue zaten sadece BU tahmini iyileştirir,
        // nihai $0.00 koruması yine de kod tarafında satır satır uygulanıyor.
        return numericCols.Count > 0 ? numericCols.Max() : null;
    }

    /// <summary>Başlıksız kod adayı filtresi: değerleri çoğunlukla KISA (&lt;=20 karakter) ve
    /// BOŞLUKSUZ olan sütunlar (ör. "3263", "AB-102") kod adayı sayılır; "ERKEK 3 LÜ TAKIM..." gibi
    /// boşluklu serbest metin sütunlar (açıklama) burada elenir. Kesin bir karar DEĞİLDİR —
    /// Worker.cs'teki OCR oylaması asıl hakemdir (bkz. LoadCandidateCodeColumns çağıranı); bu
    /// sadece bariz metin sütunlarını adaylıktan düşürüp oylamaya gereksiz gürültü taşımayı önler.</summary>
    private static bool LooksLikeCodeColumn(List<GridRow> rows, int headerRowNumber, int col, int sampleSize = 15)
    {
        int total = 0, plausible = 0;
        foreach (var row in rows.Where(r => r.RowNumber > headerRowNumber))
        {
            var val = row.Cell(col).Trim();
            if (string.IsNullOrEmpty(val)) continue;

            total++;
            if (val.Length <= 20 && !val.Contains(' ')) plausible++;
            if (total >= sampleSize) break;
        }

        return total > 0 && plausible * 2 >= total;
    }

    /// <summary>Bir sütunun değerleri çoğunlukla tire ile ayrılmış rakam listesiyse (beden/yaş/ay
    /// aralığı) bu bir kod sütunu OLAMAZ, başlığı yanıltıcı biçimde "kod" içerse bile (kenar durumu,
    /// ama ucuz bir güvenlik önlemi) — YETER Kİ şekil KESİN olsun. İki alt-durum ayrılır:
    /// (1) ÇOK-sayılı listeler (3+ sayı / 2+ tire, "134-140-146-152" gibi) hiçbir gerçek ürün
    /// kodunda görülmez — başlık ne derse desin HER ZAMAN elenir.
    /// (2) TEK-tireli iki-sayı çiftleri ("98-104" gibi) BELİRSİZDİR: hem gerçek bir beden aralığı
    /// (cm) hem de "26-158"/"27-958" gibi gerçek bir stil-numarası ürün kodu (üretim vakası, DECO
    /// KİDS 2026-08-07) AYNI şekle sahip olabilir — şekilden ayırt edilemez. Bu durumda başlık
    /// hakemdir: açık "kod"/"sku"/"id" başlığı (<paramref name="headerPriority"/> == 2, en
    /// güvenilir sinyal) varsa sütun elenmez; zayıf/belirsiz başlıkta (0/1) eski güvenlik-öncelikli
    /// davranış korunur (elenir). İlk birkaç dolu hücre örneklenir, tamamını taramaya gerek yok.</summary>
    private static readonly Regex MultiNumberRangePattern = new(@"^\d{1,4}(-\d{1,4}){2,}$", RegexOptions.Compiled);

    private static bool LooksLikeSizeOrAgeColumn(List<GridRow> rows, int headerRowNumber, int col, int headerPriority, int sampleSize = 15)
    {
        int total = 0, rangeLike = 0, multiNumber = 0;
        foreach (var row in rows.Where(r => r.RowNumber > headerRowNumber))
        {
            var val = row.Cell(col).Trim();
            if (string.IsNullOrEmpty(val)) continue;

            total++;
            if (SizeOrAgeRangeToken.IsMatch(val))
            {
                rangeLike++;
                if (MultiNumberRangePattern.IsMatch(val)) multiNumber++;
            }
            if (total >= sampleSize) break;
        }

        if (total == 0 || rangeLike * 2 < total) return false;

        // Çoğunluk KESİN (3+ sayılı) bir liste değilse — yani belirsiz tek-tireli çiftlerden
        // oluşuyorsa — ve başlık açıkça kod/sku/id diyorsa, başlık kazanır.
        if (multiNumber * 2 < rangeLike && headerPriority >= 2) return false;

        return true;
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
