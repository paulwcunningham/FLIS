using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using FLIS.AnalysisEngine.Data;
using FLIS.AnalysisEngine.Models;
using FLIS.AnalysisEngine.Services;
using FLIS.AnalysisEngine.Api;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("==========================================");
    Log.Information("  FLIS - Flash Loan Intelligence Service  ");
    Log.Information("  Analysis Engine v1.0.0                  ");
    Log.Information("==========================================");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Services.AddSerilog();

    // Bind configuration sections
    builder.Services.Configure<AnalysisSettings>(
        builder.Configuration.GetSection("AnalysisSettings"));
    builder.Services.Configure<ModelSettings>(
        builder.Configuration.GetSection("ModelSettings"));

    // Get connection string
    var connectionString = builder.Configuration.GetConnectionString("ProductionDatabase")
        ?? builder.Configuration.GetConnectionString("FlisDatabase")
        ?? throw new InvalidOperationException("No database connection string configured");

    // Register DbContext
    builder.Services.AddDbContext<FlisDbContext>(options =>
        options.UseNpgsql(connectionString));

    // Register services
    builder.Services.AddSingleton<MlModelService>();
    builder.Services.AddHostedService<FeatureEngineeringService>();
    builder.Services.AddHostedService<PatternRecognitionService>();
    builder.Services.AddHostedService<ModelTrainingService>();
    builder.Services.AddHostedService<DailySummaryService>();

    // Add API controllers
    builder.Services.AddControllers();

    // Add Swagger/OpenAPI
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "FLIS Analysis Engine API",
            Version = "v1",
            Description = "Flash Loan Intelligence Service - ML-powered prediction and pattern recognition API",
            Contact = new OpenApiContact
            {
                Name = "Magnus Trading Platform",
                Email = "support@magnus.trading"
            }
        });
    });

    // Add CORS
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    var app = builder.Build();

    // Ensure models directory exists
    var modelSettings = builder.Configuration.GetSection("ModelSettings").Get<ModelSettings>()
        ?? new ModelSettings();
    Directory.CreateDirectory(modelSettings.ModelsDirectory);

    // Configure the HTTP request pipeline
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "FLIS Analysis Engine API v1");
        c.RoutePrefix = string.Empty; // Serve Swagger UI at root
    });

    app.UseCors();
    app.UseRouting();
    app.MapControllers();

    // Add minimal API endpoints
    app.MapFlisApiEndpoints();

    Log.Information("Starting FLIS Analysis Engine...");
    Log.Information("API documentation available at: http://localhost:5200/swagger");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "FLIS Analysis Engine terminated unexpectedly");
}
finally
{
    Log.Information("FLIS Analysis Engine shutting down");
    await Log.CloseAndFlushAsync();
}
