using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace PriceBotPipeline;

/// <summary>AS_PWB_MarkaCarpan'dan yüklenen marka listesi. Excluded: NetCarpan &lt;= 0 olduğu
/// için fiyat hesabına giremeyen (veri hatası) markalar — sadece OCR bunlardan birini bir
/// klasörde fiilen yakalarsa bilgi amaçlı loglanır, her yükleme turunda değil.</summary>
public sealed record BrandLoadResult(List<BrandMultiplier> Brands, List<BrandMultiplier> Excluded);

public sealed class NebimBrandProvider
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public NebimBrandProvider(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<BrandLoadResult> GetBrandMultipliersAsync()
    {
        // NetCarpan <= 0 satırlar veri hatasıdır (gerçek vaka: CASSİOPE BABY = -336.62) ve
        // fiyat hesabına asla girmemeli: burada elenir. Her tur başına loglamak yerine (bu
        // liste ~300 satır ve her 10 sn'de bir yeniden çekiliyor, spam olurdu) elenenler
        // Excluded'a toplanır; çağıran taraf sadece bu markalardan biri OCR'da fiilen
        // yakalanırsa bilgi loglar (bkz. Worker.ResolveFolderBrandAsync).
        const string sql = @"
            SELECT OnEk, OnEkAciklamasi, NetCarpan
            FROM dbo.AS_PWB_MarkaCarpan";

        var brands = new List<BrandMultiplier>();
        var excluded = new List<BrandMultiplier>();

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand(sql, cn);
        await using var rd = await cmd.ExecuteReaderAsync();

        while (await rd.ReadAsync())
        {
            var onEk = rd.IsDBNull(0) ? "" : rd.GetString(0).Trim();
            var fullName = rd.IsDBNull(1) ? "" : rd.GetString(1).Trim();
            // NetCarpan kolon tipi decimal ama float'a dönme ihtimaline karşı toleranslı oku.
            var netCarpan = rd.IsDBNull(2) ? 0m : Convert.ToDecimal(rd.GetValue(2));

            if (fullName.Length == 0) continue;
            if (netCarpan <= 0)
            {
                excluded.Add(new BrandMultiplier(onEk, fullName, netCarpan));
                continue;
            }

            brands.Add(new BrandMultiplier(onEk, fullName, netCarpan));
        }

        return new BrandLoadResult(brands, excluded);
    }
}
