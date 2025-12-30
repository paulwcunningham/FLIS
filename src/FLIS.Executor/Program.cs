using FLIS.Executor.Services;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Services.AddSerilog();

// Register services
builder.Services.AddSingleton<IMultiChainRpcProvider, MultiChainRpcProvider>();
builder.Services.AddSingleton<IGasBiddingService, GasBiddingService>();
builder.Services.AddSingleton<ISimulationService, SimulationService>();
builder.Services.AddSingleton<ITransactionManager, TransactionManager>();

builder.Services.AddHttpClient();

// Register background services
builder.Services.AddHostedService<NatsOpportunitySubscriber>();

var host = builder.Build();

try
{
    Log.Information("Starting FLIS.Executor service");
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
