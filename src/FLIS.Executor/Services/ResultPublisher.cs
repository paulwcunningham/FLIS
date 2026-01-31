using System.Text;
using System.Text.Json;
using NATS.Client;
using NATS.Client.JetStream;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FLIS.Executor.Services;

/// <summary>
/// Enhanced result publisher that sends execution results to MLOptimizer via NATS.
/// Supports both standard NATS pub/sub and JetStream for guaranteed delivery.
/// </summary>
public interface IResultPublisher
{
    Task PublishResultAsync(FlashLoanResult result);
    Task PublishStatusUpdateAsync(FlashLoanStatusUpdate status);
    Task PublishMevBundleResultAsync(MevBundleResult result);
}

public class ResultPublisher : IResultPublisher, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResultPublisher> _logger;
    private IConnection? _natsConnection;
    private IJetStream? _jetStream;
    private readonly bool _useJetStream;
    
    // NATS subjects aligned with MLOptimizer FlashLoanResultSubscriber
    private const string ResultSubjectPrefix = "flashloan.result";
    private const string StatusSubjectPrefix = "flashloan.status";
    private const string MevResultSubjectPrefix = "mev.bundle.result";
    private const string TrainingDataSubject = "mloptimizer.training.flashloan";
    
    public ResultPublisher(
        IConfiguration configuration,
        ILogger<ResultPublisher> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _useJetStream = configuration.GetValue<bool>("Nats:UseJetStream", true);
        
        InitializeNatsConnection();
    }
    
    private void InitializeNatsConnection()
    {
        try
        {
            var natsUrl = _configuration["Nats:Url"] ?? "nats://localhost:4222";
            var options = ConnectionFactory.GetDefaultOptions();
            options.Url = natsUrl;
            
            // Configure authentication if provided
            var user = _configuration["Nats:User"];
            var password = _configuration["Nats:Password"];
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password))
            {
                options.User = user;
                options.Password = password;
            }
            
            // Configure TLS if enabled
            if (_configuration.GetValue<bool>("Nats:UseTls", false))
            {
                options.Secure = true;
            }
            
            // Reconnection settings
            options.MaxReconnect = Options.ReconnectForever;
            options.ReconnectWait = 2000;
            
            var factory = new ConnectionFactory();
            _natsConnection = factory.CreateConnection(options);
            
            _logger.LogInformation("Connected to NATS at {Url}", natsUrl);
            
            // Initialize JetStream if enabled
            if (_useJetStream && _natsConnection != null)
            {
                try
                {
                    _jetStream = _natsConnection.CreateJetStreamContext();
                    _logger.LogInformation("JetStream context initialized for guaranteed delivery");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "JetStream not available, falling back to standard pub/sub");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to NATS - results will not be published");
        }
    }
    
    public async Task PublishResultAsync(FlashLoanResult result)
    {
        if (_natsConnection == null || !_natsConnection.State.Equals(ConnState.CONNECTED))
        {
            _logger.LogWarning("NATS not connected - skipping result publication for {OpportunityId}", result.OpportunityId);
            return;
        }
        
        try
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var data = Encoding.UTF8.GetBytes(json);
            
            // Publish to chain-specific subject (as expected by MLOptimizer FlashLoanResultSubscriber)
            var chainSubject = $"{ResultSubjectPrefix}.{result.ChainName.ToLower()}";
            
            if (_jetStream != null)
            {
                // Use JetStream for guaranteed delivery
                var ack = await _jetStream.PublishAsync(chainSubject, data);
                _logger.LogInformation(
                    "Published result via JetStream: {Subject} - OpportunityId={Id}, Success={Success}, Seq={Seq}",
                    chainSubject, result.OpportunityId, result.Success, ack.Seq);
            }
            else
            {
                // Fall back to standard pub/sub
                _natsConnection.Publish(chainSubject, data);
                _logger.LogInformation(
                    "Published result to NATS: {Subject} - OpportunityId={Id}, Success={Success}",
                    chainSubject, result.OpportunityId, result.Success);
            }
            
            // Also publish to training data subject for MLOptimizer learning
            await PublishTrainingDataAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish result for {OpportunityId}", result.OpportunityId);
        }
    }
    
    public async Task PublishStatusUpdateAsync(FlashLoanStatusUpdate status)
    {
        if (_natsConnection == null || !_natsConnection.State.Equals(ConnState.CONNECTED))
        {
            return;
        }
        
        try
        {
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var data = Encoding.UTF8.GetBytes(json);
            
            var subject = $"{StatusSubjectPrefix}.{status.OpportunityId}";
            _natsConnection.Publish(subject, data);
            
            _logger.LogDebug("Published status update: {Subject} - Status={Status}", subject, status.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish status update for {OpportunityId}", status.OpportunityId);
        }
    }
    
    public async Task PublishMevBundleResultAsync(MevBundleResult result)
    {
        if (_natsConnection == null || !_natsConnection.State.Equals(ConnState.CONNECTED))
        {
            return;
        }
        
        try
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var data = Encoding.UTF8.GetBytes(json);
            
            var subject = $"{MevResultSubjectPrefix}.{result.Provider.ToLower()}";
            
            if (_jetStream != null)
            {
                var ack = await _jetStream.PublishAsync(subject, data);
                _logger.LogInformation(
                    "Published MEV bundle result via JetStream: {Subject} - BundleId={BundleId}, Success={Success}",
                    subject, result.BundleId, result.Success);
            }
            else
            {
                _natsConnection.Publish(subject, data);
                _logger.LogInformation(
                    "Published MEV bundle result: {Subject} - BundleId={BundleId}, Success={Success}",
                    subject, result.BundleId, result.Success);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish MEV bundle result for {BundleId}", result.BundleId);
        }
    }
    
    private async Task PublishTrainingDataAsync(FlashLoanResult result)
    {
        try
        {
            // Create training data record for MLOptimizer continuous learning
            var trainingData = new
            {
                result.OpportunityId,
                result.ChainName,
                result.Strategy,
                result.Asset,
                result.Amount,
                result.Success,
                result.ActualProfitUsd,
                result.EstimatedProfitUsd,
                result.SlippageBps,
                result.GasUsed,
                result.ActualCostUsd,
                result.SpreadBps,
                result.OrderBookImbalance,
                result.VolatilityPercent,
                result.MarketRegime = result.MarketRegime,
                result.MevProvider,
                result.MevTipPaid,
                result.WasFrontrun,
                result.WasBackrun,
                
                // Calculate latency metrics
                TotalLatencyMs = result.TransactionConfirmedAtNanos.HasValue && result.OpportunityReceivedAtNanos.HasValue
                    ? (result.TransactionConfirmedAtNanos.Value - result.OpportunityReceivedAtNanos.Value) / 1_000_000.0
                    : (double?)null,
                SimulationLatencyMs = result.SimulationCompletedAtNanos.HasValue && result.SimulationStartedAtNanos.HasValue
                    ? (result.SimulationCompletedAtNanos.Value - result.SimulationStartedAtNanos.Value) / 1_000_000.0
                    : (double?)null,
                    
                result.Timestamp
            };
            
            var json = JsonSerializer.Serialize(trainingData, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var data = Encoding.UTF8.GetBytes(json);
            
            _natsConnection?.Publish(TrainingDataSubject, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish training data for {OpportunityId}", result.OpportunityId);
        }
    }
    
    public void Dispose()
    {
        _natsConnection?.Dispose();
    }
}

/// <summary>
/// MEV bundle execution result
/// </summary>
public record MevBundleResult(
    string BundleId,
    string Provider,  // "jito" or "suave"
    string ChainName,
    bool Success,
    string? Reason,
    string? BlockHash,
    long? BlockNumber,
    int? BundleIndex,
    decimal? TipPaid,
    decimal? ProfitRealized,
    long SubmittedAtNanos,
    long? IncludedAtNanos,
    List<string> TransactionHashes,
    bool WasSimulated,
    decimal? SimulatedProfit
);
