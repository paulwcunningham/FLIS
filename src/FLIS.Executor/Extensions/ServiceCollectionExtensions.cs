using Microsoft.Extensions.DependencyInjection;
using FLIS.Executor.Services;
using FLIS.Executor.Services.MEV;

namespace FLIS.Executor.Extensions;

/// <summary>
/// Service collection extensions for registering FLIS services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all FLIS executor services to the service collection.
    /// </summary>
    public static IServiceCollection AddFlisServices(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<IResultPublisher, ResultPublisher>();
        services.AddSingleton<ITransactionManager, TransactionManager>();
        
        // MEV services
        services.AddSingleton<IJitoBundleService, JitoBundleService>();
        services.AddSingleton<ISuaveBundleService, SuaveBundleService>();
        services.AddSingleton<IMevCoordinator, MevCoordinator>();
        
        return services;
    }
    
    /// <summary>
    /// Adds MEV-specific services for Jito and Suave integration.
    /// </summary>
    public static IServiceCollection AddMevServices(this IServiceCollection services)
    {
        services.AddSingleton<IJitoBundleService, JitoBundleService>();
        services.AddSingleton<ISuaveBundleService, SuaveBundleService>();
        services.AddSingleton<IMevCoordinator, MevCoordinator>();
        
        return services;
    }
}
