using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PriceBotPipeline;
using Xunit;

namespace PriceBot.Worker.Tests;

/// <summary>2026-08-28, kullanıcı isteği, gerçek vaka: Gonderim_20260826_160228_1bd6a08b'de kod
/// "0050", İKİ farklı alakasız görsele (gerçek kodları 0077 ve 26200) yanlışlıkla fuzzy-eşleşmişti.
/// Bu testler LearnedCodeCorrectionStore'un (a) diskte kalıcı olduğunu, (b) aynı yanlış kodun
/// FARKLI görsellerde farklı doğru kodlara öğrenilebildiğini ve (c) "wrongCode == confirmedCode"
/// (gerçek bir hata olmayan) durumları öğrenmediğini doğrular.</summary>
public class LearnedCodeCorrectionStoreTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"lccs_test_{Guid.NewGuid():N}.json");

    [Fact]
    public void Learn_YeniDuzeltmeyi_KaliciHaleGetirir()
    {
        var path = NewTempPath();
        try
        {
            var store = new LearnedCodeCorrectionStore(path, NullLogger.Instance);
            Assert.False(store.IsKnownBad("COŞAY BEBE", "0050"));

            store.Learn("COŞAY BEBE", wrongCode: "0050", confirmedCode: "0077", folder: "Gonderim_test");

            Assert.True(store.IsKnownBad("COŞAY BEBE", "0050"));
            Assert.Equal("0077", store.TryGetLearnedReplacement("COŞAY BEBE", "0050"));
            Assert.True(File.Exists(path));

            // Diskten yeniden yüklendiğinde de kalıcı olmalı (yeni bir Worker turu/yeniden
            // başlatma senaryosu).
            var reloaded = new LearnedCodeCorrectionStore(path, NullLogger.Instance);
            Assert.Equal("0077", reloaded.TryGetLearnedReplacement("COŞAY BEBE", "0050"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AyniYanlisKod_FarkliGorsellerdeFarkliDogruKodaOgrenilebilir()
    {
        // Gerçek vaka: "0050" hem 0077'ye hem 26200'e yanlışlıkla bağlanmıştı — TryGetLearnedReplacement
        // EN SON öğrenileni döner (Worker.cs bunu SADECE görselin KENDİ OCR adaylarında da geçtiğini
        // doğruladıktan sonra uygular, bu yüzden ikisinin de kayıtlı olması güvenlidir).
        var path = NewTempPath();
        try
        {
            var store = new LearnedCodeCorrectionStore(path, NullLogger.Instance);
            store.Learn("COŞAY BEBE", "0050", "0077", "Gonderim_A");
            store.Learn("COŞAY BEBE", "0050", "26200", "Gonderim_B");

            Assert.Equal(2, store.Corrections.Count(c => c.WrongCode == "0050"));
            Assert.Equal("26200", store.TryGetLearnedReplacement("COŞAY BEBE", "0050")); // en son öğrenilen
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Learn_WrongCodeConfirmedCodeIleAyniysa_HicbirSeyOgrenmez()
    {
        var path = NewTempPath();
        try
        {
            var store = new LearnedCodeCorrectionStore(path, NullLogger.Instance);
            store.Learn("COŞAY BEBE", wrongCode: "24113", confirmedCode: "24113", folder: "Gonderim_test");

            Assert.Empty(store.Corrections);
            Assert.False(File.Exists(path)); // Hiç yazma denemesi bile yapılmamalı.
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Learn_AyniUcluTekrarEklenmez_Idempotent()
    {
        var path = NewTempPath();
        try
        {
            var store = new LearnedCodeCorrectionStore(path, NullLogger.Instance);
            store.Learn("COŞAY BEBE", "0050", "0077", "Gonderim_A");
            store.Learn("COŞAY BEBE", "0050", "0077", "Gonderim_C"); // aynı marka+yanlış+doğru üçlüsü

            Assert.Single(store.Corrections);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FarkliMarka_BirbirininDuzeltmesiniGormez()
    {
        var path = NewTempPath();
        try
        {
            var store = new LearnedCodeCorrectionStore(path, NullLogger.Instance);
            store.Learn("COŞAY BEBE", "0050", "0077", "Gonderim_A");

            Assert.False(store.IsKnownBad("BAŞKA MARKA", "0050"));
            Assert.Null(store.TryGetLearnedReplacement("BAŞKA MARKA", "0050"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BozukJsonDosyasi_BosListeyleDevamEder_Crashlamaz()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, "{ bozuk json ][", Encoding.UTF8);
            var store = new LearnedCodeCorrectionStore(path, NullLogger.Instance);

            Assert.Empty(store.Corrections);
            Assert.False(store.IsKnownBad("COŞAY BEBE", "0050"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
