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

    /// <summary>Excel dosya adının kendisini <see cref="BrandMatcher.MatchFromOcrTokens"/>'a
    /// verilebilecek bir token sözlüğüne çevirir. Gerçek vaka (2026-08-21, JOJOMİNİ): gönderen,
    /// fiyat listesini genelde markanın adını içeren bir dosya adıyla gönderiyor ("Jojomini
    /// fiyat.xlsx") — bot bunun başına kendi zaman damgası/hash önekini ekliyor
    /// ("20260821_110620295_0eae04_Jojomini fiyat.xlsx") ama marka adı metin olarak duruyor.
    /// Bu vakada ürün görsellerindeki logo ("jojomini") tek kelime, aşırı dekoratif kıvrık bir
    /// fontla basılıydı ve hiçbir OCR/görü sağlayıcısı okuyamadı; oysa dosya adı aynı kelimeyi
    /// OCR belirsizliği olmadan, düz metin olarak zaten taşıyordu.
    ///
    /// Letterhead taramasıyla AYNI güvenlik ağını kullanır (BrandMatcher'ın kelime-bazlı-tam-
    /// eşleşme + jenerik-kelime filtresi + çelişkili-NetCarpan koruması) — dosya adı da tek bir
    /// "token" olarak sabit yüksek güvenle (100 — insan/bot yazdığı düz metin, OCR gürültüsü yok)
    /// eklenir; BrandMatcher zaten kendi içinde kelimelere ayırıp aynı kuralları uygular. Bot'un
    /// eklediği sayısal/hex zaman damgası önekleri (örn. "20260821", "0eae04") harf içermediği
    /// veya markaların hiçbirinin adına denk gelmediği için zararsız gürültüdür. Bulunamazsa
    /// davranış hiç değişmez, akış letterhead/Gemini/WhatsApp sorusuna düşer.</summary>
    public static Dictionary<string, float> ExtractFileNameTokens(string excelName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(excelName);
        return string.IsNullOrWhiteSpace(nameWithoutExtension)
            ? new Dictionary<string, float>(StringComparer.Ordinal)
            : new Dictionary<string, float>(StringComparer.Ordinal) { [nameWithoutExtension] = 100f };
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
                //
                // SADECE-PARA-BİRİMİ başlığı da (2026-08-19, gerçek vaka: NGZ "2026 MİNİTİX 3 İP
                // TL FİYAT LİSTESİ" — aynı liste 17/18/19 Ağustos'ta 3 kez başarısız oldu): bazı
                // listelerde fiyat sütununun başlığı "fiyat"/"tutar" kelimesini hiç içermiyor,
                // sadece para birimi kısaltması ("TL") yazıyor. Bu durumda YUKARIDAKİ kontrol hiç
                // eşleşmiyor, satır header sayılmıyor, tablo "başlıksız" sanılıyor — başlıksız
                // yoldaki değer-şekli sezgisi ("kısa ve boşluksuz mu") ise SIRA NO gibi alakasız
                // sütunları da kod adayı sayabiliyor (bkz. LooksLikeSequentialRowIndexColumn'daki
                // vaka anlatımı). "tl"/"try"/"₺"/"$" başlık hücresinin TAMAMI (Contains değil, tam
                // eşitlik) olduğunda güvenle fiyat sinyali sayılır — Contains kullanılsaydı "atlıyor"
                // gibi "tl" alt-dizesi geçen alakasız kelimeler yanlışlıkla eşleşebilirdi.
                int pricePriority = (colName.Contains("fiyat") || colName.Contains("price")) ? 2
                    : (colName.Contains("tutar") || CurrencyOnlyHeaderPattern.IsMatch(colName.Trim())) ? 1
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

            // GÜVENLİK AĞI (2026-08-19, gerçek vaka: NGZ "2026-KADİFE.xlsx" / BABYİM,
            // Gonderim_20260817_161752_6b1484cc): kod adayı ile fiyat adayı AYNI sütunda
            // olamaz — aksi halde her ürünün "fiyatı" kendi kodu olur. Bu dosyada, asıl başlık
            // satırından (B=Ürün Numarası, E=GÜNCEL FİYAT) ÖNCE gelen birleşik bir banner
            // hücresi (" Kadife Ürün Fiyat Listesi", tek dolu hücre) hem "ürün" (kod adayı,
            // öncelik 1) hem "fiyat" (fiyat adayı, öncelik 2) kelimesini İÇERİYORDU — üstteki
            // döngü bu satırı (henüz asıl başlığa ulaşmadan) geçerli bir başlık sanıp
            // priceCol = codeCol = B seçti; sonuç: 14 görselin TAMAMINA gerçek TL fiyatı
            // yerine ürünün kendi kodu basıldı (ör. kod 2456, gerçek fiyat 296 TL iken "fiyat"
            // 2456 sanıldı — $60,23 basıldı, olması gereken ~$7,26), bu yanlış fiyatlı
            // görseller gerçek müşteriye WhatsApp'tan gönderildi. Başlıksız-tablo dalında
            // (aşağıda, "usedCols.Where(c => c != priceCol...)") zaten var olan aynı ayrım
            // burada da uygulanıyor: hPrice'a denk gelen sütun kod adaylarından çıkarılır;
            // geriye kod adayı kalmazsa (bu banner vakasında olduğu gibi, tek dolu hücre hem
            // kod hem fiyat kelimesini taşıyordu) bu satır header sayılmaz, tarama bir sonraki
            // satıra (gerçek başlığa) devam eder.
            var codeCandidatesExcludingPriceCol = rowCandidates.Where(c => c.Col != hPrice).ToList();

            if (codeCandidatesExcludingPriceCol.Count > 0 && hPrice != -1)
            {
                headerRow = row;
                priceCol = hPrice;
                codeColumnHeaders = codeCandidatesExcludingPriceCol;
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
            // sanıyordu. Referans satır artık TÜM tabloda EN SIK görülen kullanılan-sütun-sayısına
            // sahip olanı (asıl veri düzeni) — banner gibi tek seferlik satırlar elenir.
            //
            // ÖRNEKLEM TÜM SATIRLAR OLMALI, İLK BİRKAÇI DEĞİL (2026-08-21 düzeltmesi, gerçek vaka:
            // NGZ/ALİSA "ALİSA 2026.xls" — aynı müşteri 5 gün içinde aynı dosyayı 6 kez gönderdi,
            // hiçbiri hiç işlenmedi). Bu dosyada başlık satırı ("No"/"Stok Adı") 2 dolu hücreye
            // sahipti ve İLK 11 satır arasında (eski `rows.Take(11)`) TESADÜFEN fiyatı boş bırakılmış
            // ilk birkaç ürün satırıyla (onlar da kod+açıklama = 2 dolu hücre, fiyat sütunu o
            // satırlarda boştu) aynı "moda" düşüyordu — küçük örneklemde 2-hücreli satırlar 3-hücreli
            // (kod+açıklama+fiyat, dosyanın asıl/baskın satır şekli, 35 satırın 29'u) satırlardan
            // daha SIK görünüyordu, ilk eşleşen de (yanlışlıkla) başlık satırının KENDİSİ oluyordu —
            // sonuç: kod/fiyat sütunları hiç bulunamadı, `LoadCandidateCodeColumns` sürekli boş
            // döndü, klasör HER turda "olası bir ürün kodu sütunu bulunamadı" ile atlandı.
            // TÜM satırlar üzerinden hesaplanan moda, dosyanın gerçek baskın satır şeklini (3
            // hücre) doğru buluyor; ilk birkaç satırdaki tesadüfi örneklem sapmasına karşı bağışık.
            // Banner/tek-seferlik satır senaryosunda (yukarıdaki 2026-08-07 vakası) bu değişiklik
            // sonucu değiştirmiyor — banner zaten TEK satır, örneklem büyüdükçe payı küçülüyor,
            // moda hesabını etkilemesi daha da zorlaşıyor.
            var sample = rows;
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
                // Bilinçli olarak applySizeOrAgeFilter'a bağlı DEĞİL (yukarıdaki gibi koşullu
                // değil): gerçek bir SIRA NO sütunu her iki geçişte de (normal + kurtarma) aynı
                // şekilde kesin bir dizi olacağı için bu kontrolün iki geçişte de aktif kalması
                // güvenlidir — kurtarma geçişinin amacı yanlışlıkla elenen TİRELİ kod sütunlarını
                // geri getirmek, SIRA NO'yu değil.
                if (LooksLikeSequentialRowIndexColumn(rows, headerRow.RowNumber, col, priority)) continue;

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

                AddSpacedSuffixAliases(prices, descriptions);
                AddHyphenStrippedAliases(prices, descriptions);

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
    /// <summary>Başlık hücresinin TAMAMININ (trim sonrası) sadece bir para birimi kısaltması
    /// olduğunu tespit eder — "fiyat"/"price"/"tutar" içermeyen ama yine de fiyat sütunu olan
    /// başlıklar için (bkz. yukarıdaki "SADECE-PARA-BİRİMİ başlığı" notu). Bilinçli olarak
    /// TAM EŞİTLİK (^...$) kullanılır, Contains değil — "tl" bir alt-dize kontrolüyle arandığında
    /// "atlıyor", "satıldı" gibi tamamen alakasız kelimeler yanlışlıkla eşleşirdi.</summary>
    private static readonly Regex CurrencyOnlyHeaderPattern = new(@"^(tl|try|usd|₺|\$)$", RegexOptions.Compiled);

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

    /// <summary>Bir sütunun değerleri satır sırasına göre KESİNTİSİZ +1 artan bir tamsayı
    /// dizisiyse (1,2,3,4,... ya da 0,1,2,3,...) bu KESİNLİKLE bir ürün kodu DEĞİL, bir SIRA
    /// NUMARASI sütunudur — gerçek vaka (2026-08-19, NGZ "2026 MİNİTİX 3 İP TL FİYAT LİSTESİ",
    /// aynı müşteri aynı listeyi 17/18/19 Ağustos'ta 3 kez gönderdi, üçünde de aynı hata):
    /// bu listenin fiyat başlığı sadece "TL" idi (eski kod bunu tanımıyordu — bkz. yukarıdaki
    /// "SADECE-PARA-BİRİMİ başlığı" notu), başlık satırı hiç bulunamadı, tablo "başlıksız"
    /// sayıldı. Başlıksız yoldaki LooksLikeCodeColumn SADECE "kısa ve boşluksuz mu" bakıyor —
    /// "SIRA NO" sütununu (değerleri 1..32) da bir kod adayı sandı. Sonuç: bu sütunun değerleri
    /// gerçek MODELKODU sütunuyla BİRLEŞTİRİLİP excelCodesUnion'a (Worker.cs) girdi; OCR'ın
    /// yanlışlıkla okuduğu küçük bir sayı ("11", "21" gibi) SIRA NO'nun o satırındaki GERÇEK bir
    /// fiyatla eşleşip müşteriye BAŞKA bir ürünün fiyatı %32-100 "güven" ile damgalanıp
    /// gönderildi (kod "11" → SIRA NO=11'in satırındaki 326,50 TL; kod "21" → SIRA NO=21'in
    /// 322,00 TL'si — üç ayrı gönderimde toplam 4 kez tekrarlandı, biri Claude görü tespitiyle
    /// bile "%100 güven"le). Şekil çok KESİN olduğu için (gerçek bir ürün kodu ailesinin
    /// TESADÜFEN satır sırasına birebir uyan kesintisiz bir dizi oluşturması neredeyse imkânsız)
    /// bu güvenlik ağı ucuzdur. LooksLikeSizeOrAgeColumn ile AYNI kalıp izlenir: açık "kod"/
    /// "sku"/"id" başlığı (<paramref name="headerPriority"/> == 2) varsa sütun elenmez (bazı
    /// listeler gerçekten ürünlerini 1,2,3... diye kodlamış olabilir, açık başlık kazanır);
    /// başlıksız yolun ürettiği adaylar HER ZAMAN priority=0 olduğu için oradaki SIRA NO gibi
    /// sütunlar bu kontrolden hiçbir zaman muaf tutulmaz.</summary>
    private static bool LooksLikeSequentialRowIndexColumn(List<GridRow> rows, int headerRowNumber, int col, int headerPriority, int sampleSize = 15)
    {
        if (headerPriority >= 2) return false;

        int? prev = null;
        int? first = null;
        int count = 0;
        foreach (var row in rows.Where(r => r.RowNumber > headerRowNumber))
        {
            var val = row.Cell(col).Trim();
            if (string.IsNullOrEmpty(val)) continue;

            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return false;
            first ??= n;
            if (prev.HasValue && n != prev.Value + 1) return false;
            prev = n;

            count++;
            if (count >= sampleSize) break;
        }

        return count >= 3 && first is 0 or 1;
    }

    /// <summary>Marka-önekli/sonekli kod desteği (2026-08-13, gerçek vaka: NGZ "NET MEVSİMLİK
    /// FİYAT LİSTESİ 2026.xls" / MİNİCE): bazı Excel listelerinde kod sütunu her satırda marka
    /// adını sayısal kodun ÖNÜNE metin olarak ekliyor ("MİNİCE 6482", "MİNİCE 6410" gibi) — ama
    /// fiziksel üründeki etikette SADECE sayı basılı, marka adı ayrı bir yerde (logo/nakış, marka
    /// OCR'ı ondan kanıt buluyor). OCR bu yüzden doğru sayıyı ("6482") okuyor ama <see
    /// cref="MatchExact"/> (Excel'e karşı TAM string eşitliği) hiçbir zaman eşleşmiyor — "6482" !=
    /// "MİNİCE 6482". Gerçek vakada 38 görselden sadece 6'sı (hepsi Gemini görü tespiti
    /// fallback'iyle) damgalanabildi, kalan 32'si OCR'ın doğru okuduğu sayıya rağmen "eşleşen kod
    /// bulunamadı" ile atlandı.
    ///
    /// Çözüm: her kod hücresinin İLK ve SON boşlukla ayrılmış parçası ayrı ayrı denenir — hangisi
    /// 3-7 haneli SAF rakamsa (harf/tire İÇERMEYEN — "64A2", "V-029" gibi soneklere alias
    /// eklenmez, sadece tam-string eşleşmesi geçerli kalır, yanlış eşleştirme riski yok) o parça
    /// ikinci bir anahtar (alias) olarak aynı fiyata eklenir. Bu, hem "METİN SAYI" (marka önde,
    /// gerçek vaka) hem simetrik olarak "SAYI METİN" (sayı önde, ör. "6482 MİNİCE") biçimini
    /// kapsar — hangi taraf marka hangi taraf kod olduğu ÖNEKİN içeriğinden değil, şeklinden
    /// (saf rakam mı) çıkarılıyor, bu yüzden markanın önek mi sonek mi olduğu koda özgü olabilir,
    /// tüm satırlarda AYNI konumda/AYNI metin olması ŞART DEĞİL. Mevcut hiçbir anahtar
    /// SİLİNMEZ/ÜZERİNE YAZILMAZ, bu yüzden geriye dönük regresyon riski yok (sadece yeni eşleşme
    /// imkânı eklenir). Güvenlik ağı: iki farklı tam-kod AYNI sayısal alias'a indirgenirse (gerçek
    /// bir çakışma potansiyeli, örn. iki farklı marka/model aynı "123" ile bitiyorsa) VEYA alias
    /// zaten Excel'de kendi başına ayrı bir gerçek kod olarak varsa, o alias hiç eklenmez — sadece
    /// tam-string eşleşmesi geçerli kalır (Barkod/Tutarı güvenlik ağlarıyla aynı "belirsizlikte
    /// ele" ruhu).</summary>
    private static void AddSpacedSuffixAliases(Dictionary<string, decimal> prices, Dictionary<string, string> descriptions)
    {
        var aliasSourceCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RegisterAlias(string alias, string sourceCode)
        {
            if (prices.ContainsKey(alias)) { conflicts.Add(alias); return; }
            if (aliasSourceCode.TryGetValue(alias, out var existingCode))
            {
                if (prices[existingCode] != prices[sourceCode]) conflicts.Add(alias);
                return;
            }
            aliasSourceCode[alias] = sourceCode;
        }

        foreach (var code in prices.Keys.ToList())
        {
            int firstSpace = code.IndexOf(' ');
            int lastSpace = code.LastIndexOf(' ');
            if (firstSpace <= 0) continue; // hiç boşluk yok — alias'a gerek yok, tek token zaten

            var trailing = code[(lastSpace + 1)..];
            if (IsPlausibleNumericAlias(trailing)) RegisterAlias(trailing, code);

            var leading = code[..firstSpace];
            if (leading != trailing && IsPlausibleNumericAlias(leading)) RegisterAlias(leading, code);
        }

        foreach (var (alias, sourceCode) in aliasSourceCode)
        {
            if (conflicts.Contains(alias)) continue;
            prices[alias] = prices[sourceCode];
            if (descriptions.TryGetValue(sourceCode, out var desc)) descriptions[alias] = desc;
        }
    }

    private static bool IsPlausibleNumericAlias(string token) =>
        token.Length is >= 3 and <= 7 && token.All(char.IsDigit);

    /// <summary>Tireli stil-numarası kod desteği (2026-08-19, gerçek vaka: NGZ "2026 MİNİTİX 3
    /// İP TL FİYAT LİSTESİ" — Gonderim_20260817_182716_1ebcfab2 ve aynı listenin 18/19
    /// Ağustos'taki iki tekrar gönderimi): bazı Excel listelerinde kod hücresi tek-tireli stil
    /// numarası formatında ("26-274", "26-206" gibi), ama fiziksel etikette OCR bunu TİRESİZ,
    /// tek bir bitişik sayı olarak okuyor ("26274") — <see cref="MatchExact"/> (tam string
    /// eşitliği) hiçbir zaman eşleşmiyor. Gerçek vakada 16 görselin 12-13'ü bu yüzden "eşleşen
    /// kod bulunamadı" ile atlandı; OCR'ın okuduğu adaylar ("26274", "26269", "26230"...) tire
    /// kaldırılınca Excel'deki MODELKODU sütunuyla BİREBİR örtüşüyordu.
    ///
    /// AddSpacedSuffixAliases'tan FARKI: o fonksiyon boşlukla ayrılan iki AYRI token'dan birini
    /// (ör. "MİNİCE 6482" -> "6482") alias yapıyor; burada OCR kodu TEK bir bitişik sayı olarak
    /// okuduğu için tireyi SİLİP iki tarafı BİRLEŞTİRMEK gerekiyor ("26-274" -> "26274"). Harf
    /// içeren tireli sonekler ("V-029" gibi) güvenlidir — birleşik hâl harf içerdiği için
    /// IsPlausibleNumericAlias'ın "tüm rakam" testinden geçemez, alias hiç üretilmez, sadece
    /// tam-string eşleşmesi geçerli kalır. Mevcut hiçbir anahtar silinmez/üzerine yazılmaz,
    /// sadece yeni eşleşme imkânı eklenir; çakışma güvenlik ağı AddSpacedSuffixAliases ile
    /// birebir aynıdır (RegisterAlias'ın kopyası — iki farklı kod aynı birleşik alias'a inerse
    /// ya da alias zaten ayrı bir gerçek kodsa hiç eklenmez).</summary>
    private static void AddHyphenStrippedAliases(Dictionary<string, decimal> prices, Dictionary<string, string> descriptions)
    {
        var aliasSourceCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RegisterAlias(string alias, string sourceCode)
        {
            if (prices.ContainsKey(alias)) { conflicts.Add(alias); return; }
            if (aliasSourceCode.TryGetValue(alias, out var existingCode))
            {
                if (prices[existingCode] != prices[sourceCode]) conflicts.Add(alias);
                return;
            }
            aliasSourceCode[alias] = sourceCode;
        }

        foreach (var code in prices.Keys.ToList())
        {
            if (!code.Contains('-')) continue;

            var stripped = code.Replace("-", "");
            if (stripped == code || !IsPlausibleNumericAlias(stripped)) continue;

            RegisterAlias(stripped, code);
        }

        foreach (var (alias, sourceCode) in aliasSourceCode)
        {
            if (conflicts.Contains(alias)) continue;
            prices[alias] = prices[sourceCode];
            if (descriptions.TryGetValue(sourceCode, out var desc)) descriptions[alias] = desc;
        }
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
