using FLIS.DataCollector.Models;
using FLIS.DataCollector.Services;
using Serilog;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("===========================================");
    Log.Information("  FLIS - Flash Loan Intelligence Service  ");
    Log.Information("  Data Collection Pipeline v1.0.0          ");
    Log.Information("===========================================");

    var builder = Host.CreateApplicationBuilder(args);

    // Use Serilog
    builder.Services.AddSerilog();

    // Bind configuration sections
    builder.Services.Configure<BlockchainConfig>(
        builder.Configuration.GetSection("BlockchainConfig"));
    builder.Services.Configure<FlashLoanContractsConfig>(
        builder.Configuration.GetSection("FlashLoanContracts"));
    builder.Services.Configure<CollectorSettings>(
        builder.Configuration.GetSection("CollectorSettings"));

    // Get settings for database service
    var settings = builder.Configuration
        .GetSection("CollectorSettings")
        .Get<CollectorSettings>() ?? new CollectorSettings();

    // Use production database if available, otherwise local
    var connectionString = builder.Configuration.GetConnectionString("ProductionDatabase")
        ?? builder.Configuration.GetConnectionString("FLISDatabase")
        ?? throw new InvalidOperationException("No database connection string configured");

    // Register database service
    builder.Services.AddSingleton(sp =>
        new DatabaseService(
            connectionString,
            settings.BatchSize,
            sp.GetRequiredService<ILogger<DatabaseService>>()));

    // Register background services
    builder.Services.AddHostedService<EVMCollectorService>();
    builder.Services.AddHostedService<XRPCollectorService>();

    // Add health monitoring service
    builder.Services.AddHostedService<HealthMonitorService>();

    var host = builder.Build();

    Log.Information("Starting FLIS Data Collector...");
    Log.Information("EVM Collector: {Enabled}", settings.EnableEVMCollector ? "Enabled" : "Disabled");
    Log.Information("XRP Collector: {Enabled}", settings.EnableXRPCollector ? "Enabled" : "Disabled");
    Log.Information("Batch Size: {BatchSize}", settings.BatchSize);
    Log.Information("Polling Interval: {Interval}s", settings.PollingIntervalSeconds);

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "FLIS Data Collector terminated unexpectedly");
}
finally
{
    Log.Information("FLIS Data Collector shutting down");
    await Log.CloseAndFlushAsync();
}
