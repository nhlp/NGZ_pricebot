using Microsoft.Data.SqlClient;

namespace PriceBotPipeline;

public sealed class NebimRateProvider
{
    private readonly string _connectionString;
    public NebimRateProvider(string connectionString) => _connectionString = connectionString;

    public async Task<(decimal Rate, DateTime RateDate)?> GetUsdRateAsync(DateTime forDate)
    {
        // Verilen tarihte veya öncesindeki en güncel kur alınır: hafta sonu/tatil gibi
        // kur girilmemiş günlerde otomatik olarak en son girilen kura düşülür.
        const string sql = @"
            SELECT TOP 1 l.ExchangeRate, h.ExchangeDate
            FROM dbo.trExchangeRateLine l
            INNER JOIN dbo.trExchangeRateHeader h ON l.ExchangeRateHeaderID = h.ExchangeRateHeaderID
            WHERE l.CurrencyCode = 'USD'
              AND CAST(h.ExchangeDate AS DATE) <= CAST(@forDate AS DATE)
              AND h.ExchangeTypeCode = 4
            ORDER BY h.ExchangeDate DESC, h.CreatedDate DESC";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[NebimRateProvider] USD Kuru aranıyor... {forDate:yyyy-MM-dd} veya öncesi | ExchangeTypeCode: 4");
        Console.ResetColor();

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@forDate", forDate.Date);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync())
            {
                // Kur bulunamadığında log basıyoruz
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[NebimRateProvider] UYARI: {forDate:yyyy-MM-dd} veya öncesine ait veritabanında USD kuru bulunamadı!");
                Console.ResetColor();
                return null;
            }

            // ExchangeRate kolonu SQL tarafında float; doğrudan GetDecimal ile okunamıyor.
            decimal rate = Convert.ToDecimal(rd.GetValue(0));
            DateTime rateDate = rd.GetDateTime(1);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[NebimRateProvider] BAŞARILI: Kur Bulundu! Tarih: {rateDate:yyyy-MM-dd} | Değer: {rate}");
            Console.ResetColor();

            return (rate, rateDate);
        }
        catch (Exception ex)
        {
            // SQL veya bağlantı hatalarını yakalamak için log
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[NebimRateProvider] HATA: Sorgu çalıştırılırken bir hata oluştu!");
            Console.WriteLine($"Hata Detayı: {ex.Message}");
            Console.ResetColor();
            throw;
        }
    }
}