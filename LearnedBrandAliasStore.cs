using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PriceBotPipeline;

/// <summary>Marka soru-cevap akışının "kendini eğitmesi" (2026-08-21, kullanıcı isteği):
/// müşteri marka sorusuna cevap yazdığında ya da Gemini görü modeli markayı bulduğunda,
/// o klasörde OCR/dosya-adının resmi Nebim adıyla eşleşmeyen ama artık DOĞRULANMIŞ delil
/// kelimeleri (BrandMatcher.ExtractAliasCandidates ile jenerik/çakışma güvenlik ağından
/// geçirilmiş) kalıcı hale getirilir. Sonraki klasörlerde (aynı üreticinin aynı dekoratif
/// logo fontu, aynı dosya-adı alışkanlığı — hatta BAŞKA bir müşteriden gelse bile) bu
/// alias'lar BrandMatcher.MatchFromLearnedAliases ile Gemini'ye/WhatsApp sorusuna düşmeden
/// ÖNCE denenir — ücretsiz ve yerel.
///
/// Depolama BİLİNÇLİ olarak yerel bir JSON dosyası (appsettings.json'ın yanında,
/// gonderim_bekleyen.json ile aynı desen) — Nebim ERP şemasına yeni bir tablo eklemek yerine.
/// Worker.ExecuteAsync'teki dış döngü klasörleri SIRAYLA (foreach, Parallel.For sadece bir
/// klasörün İÇİNDEKİ görsel taramasında kullanılır) işlediği için bu store'a eşzamanlı erişim
/// olmaz; yine de disk yazımı atomik yapılır (geçici dosyaya yazıp taşıma) ki yarım yazılmış
/// bir JSON servis çökmesi/yeniden başlatma anına denk gelirse dosya bozulmasın.</summary>
public sealed class LearnedBrandAliasStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private List<LearnedAlias> _aliases;

    public LearnedBrandAliasStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _aliases = Load(path, logger);
    }

    public IReadOnlyList<LearnedAlias> Aliases => _aliases;

    private static List<LearnedAlias> Load(string path, ILogger logger)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<LearnedAlias>>(json) ?? [];
        }
        catch (Exception ex)
        {
            // Bozuk/okunamayan dosya öğrenmeyi sıfırdan başlatır — worker'ı kesintiye
            // uğratmaz, sadece daha önce öğrenilmiş alias'lar bu turdan itibaren kaybolur
            // (yeni cevaplarla zamanla yeniden birikir).
            logger.LogWarning(ex, "Öğrenilmiş marka alias dosyası okunamadı ({Path}), boş listeyle devam ediliyor.", path);
            return [];
        }
    }

    /// <summary>Teyit edilmiş bir marka için delil token'larından aday alias çıkarır
    /// (BrandMatcher.ExtractAliasCandidates) ve henüz kayıtlı olmayanları kalıcı hale getirir.
    /// Idempotent: aynı kelime hangi markaya kayıtlıysa kayıtlı kalır, tekrar eklenmez.</summary>
    public void Learn(
        BrandMultiplier confirmedBrand,
        IReadOnlyDictionary<string, float> evidenceTokens,
        IReadOnlyList<BrandMultiplier> allBrands,
        string source,
        string folder)
    {
        var candidates = BrandMatcher.ExtractAliasCandidates(evidenceTokens, confirmedBrand, allBrands);
        if (candidates.Count == 0) return;

        var existingWords = _aliases.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
        var newWords = candidates.Where(w => !existingWords.Contains(w)).ToList();
        if (newWords.Count == 0) return;

        var learnedAt = DateTime.UtcNow;
        var updated = new List<LearnedAlias>(_aliases.Count + newWords.Count);
        updated.AddRange(_aliases);
        updated.AddRange(newWords.Select(w => new LearnedAlias(confirmedBrand.FullName, w, learnedAt, source, folder)));

        try
        {
            Save(_path, updated);
            _aliases = updated;
            _logger.LogInformation(
                "Klasör {Folder}: '{Brand}' markası için {Count} yeni alias öğrenildi ({Words}) — kaynak: {Source}.",
                folder, confirmedBrand.FullName, newWords.Count, string.Join(", ", newWords), source);
        }
        catch (Exception ex)
        {
            // Diske yazma başarısız olsa bile (ör. geçici dosya kilidi) bu turun marka tespiti
            // zaten tamamlanmış durumda — sadece öğrenme kaybolur, klasör işlemesi etkilenmez.
            _logger.LogWarning(ex, "Klasör {Folder}: öğrenilmiş alias'lar diske yazılamadı — bu turun marka tespiti etkilenmedi, sadece öğrenme kaydedilemedi.", folder);
        }
    }

    private static void Save(string path, List<LearnedAlias> aliases)
    {
        var json = JsonSerializer.Serialize(aliases, new JsonSerializerOptions { WriteIndented = true });
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json, Encoding.UTF8);
        File.Move(tmpPath, path, overwrite: true);
    }
}
