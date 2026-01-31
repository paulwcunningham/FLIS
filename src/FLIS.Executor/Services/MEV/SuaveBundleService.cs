using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FLIS.Executor.Services.MEV;

/// <summary>
/// Suave MEV bundle service for Ethereum and EVM chains.
/// Submits confidential compute requests to Suave for MEV extraction.
/// </summary>
public interface ISuaveBundleService
{
    Task<SuaveBundleResult> SubmitBundleAsync(SuaveBundle bundle);
    Task<SuaveAuctionResult> SubmitToAuctionAsync(SuaveAuctionRequest request);
    Task<SuaveBundleStatus> GetBundleStatusAsync(string bundleId);
    Task<SuaveGasEstimate> GetGasEstimateAsync(string chainName);
    bool IsAvailable { get; }
}

public class SuaveBundleService : ISuaveBundleService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SuaveBundleService> _logger;
    private readonly string _suaveRpcUrl;
    private readonly string _kettleUrl;
    private readonly Dictionary<string, string> _builderEndpoints;
    
    public bool IsAvailable { get; private set; }
    
    public SuaveBundleService(
        IConfiguration configuration,
        ILogger<SuaveBundleService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        
        // Suave endpoints
        _suaveRpcUrl = configuration["Suave:RpcUrl"] ?? "https://rpc.rigil.suave.flashbots.net";
        _kettleUrl = configuration["Suave:KettleUrl"] ?? "https://kettle.rigil.suave.flashbots.net";
        
        // Builder endpoints for different chains
        _builderEndpoints = new Dictionary<string, string>
        {
            ["ethereum"] = configuration["Suave:Builders:Ethereum"] ?? "https://relay.flashbots.net",
            ["polygon"] = configuration["Suave:Builders:Polygon"] ?? "https://bor.txrelay.marlin.org",
            ["arbitrum"] = configuration["Suave:Builders:Arbitrum"] ?? "https://arb-relay.flashbots.net",
            ["base"] = configuration["Suave:Builders:Base"] ?? "https://base-relay.flashbots.net"
        };
        
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        
        // Add Flashbots authentication if configured
        var flashbotsKey = configuration["Suave:FlashbotsKey"];
        if (!string.IsNullOrEmpty(flashbotsKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Flashbots-Signature", flashbotsKey);
        }
        
        IsAvailable = !string.IsNullOrEmpty(_suaveRpcUrl);
        
        _logger.LogInformation("Suave bundle service initialized. Available: {Available}, Endpoint: {Endpoint}",
            IsAvailable, _suaveRpcUrl);
    }
    
    public async Task<SuaveBundleResult> SubmitBundleAsync(SuaveBundle bundle)
    {
        var submittedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        
        try
        {
            _logger.LogInformation(
                "Submitting Suave bundle for {Chain} with {Count} transactions, target block: {Block}",
                bundle.ChainName, bundle.Transactions.Count, bundle.TargetBlockNumber);
            
            // Get the appropriate builder endpoint
            var builderUrl = _builderEndpoints.GetValueOrDefault(bundle.ChainName.ToLower(), _builderEndpoints["ethereum"]);
            
            // Flashbots bundle format
            var request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "eth_sendBundle",
                @params = new object[]
                {
                    new
                    {
                        txs = bundle.Transactions,  // Array of signed transaction hex strings
                        blockNumber = $"0x{bundle.TargetBlockNumber:X}",
                        minTimestamp = bundle.MinTimestamp,
                        maxTimestamp = bundle.MaxTimestamp,
                        revertingTxHashes = bundle.RevertingTxHashes ?? new List<string>()
                    }
                }
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(builderUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Suave bundle submission failed: {StatusCode} - {Response}",
                    response.StatusCode, responseJson);
                
                return new SuaveBundleResult(
                    BundleId: bundle.BundleId,
                    ChainName: bundle.ChainName,
                    Success: false,
                    Reason: $"HTTP {response.StatusCode}: {responseJson}",
                    BundleHash: null,
                    BlockNumber: null,
                    BlockHash: null,
                    BundleIndex: null,
                    EffectiveGasPrice: null,
                    TotalGasUsed: null,
                    SubmittedAtNanos: submittedAt,
                    IncludedAtNanos: null,
                    LandedTransactions: new List<string>()
                );
            }
            
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);
            
            if (result.TryGetProperty("result", out var bundleResult))
            {
                var bundleHash = bundleResult.TryGetProperty("bundleHash", out var bh)
                    ? bh.GetString()
                    : bundle.BundleId;
                
                _logger.LogInformation("Suave bundle submitted successfully: {BundleHash}", bundleHash);
                
                return new SuaveBundleResult(
                    BundleId: bundle.BundleId,
                    ChainName: bundle.ChainName,
                    Success: true,
                    Reason: null,
                    BundleHash: bundleHash,
                    BlockNumber: null,
                    BlockHash: null,
                    BundleIndex: null,
                    EffectiveGasPrice: null,
                    TotalGasUsed: null,
                    SubmittedAtNanos: submittedAt,
                    IncludedAtNanos: null,
                    LandedTransactions: new List<string>()
                );
            }
            
            if (result.TryGetProperty("error", out var error))
            {
                var errorMessage = error.TryGetProperty("message", out var msg)
                    ? msg.GetString()
                    : "Unknown error";
                
                _logger.LogError("Suave bundle rejected: {Error}", errorMessage);
                
                return new SuaveBundleResult(
                    BundleId: bundle.BundleId,
                    ChainName: bundle.ChainName,
                    Success: false,
                    Reason: errorMessage,
                    BundleHash: null,
                    BlockNumber: null,
                    BlockHash: null,
                    BundleIndex: null,
                    EffectiveGasPrice: null,
                    TotalGasUsed: null,
                    SubmittedAtNanos: submittedAt,
                    IncludedAtNanos: null,
                    LandedTransactions: new List<string>()
                );
            }
            
            return new SuaveBundleResult(
                BundleId: bundle.BundleId,
                ChainName: bundle.ChainName,
                Success: false,
                Reason: "Unexpected response format",
                BundleHash: null,
                BlockNumber: null,
                BlockHash: null,
                BundleIndex: null,
                EffectiveGasPrice: null,
                TotalGasUsed: null,
                SubmittedAtNanos: submittedAt,
                IncludedAtNanos: null,
                LandedTransactions: new List<string>()
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit Suave bundle {BundleId}", bundle.BundleId);
            
            return new SuaveBundleResult(
                BundleId: bundle.BundleId,
                ChainName: bundle.ChainName,
                Success: false,
                Reason: ex.Message,
                BundleHash: null,
                BlockNumber: null,
                BlockHash: null,
                BundleIndex: null,
                EffectiveGasPrice: null,
                TotalGasUsed: null,
                SubmittedAtNanos: submittedAt,
                IncludedAtNanos: null,
                LandedTransactions: new List<string>()
            );
        }
    }
    
    public async Task<SuaveAuctionResult> SubmitToAuctionAsync(SuaveAuctionRequest request)
    {
        var submittedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        
        try
        {
            _logger.LogInformation(
                "Submitting to Suave auction for {Chain}, max bid: {MaxBid} ETH",
                request.ChainName, request.MaxBidWei / 1e18);
            
            // Suave confidential compute request
            var ccr = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "eth_sendRawTransaction",
                @params = new object[]
                {
                    new
                    {
                        confidentialInputs = request.ConfidentialInputs,
                        kettleAddress = request.KettleAddress,
                        chainId = request.ChainId,
                        to = request.ContractAddress,
                        data = request.CallData,
                        gas = $"0x{request.GasLimit:X}",
                        maxFeePerGas = $"0x{request.MaxFeePerGas:X}",
                        maxPriorityFeePerGas = $"0x{request.MaxPriorityFeePerGas:X}"
                    }
                }
            };
            
            var json = JsonSerializer.Serialize(ccr);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(_kettleUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                return new SuaveAuctionResult(
                    AuctionId: request.AuctionId,
                    ChainName: request.ChainName,
                    Success: false,
                    Reason: $"HTTP {response.StatusCode}",
                    WinningBid: null,
                    OurBid: request.MaxBidWei,
                    BlockNumber: null,
                    SubmittedAtNanos: submittedAt,
                    ResolvedAtNanos: null
                );
            }
            
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);
            
            if (result.TryGetProperty("result", out var auctionResult))
            {
                _logger.LogInformation("Suave auction submission successful");
                
                return new SuaveAuctionResult(
                    AuctionId: request.AuctionId,
                    ChainName: request.ChainName,
                    Success: true,
                    Reason: null,
                    WinningBid: null,  // Will be known after auction resolves
                    OurBid: request.MaxBidWei,
                    BlockNumber: null,
                    SubmittedAtNanos: submittedAt,
                    ResolvedAtNanos: null
                );
            }
            
            return new SuaveAuctionResult(
                AuctionId: request.AuctionId,
                ChainName: request.ChainName,
                Success: false,
                Reason: "Unexpected response",
                WinningBid: null,
                OurBid: request.MaxBidWei,
                BlockNumber: null,
                SubmittedAtNanos: submittedAt,
                ResolvedAtNanos: null
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit to Suave auction {AuctionId}", request.AuctionId);
            
            return new SuaveAuctionResult(
                AuctionId: request.AuctionId,
                ChainName: request.ChainName,
                Success: false,
                Reason: ex.Message,
                WinningBid: null,
                OurBid: request.MaxBidWei,
                BlockNumber: null,
                SubmittedAtNanos: submittedAt,
                ResolvedAtNanos: null
            );
        }
    }
    
    public async Task<SuaveBundleStatus> GetBundleStatusAsync(string bundleId)
    {
        try
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "flashbots_getBundleStats",
                @params = new object[] { bundleId }
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(_builderEndpoints["ethereum"], content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);
            
            if (result.TryGetProperty("result", out var stats))
            {
                var isSimulated = stats.TryGetProperty("isSimulated", out var sim) && sim.GetBoolean();
                var isHighPriority = stats.TryGetProperty("isHighPriority", out var hp) && hp.GetBoolean();
                var receivedAt = stats.TryGetProperty("receivedAt", out var ra) ? ra.GetString() : null;
                
                return new SuaveBundleStatus(
                    BundleId: bundleId,
                    Status: isSimulated ? "simulated" : "pending",
                    IsSimulated: isSimulated,
                    IsHighPriority: isHighPriority,
                    ReceivedAt: receivedAt,
                    BlockNumber: null,
                    Error: null
                );
            }
            
            return new SuaveBundleStatus(
                BundleId: bundleId,
                Status: "not_found",
                IsSimulated: false,
                IsHighPriority: false,
                ReceivedAt: null,
                BlockNumber: null,
                Error: "Bundle not found"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Suave bundle status for {BundleId}", bundleId);
            
            return new SuaveBundleStatus(
                BundleId: bundleId,
                Status: "error",
                IsSimulated: false,
                IsHighPriority: false,
                ReceivedAt: null,
                BlockNumber: null,
                Error: ex.Message
            );
        }
    }
    
    public async Task<SuaveGasEstimate> GetGasEstimateAsync(string chainName)
    {
        try
        {
            // Get current gas prices from the network
            var builderUrl = _builderEndpoints.GetValueOrDefault(chainName.ToLower(), _builderEndpoints["ethereum"]);
            
            var request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "eth_gasPrice",
                @params = Array.Empty<object>()
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(builderUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);
            
            if (result.TryGetProperty("result", out var gasPrice))
            {
                var gasPriceWei = Convert.ToInt64(gasPrice.GetString(), 16);
                var gasPriceGwei = gasPriceWei / 1e9;
                
                return new SuaveGasEstimate(
                    ChainName: chainName,
                    BaseFeeGwei: gasPriceGwei * 0.8,  // Estimate base fee
                    PriorityFeeGwei: gasPriceGwei * 0.2,  // Estimate priority fee
                    RecommendedMaxFeeGwei: gasPriceGwei * 1.5,
                    Timestamp: DateTime.UtcNow
                );
            }
            
            // Default estimates
            return new SuaveGasEstimate(
                ChainName: chainName,
                BaseFeeGwei: 30,
                PriorityFeeGwei: 2,
                RecommendedMaxFeeGwei: 50,
                Timestamp: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Suave gas estimate for {Chain}", chainName);
            
            return new SuaveGasEstimate(
                ChainName: chainName,
                BaseFeeGwei: 50,
                PriorityFeeGwei: 3,
                RecommendedMaxFeeGwei: 100,
                Timestamp: DateTime.UtcNow
            );
        }
    }
    
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

// Suave-specific models
public record SuaveBundle(
    string BundleId,
    string ChainName,
    List<string> Transactions,  // Signed transaction hex strings
    long TargetBlockNumber,
    long? MinTimestamp,
    long? MaxTimestamp,
    List<string>? RevertingTxHashes
);

public record SuaveBundleResult(
    string BundleId,
    string ChainName,
    bool Success,
    string? Reason,
    string? BundleHash,
    long? BlockNumber,
    string? BlockHash,
    int? BundleIndex,
    decimal? EffectiveGasPrice,
    long? TotalGasUsed,
    long SubmittedAtNanos,
    long? IncludedAtNanos,
    List<string> LandedTransactions
);

public record SuaveBundleStatus(
    string BundleId,
    string Status,
    bool IsSimulated,
    bool IsHighPriority,
    string? ReceivedAt,
    long? BlockNumber,
    string? Error
);

public record SuaveAuctionRequest(
    string AuctionId,
    string ChainName,
    int ChainId,
    string KettleAddress,
    string ContractAddress,
    string CallData,
    string ConfidentialInputs,
    long MaxBidWei,
    long GasLimit,
    long MaxFeePerGas,
    long MaxPriorityFeePerGas
);

public record SuaveAuctionResult(
    string AuctionId,
    string ChainName,
    bool Success,
    string? Reason,
    decimal? WinningBid,
    decimal OurBid,
    long? BlockNumber,
    long SubmittedAtNanos,
    long? ResolvedAtNanos
);

public record SuaveGasEstimate(
    string ChainName,
    double BaseFeeGwei,
    double PriorityFeeGwei,
    double RecommendedMaxFeeGwei,
    DateTime Timestamp
);
