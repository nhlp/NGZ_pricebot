using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace PriceBotPipeline;

public sealed class NebimBrandProvider
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public NebimBrandProvider(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<BrandMultiplier>> GetBrandMultipliersAsync()
    {
        // NetCarpan <= 0 satırlar veri hatasıdır (gerçek vaka: CASSİOPE BABY = -336.62) ve
        // fiyat hesabına asla girmemeli: burada elenir ve uyarı loglanır. Elenen marka OCR'da
        // da kullanıcı cevabında da "listede yok" muamelesi görür — DB'de düzeltilince
        // kendiliğinden akışa girer.
        const string sql = @"
            SELECT OnEk, OnEkAciklamasi, NetCarpan
            FROM dbo.AS_PWB_MarkaCarpan";

        var brands = new List<BrandMultiplier>();

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
                _logger.LogWarning("AS_PWB_MarkaCarpan: '{Brand}' markası NetCarpan={NetCarpan} (<= 0, veri hatası) olduğu için eşleştirme dışı bırakıldı — Nebim tarafında düzeltilmeli.",
                    fullName, netCarpan);
                continue;
            }

            brands.Add(new BrandMultiplier(onEk, fullName, netCarpan));
        }

        return brands;
    }
}
