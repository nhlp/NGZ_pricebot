using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PriceBotPipeline;

/// <summary>Ürün kodu düzeltmelerinin "kendini eğitmesi" (2026-08-28, kullanıcı isteği, gerçek
/// vaka: Gonderim_20260826_160228_1bd6a08b — gorsel_..._6e6383.jpg, gerçek kodu 24113 olan bir
/// KitKat takımı, düşük güvenli (güven 44) OCR-fuzzy eşleşmeyle YANLIŞLIKLA kod 26206'ya
/// (tamamen alakasız bir yenidoğan seti) bağlanıp $16,56 ile müşteriye gönderilmişti; aynı partide
/// 10 fuzzy sonuçtan 6'sı yanlış çıktı). Artık HİÇBİR fuzzy sonuç (OCR'ın kendi Levenshtein-1
/// tahmini ya da AI görü-tespiti fallback'i fark etmez) müşteriye otomatik gönderilmiyor — bkz.
/// Worker.cs'teki KontrolBekliyor akışı. Operatör KontrolBekliyor klasöründeki bir görseli doğru
/// kodla yeniden adlandırdığında (bkz. Worker.ResolveKontrolBekliyorAsync), o düzeltme bu store'a
/// kalıcı hale getirilir — aynı marka için AYNI yanlış kod bir daha fuzzy/AI tahmini olarak
/// çıkarsa (ör. "0050" bu partide İKİ farklı alakasız görsele yanlışlıkla bağlanmıştı) Worker
/// artık onu körü körüne KABUL ETMEZ: (a) bu turdaki OCR adayları arasında daha önce doğrulanmış
/// düzeltme kodu da varsa doğrudan ona geçer (insan onayına gerek kalmadan), yoksa (b) en azından
/// KontrolBekliyor özetine "bu kod bu marka için daha önce de yanlış çıkmıştı" notu eklenir.
///
/// Depolama BİLİNÇLİ olarak yerel bir JSON dosyası (appsettings.json'ın yanında,
/// LearnedBrandAliasStore.cs ile AYNI desen) — Nebim ERP şemasına dokunulmaz. Worker.ExecuteAsync
/// klasörleri sırayla işlediği için bu store'a eşzamanlı erişim olmaz; disk yazımı yine de atomik
/// (geçici dosyaya yazıp taşıma).</summary>
public sealed class LearnedCodeCorrectionStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private List<LearnedCodeCorrection> _corrections;

    public LearnedCodeCorrectionStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _corrections = Load(path, logger);
    }

    public IReadOnlyList<LearnedCodeCorrection> Corrections => _corrections;

    private static List<LearnedCodeCorrection> Load(string path, ILogger logger)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<LearnedCodeCorrection>>(json) ?? [];
        }
        catch (Exception ex)
        {
            // Bozuk/okunamayan dosya öğrenmeyi sıfırdan başlatır — worker'ı kesintiye uğratmaz,
            // sadece daha önce öğrenilmiş düzeltmeler bu turdan itibaren kaybolur (operatör yeni
            // düzeltmeler yazdıkça zamanla yeniden birikir).
            logger.LogWarning(ex, "Öğrenilmiş kod düzeltmeleri dosyası okunamadı ({Path}), boş listeyle devam ediliyor.", path);
            return [];
        }
    }

    /// <summary>Bu (marka, yanlış kod) çifti daha önce en az bir kez operatör tarafından yanlış
    /// bulunup düzeltilmiş mi? KontrolBekliyor özetine uyarı eklemek için kullanılır — tek başına
    /// otomatik bir aksiyon TETİKLEMEZ (bkz. TryGetLearnedReplacement).</summary>
    public bool IsKnownBad(string brand, string wrongCode) =>
        _corrections.Any(c => string.Equals(c.Brand, brand, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(c.WrongCode, wrongCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>Bu (marka, yanlış kod) çifti için EN SON öğrenilen doğru kodu döner (varsa).
    /// Çağıran taraf (Worker.cs) bunu SADECE doğru kodun bu görselin KENDİ OCR adayları arasında
    /// da geçtiğini doğruladıktan sonra otomatik uygulamalı — aynı yanlış kod farklı görsellerde
    /// farklı gerçek kodlara denk gelebilir (ör. "0050" hem 0077'ye hem 26200'e yanlışlıkla
    /// bağlanmıştı), bu yüzden körü körüne "X hep Y'dir" varsayımı GÜVENLİ DEĞİL.</summary>
    public string? TryGetLearnedReplacement(string brand, string wrongCode) =>
        _corrections
            .Where(c => string.Equals(c.Brand, brand, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(c.WrongCode, wrongCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.LearnedAt)
            .Select(c => c.ConfirmedCode)
            .FirstOrDefault();

    /// <summary>Operatörün KontrolBekliyor'da yaptığı bir düzeltmeyi kalıcı hale getirir.
    /// Idempotent: aynı (marka, yanlış kod, doğru kod) üçlüsü zaten kayıtlıysa tekrar eklenmez.
    /// <paramref name="wrongCode"/> null ise (görsel hiç eşleşmemiş, "atlandı" durumundan
    /// KontrolBekliyor'a değil doğrudan operatör tarafından çözülmüşse) hiçbir şey öğrenilmez —
    /// öğrenme SADECE "sistem X dedi ama doğrusu Y'ymiş" biçimindeki gerçek hatalardan gelir.</summary>
    public void Learn(string brand, string? wrongCode, string confirmedCode, string folder)
    {
        if (string.IsNullOrWhiteSpace(wrongCode)) return;
        if (string.Equals(wrongCode, confirmedCode, StringComparison.OrdinalIgnoreCase)) return;

        var already = _corrections.Any(c =>
            string.Equals(c.Brand, brand, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.WrongCode, wrongCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.ConfirmedCode, confirmedCode, StringComparison.OrdinalIgnoreCase));
        if (already) return;

        var updated = new List<LearnedCodeCorrection>(_corrections.Count + 1);
        updated.AddRange(_corrections);
        updated.Add(new LearnedCodeCorrection(brand, wrongCode, confirmedCode, DateTime.UtcNow, folder));

        try
        {
            Save(_path, updated);
            _corrections = updated;
            _logger.LogInformation(
                "Klasör {Folder}: kod düzeltmesi öğrenildi — '{Brand}' markasında '{Wrong}' -> '{Confirmed}'.",
                folder, brand, wrongCode, confirmedCode);
        }
        catch (Exception ex)
        {
            // Diske yazma başarısız olsa bile bu turun düzeltmesi zaten uygulanmış durumda —
            // sadece gelecekteki turlar için öğrenme kaybolur, bu görselin kendi çözümü etkilenmez.
            _logger.LogWarning(ex, "Klasör {Folder}: öğrenilmiş kod düzeltmesi diske yazılamadı.", folder);
        }
    }

    private static void Save(string path, List<LearnedCodeCorrection> corrections)
    {
        var json = JsonSerializer.Serialize(corrections, new JsonSerializerOptions { WriteIndented = true });
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json, Encoding.UTF8);
        File.Move(tmpPath, path, overwrite: true);
    }
}

public sealed record LearnedCodeCorrection(
    string Brand, string WrongCode, string ConfirmedCode, DateTime LearnedAt, string Folder);
