using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FLIS.Executor.Services.MEV;

/// <summary>
/// MEV Coordinator that orchestrates Jito (Solana) and Suave (Ethereum/EVM) bundle services.
/// Automatically selects the appropriate MEV provider based on chain and opportunity characteristics.
/// </summary>
public interface IMevCoordinator
{
    Task<MevExecutionResult> ExecuteWithMevAsync(FlashLoanOpportunity opportunity, List<string> transactions);
    Task<MevProviderStatus> GetProviderStatusAsync(string provider);
    string SelectBestProvider(FlashLoanOpportunity opportunity);
    bool IsMevAvailable(string chainName);
}

public class MevCoordinator : IMevCoordinator
{
    private readonly IJitoBundleService _jitoService;
    private readonly ISuaveBundleService _suaveService;
    private readonly IResultPublisher _resultPublisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MevCoordinator> _logger;
    
    // Chain to MEV provider mapping
    private readonly Dictionary<string, string> _chainProviders = new()
    {
        ["solana"] = "jito",
        ["ethereum"] = "suave",
        ["polygon"] = "suave",
        ["arbitrum"] = "suave",
        ["base"] = "suave",
        ["optimism"] = "suave",
        ["avalanche"] = "suave",
        ["bsc"] = "suave"
    };
    
    public MevCoordinator(
        IJitoBundleService jitoService,
        ISuaveBundleService suaveService,
        IResultPublisher resultPublisher,
        IConfiguration configuration,
        ILogger<MevCoordinator> logger)
    {
        _jitoService = jitoService;
        _suaveService = suaveService;
        _resultPublisher = resultPublisher;
        _configuration = configuration;
        _logger = logger;
    }
    
    public async Task<MevExecutionResult> ExecuteWithMevAsync(
        FlashLoanOpportunity opportunity,
        List<string> transactions)
    {
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        
        // Select MEV provider
        var provider = !string.IsNullOrEmpty(opportunity.PreferredMevProvider)
            ? opportunity.PreferredMevProvider
            : SelectBestProvider(opportunity);
        
        _logger.LogInformation(
            "Executing MEV bundle for opportunity {Id} on {Chain} via {Provider}",
            opportunity.Id, opportunity.ChainName, provider);
        
        try
        {
            MevExecutionResult result;
            
            if (provider.Equals("jito", StringComparison.OrdinalIgnoreCase))
            {
                result = await ExecuteWithJitoAsync(opportunity, transactions, startTime);
            }
            else if (provider.Equals("suave", StringComparison.OrdinalIgnoreCase))
            {
                result = await ExecuteWithSuaveAsync(opportunity, transactions, startTime);
            }
            else
            {
                _logger.LogWarning("Unknown MEV provider {Provider}, falling back to standard execution", provider);
                
                result = new MevExecutionResult(
                    OpportunityId: opportunity.Id,
                    Provider: "none",
                    Success: false,
                    Reason: $"Unknown MEV provider: {provider}",
                    BundleId: null,
                    TransactionHashes: new List<string>(),
                    BlockNumber: null,
                    TipPaid: 0,
                    ProfitRealized: null,
                    SubmittedAtNanos: startTime,
                    ConfirmedAtNanos: null,
                    WasFrontrun: false,
                    WasBackrun: false
                );
            }
            
            // Publish MEV result to NATS for MLOptimizer
            await _resultPublisher.PublishMevBundleResultAsync(new MevBundleResult(
                BundleId: result.BundleId ?? opportunity.Id,
                Provider: result.Provider,
                ChainName: opportunity.ChainName,
                Success: result.Success,
                Reason: result.Reason,
                BlockHash: null,
                BlockNumber: result.BlockNumber,
                BundleIndex: null,
                TipPaid: result.TipPaid,
                ProfitRealized: result.ProfitRealized,
                SubmittedAtNanos: result.SubmittedAtNanos,
                IncludedAtNanos: result.ConfirmedAtNanos,
                TransactionHashes: result.TransactionHashes,
                WasSimulated: true,
                SimulatedProfit: opportunity.ExpectedProfit
            ));
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MEV execution failed for opportunity {Id}", opportunity.Id);
            
            return new MevExecutionResult(
                OpportunityId: opportunity.Id,
                Provider: provider,
                Success: false,
                Reason: ex.Message,
                BundleId: null,
                TransactionHashes: new List<string>(),
                BlockNumber: null,
                TipPaid: 0,
                ProfitRealized: null,
                SubmittedAtNanos: startTime,
                ConfirmedAtNanos: null,
                WasFrontrun: false,
                WasBackrun: false
            );
        }
    }
    
    private async Task<MevExecutionResult> ExecuteWithJitoAsync(
        FlashLoanOpportunity opportunity,
        List<string> transactions,
        long startTime)
    {
        // Get tip estimate
        var tipEstimate = await _jitoService.GetTipEstimateAsync();
        
        // Calculate tip based on expected profit and AOI score
        var tipLamports = CalculateJitoTip(opportunity, tipEstimate);
        
        var bundle = new JitoBundle(
            BundleId: $"jito_{opportunity.Id}",
            Transactions: transactions,
            TipLamports: tipLamports,
            SkipPreflight: false,
            MaxRetries: 3
        );
        
        var result = await _jitoService.SubmitBundleAsync(bundle);
        
        if (result.Success)
        {
            // Wait for confirmation
            var status = await WaitForJitoConfirmationAsync(result.BundleId, TimeSpan.FromSeconds(30));
            
            return new MevExecutionResult(
                OpportunityId: opportunity.Id,
                Provider: "jito",
                Success: status.Status == "landed",
                Reason: status.Status == "landed" ? null : status.Error,
                BundleId: result.BundleId,
                TransactionHashes: status.Transactions,
                BlockNumber: status.Slot,
                TipPaid: result.TipPaid,
                ProfitRealized: null,  // Will be calculated from on-chain data
                SubmittedAtNanos: startTime,
                ConfirmedAtNanos: status.Status == "landed" 
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000 
                    : null,
                WasFrontrun: false,
                WasBackrun: false
            );
        }
        
        return new MevExecutionResult(
            OpportunityId: opportunity.Id,
            Provider: "jito",
            Success: false,
            Reason: result.Reason,
            BundleId: result.BundleId,
            TransactionHashes: new List<string>(),
            BlockNumber: null,
            TipPaid: 0,
            ProfitRealized: null,
            SubmittedAtNanos: startTime,
            ConfirmedAtNanos: null,
            WasFrontrun: false,
            WasBackrun: false
        );
    }
    
    private async Task<MevExecutionResult> ExecuteWithSuaveAsync(
        FlashLoanOpportunity opportunity,
        List<string> transactions,
        long startTime)
    {
        // Get gas estimate
        var gasEstimate = await _suaveService.GetGasEstimateAsync(opportunity.ChainName);
        
        // Calculate target block (current + 1)
        var targetBlock = await GetCurrentBlockNumberAsync(opportunity.ChainName) + 1;
        
        var bundle = new SuaveBundle(
            BundleId: $"suave_{opportunity.Id}",
            ChainName: opportunity.ChainName,
            Transactions: transactions,
            TargetBlockNumber: targetBlock,
            MinTimestamp: null,
            MaxTimestamp: DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
            RevertingTxHashes: null
        );
        
        var result = await _suaveService.SubmitBundleAsync(bundle);
        
        if (result.Success)
        {
            // Wait for inclusion
            var status = await WaitForSuaveConfirmationAsync(result.BundleHash ?? bundle.BundleId, TimeSpan.FromSeconds(60));
            
            return new MevExecutionResult(
                OpportunityId: opportunity.Id,
                Provider: "suave",
                Success: status.BlockNumber.HasValue,
                Reason: status.BlockNumber.HasValue ? null : status.Error,
                BundleId: result.BundleHash,
                TransactionHashes: result.LandedTransactions,
                BlockNumber: status.BlockNumber,
                TipPaid: result.EffectiveGasPrice ?? 0,
                ProfitRealized: null,
                SubmittedAtNanos: startTime,
                ConfirmedAtNanos: status.BlockNumber.HasValue
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000
                    : null,
                WasFrontrun: false,
                WasBackrun: false
            );
        }
        
        return new MevExecutionResult(
            OpportunityId: opportunity.Id,
            Provider: "suave",
            Success: false,
            Reason: result.Reason,
            BundleId: result.BundleHash,
            TransactionHashes: new List<string>(),
            BlockNumber: null,
            TipPaid: 0,
            ProfitRealized: null,
            SubmittedAtNanos: startTime,
            ConfirmedAtNanos: null,
            WasFrontrun: false,
            WasBackrun: false
        );
    }
    
    private long CalculateJitoTip(FlashLoanOpportunity opportunity, JitoTipEstimate estimate)
    {
        // Base tip on expected profit and confidence
        var expectedProfitLamports = (long)(opportunity.ExpectedProfit * 1_000_000_000); // Convert SOL to lamports
        var maxTipLamports = opportunity.MaxMevTip.HasValue
            ? (long)(opportunity.MaxMevTip.Value * 1_000_000_000)
            : expectedProfitLamports / 10; // Default: 10% of expected profit
        
        // Adjust based on AOI score (higher AOI = more aggressive tip)
        var aoiMultiplier = opportunity.AoiScore.HasValue
            ? 0.5 + (double)opportunity.AoiScore.Value * 0.5  // 0.5x to 1.0x
            : 0.75;
        
        var calculatedTip = (long)(estimate.RecommendedTipLamports * aoiMultiplier);
        
        // Ensure tip is within bounds
        return Math.Min(Math.Max(calculatedTip, estimate.MinTipLamports), maxTipLamports);
    }
    
    private async Task<JitoBundleStatus> WaitForJitoConfirmationAsync(string bundleId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        
        while (DateTime.UtcNow < deadline)
        {
            var status = await _jitoService.GetBundleStatusAsync(bundleId);
            
            if (status.Status == "landed" || status.Status == "failed")
            {
                return status;
            }
            
            await Task.Delay(500);
        }
        
        return new JitoBundleStatus(
            BundleId: bundleId,
            Status: "timeout",
            Slot: null,
            Transactions: new List<string>(),
            Error: "Confirmation timeout"
        );
    }
    
    private async Task<SuaveBundleStatus> WaitForSuaveConfirmationAsync(string bundleId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        
        while (DateTime.UtcNow < deadline)
        {
            var status = await _suaveService.GetBundleStatusAsync(bundleId);
            
            if (status.BlockNumber.HasValue || status.Status == "failed")
            {
                return status;
            }
            
            await Task.Delay(1000);
        }
        
        return new SuaveBundleStatus(
            BundleId: bundleId,
            Status: "timeout",
            IsSimulated: false,
            IsHighPriority: false,
            ReceivedAt: null,
            BlockNumber: null,
            Error: "Confirmation timeout"
        );
    }
    
    private async Task<long> GetCurrentBlockNumberAsync(string chainName)
    {
        // In production, this would query the actual chain
        // For now, return a placeholder
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
    
    public string SelectBestProvider(FlashLoanOpportunity opportunity)
    {
        var chainLower = opportunity.ChainName.ToLower();
        
        if (_chainProviders.TryGetValue(chainLower, out var provider))
        {
            return provider;
        }
        
        // Default to Suave for unknown EVM chains
        return "suave";
    }
    
    public bool IsMevAvailable(string chainName)
    {
        var provider = _chainProviders.GetValueOrDefault(chainName.ToLower());
        
        return provider switch
        {
            "jito" => _jitoService.IsAvailable,
            "suave" => _suaveService.IsAvailable,
            _ => false
        };
    }
    
    public async Task<MevProviderStatus> GetProviderStatusAsync(string provider)
    {
        if (provider.Equals("jito", StringComparison.OrdinalIgnoreCase))
        {
            var tipEstimate = await _jitoService.GetTipEstimateAsync();
            
            return new MevProviderStatus(
                Provider: "jito",
                IsAvailable: _jitoService.IsAvailable,
                SupportedChains: new[] { "solana" },
                CurrentTipEstimate: tipEstimate.RecommendedTipLamports / 1_000_000_000.0m,
                AvgLatencyMs: 50,  // Placeholder
                SuccessRate: 0.85,  // Placeholder
                LastUpdated: DateTime.UtcNow
            );
        }
        
        if (provider.Equals("suave", StringComparison.OrdinalIgnoreCase))
        {
            var gasEstimate = await _suaveService.GetGasEstimateAsync("ethereum");
            
            return new MevProviderStatus(
                Provider: "suave",
                IsAvailable: _suaveService.IsAvailable,
                SupportedChains: new[] { "ethereum", "polygon", "arbitrum", "base", "optimism" },
                CurrentTipEstimate: (decimal)gasEstimate.PriorityFeeGwei,
                AvgLatencyMs: 100,  // Placeholder
                SuccessRate: 0.80,  // Placeholder
                LastUpdated: DateTime.UtcNow
            );
        }
        
        return new MevProviderStatus(
            Provider: provider,
            IsAvailable: false,
            SupportedChains: Array.Empty<string>(),
            CurrentTipEstimate: 0,
            AvgLatencyMs: 0,
            SuccessRate: 0,
            LastUpdated: DateTime.UtcNow
        );
    }
}

// Coordinator result models
public record MevExecutionResult(
    string OpportunityId,
    string Provider,
    bool Success,
    string? Reason,
    string? BundleId,
    List<string> TransactionHashes,
    long? BlockNumber,
    decimal TipPaid,
    decimal? ProfitRealized,
    long SubmittedAtNanos,
    long? ConfirmedAtNanos,
    bool WasFrontrun,
    bool WasBackrun
);

public record MevProviderStatus(
    string Provider,
    bool IsAvailable,
    string[] SupportedChains,
    decimal CurrentTipEstimate,
    double AvgLatencyMs,
    double SuccessRate,
    DateTime LastUpdated
);
