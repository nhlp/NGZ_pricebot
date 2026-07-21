using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using PriceBotPipeline;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PriceBotWorker";
});

var logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
Directory.CreateDirectory(logsDir);

var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss}] {Level:u3} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logsDir, "pricebot-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 60,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

if (WindowsServiceHelpers.IsWindowsService())
{
    loggerConfig.WriteTo.EventLog("PriceBotWorker", manageEventSource: true, restrictedToMinimumLevel: LogEventLevel.Warning);
}

Log.Logger = loggerConfig.CreateLogger();

builder.Logging.ClearProviders();
builder.Services.AddSerilog();

builder.Services.AddHostedService<Worker>();

Mutex singleInstanceMutex;
bool isFirstInstance;
try
{
    singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Global\PriceBotWorker_SingleInstance", createdNew: out isFirstInstance);
}
catch (UnauthorizedAccessException)
{
    // Mutex başka bir güvenlik bağlamında (örn. SYSTEM olarak çalışan Windows Service) zaten var — o da bir örnek demek.
    Log.Fatal("PriceBot Worker zaten çalışıyor (mutex farklı bir oturumda/serviste sahiplenilmiş) — bu örnek başlatılmadan kapatılıyor.");
    Log.CloseAndFlush();
    return;
}

if (!isFirstInstance)
{
    Log.Fatal("PriceBot Worker zaten çalışıyor (başka bir örnek tespit edildi) — bu örnek başlatılmadan kapatılıyor.");
    Log.CloseAndFlush();
    return;
}

var host = builder.Build();

try
{
    Log.Information("PriceBot Worker host başlatılıyor (servis modu: {IsService})", WindowsServiceHelpers.IsWindowsService());
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "PriceBot Worker beklenmedik şekilde durdu");
    throw;
}
finally
{
    singleInstanceMutex.ReleaseMutex();
    Log.CloseAndFlush();
}
