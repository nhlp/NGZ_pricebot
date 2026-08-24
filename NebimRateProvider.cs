using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace PriceBotPipeline;

public sealed class NebimRateProvider
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public NebimRateProvider(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<(decimal Rate, DateTime RateDate)?> GetUsdRateAsync(DateTime forDate)
    {
        // AllExchangeRates (ExchangeTypeCode=1) verilen tarihte veya öncesindeki en güncel kuru
        // verir: view'de hafta sonu satırları da dolu görünüyor ama garanti değil; kur girilmemiş
        // günlerde otomatik olarak en son girilen kura düşülür (rapora kurun tarihi ayrıca yazılır).
        // 2026-08-21: ExchangeTypeCode tip 6'dan tip 2'ye değiştirilmişti (kullanıcı isteği); AYNI
        // GÜN, JOJO MİNİ kod 25707 vakasında (Nebim Stok Detay ekranı B.F=390,15 TL/$8,14 gösterirken
        // PriceBot 408 TL Excel fiyatı x NetCarpan ile $8,28 üretti) kullanıcı gerçek kuru Nebim'den
        // teyit edip tip 1'in (47,99) doğru kaynak olduğunu belirtti; tip 2'den tip 1'e geçildi. NOT:
        // bu tek başına o vakadaki tüm farkı kapatmaz — asıl büyük fark Excel liste fiyatı (408 TL) ile
        // Nebim'in kendi stok kartı Brüt Fiyatı (390,15 TL) arasındaki ayrı bir veri farkından geliyor,
        // bkz. CLAUDE.md "JOJO MİNİ kod 25707" notu.
        const string sql = @"
            SELECT TOP 1 Rate, [Date]
            FROM dbo.AllExchangeRates
            WHERE CurrencyCode = 'USD'
              AND ExchangeTypeCode = '1'
              AND [Date] <= CAST(@forDate AS DATE)
            ORDER BY [Date] DESC";

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@forDate", forDate.Date);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync())
            {
                _logger.LogWarning("Nebim: {ForDate:yyyy-MM-dd} veya öncesine ait USD kuru bulunamadı (AllExchangeRates, tip 2).", forDate);
                return null;
            }

            // Rate kolonu SQL tarafında float; doğrudan GetDecimal ile okunamıyor.
            decimal rate = Convert.ToDecimal(rd.GetValue(0));
            DateTime rateDate = rd.GetDateTime(1);
            return (rate, rateDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nebim: USD kuru sorgusu başarısız (AllExchangeRates, tip 2).");
            throw;
        }
    }
}
