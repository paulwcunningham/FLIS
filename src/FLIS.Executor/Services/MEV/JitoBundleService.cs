using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FLIS.Executor.Services.MEV;

/// <summary>
/// Jito MEV bundle service for Solana.
/// Submits transaction bundles to Jito block engine for MEV extraction.
/// </summary>
public interface IJitoBundleService
{
    Task<JitoBundleResult> SubmitBundleAsync(JitoBundle bundle);
    Task<JitoBundleStatus> GetBundleStatusAsync(string bundleId);
    Task<JitoTipEstimate> GetTipEstimateAsync();
    bool IsAvailable { get; }
}

public class JitoBundleService : IJitoBundleService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<JitoBundleService> _logger;
    private readonly string _blockEngineUrl;
    private readonly string _bundleApiUrl;
    
    public bool IsAvailable { get; private set; }
    
    public JitoBundleService(
        IConfiguration configuration,
        ILogger<JitoBundleService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        
        // Jito block engine endpoints
        _blockEngineUrl = configuration["Jito:BlockEngineUrl"] ?? "https://mainnet.block-engine.jito.wtf";
        _bundleApiUrl = configuration["Jito:BundleApiUrl"] ?? "https://mainnet.block-engine.jito.wtf/api/v1/bundles";
        
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        
        // Add authentication if configured
        var authToken = configuration["Jito:AuthToken"];
        if (!string.IsNullOrEmpty(authToken))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");
        }
        
        IsAvailable = !string.IsNullOrEmpty(_blockEngineUrl);
        
        _logger.LogInformation("Jito bundle service initialized. Available: {Available}, Endpoint: {Endpoint}",
            IsAvailable, _blockEngineUrl);
    }
    
    public async Task<JitoBundleResult> SubmitBundleAsync(JitoBundle bundle)
    {
        var submittedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000; // Convert to nanos
        
        try
        {
            _logger.LogInformation("Submitting Jito bundle with {Count} transactions, tip: {Tip} SOL",
                bundle.Transactions.Count, bundle.TipLamports / 1_000_000_000.0);
            
            // Jito bundle format
            var request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "sendBundle",
                @params = new object[]
                {
                    bundle.Transactions,  // Array of base64-encoded transactions
                    new
                    {
                        encoding = "base64",
                        skipPreflight = bundle.SkipPreflight,
                        maxRetries = bundle.MaxRetries
                    }
                }
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(_bundleApiUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Jito bundle submission failed: {StatusCode} - {Response}",
                    response.StatusCode, responseJson);
                
                return new JitoBundleResult(
                    BundleId: bundle.BundleId,
                    Success: false,
                    Reason: $"HTTP {response.StatusCode}: {responseJson}",
                    BlockHash: null,
                    Slot: null,
                    BundleIndex: null,
                    TipPaid: bundle.TipLamports / 1_000_000_000.0m,
                    SubmittedAtNanos: submittedAt,
                    ConfirmedAtNanos: null,
                    LandedTransactions: new List<string>()
                );
            }
            
            // Parse response
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);
            
            if (result.TryGetProperty("result", out var bundleResult))
            {
                var bundleId = bundleResult.GetString() ?? bundle.BundleId;
                
                _logger.LogInformation("Jito bundle submitted successfully: {BundleId}", bundleId);
                
                return new JitoBundleResult(
                    BundleId: bundleId,
                    Success: true,
                    Reason: null,
                    BlockHash: null,  // Will be populated when confirmed
                    Slot: null,
                    BundleIndex: null,
                    TipPaid: bundle.TipLamports / 1_000_000_000.0m,
                    SubmittedAtNanos: submittedAt,
                    ConfirmedAtNanos: null,
                    LandedTransactions: new List<string>()
                );
            }
            
            if (result.TryGetProperty("error", out var error))
            {
                var errorMessage = error.TryGetProperty("message", out var msg) 
                    ? msg.GetString() 
                    : "Unknown error";
                
                _logger.LogError("Jito bundle rejected: {Error}", errorMessage);
                
                return new JitoBundleResult(
                    BundleId: bundle.BundleId,
                    Success: false,
                    Reason: errorMessage,
                    BlockHash: null,
                    Slot: null,
                    BundleIndex: null,
                    TipPaid: 0,
                    SubmittedAtNanos: submittedAt,
                    ConfirmedAtNanos: null,
                    LandedTransactions: new List<string>()
                );
            }
            
            return new JitoBundleResult(
                BundleId: bundle.BundleId,
                Success: false,
                Reason: "Unexpected response format",
                BlockHash: null,
                Slot: null,
                BundleIndex: null,
                TipPaid: 0,
                SubmittedAtNanos: submittedAt,
                ConfirmedAtNanos: null,
                LandedTransactions: new List<string>()
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit Jito bundle {BundleId}", bundle.BundleId);
            
            return new JitoBundleResult(
                BundleId: bundle.BundleId,
                Success: false,
                Reason: ex.Message,
                BlockHash: null,
                Slot: null,
                BundleIndex: null,
                TipPaid: 0,
                SubmittedAtNanos: submittedAt,
                ConfirmedAtNanos: null,
                LandedTransactions: new List<string>()
            );
        }
    }
    
    public async Task<JitoBundleStatus> GetBundleStatusAsync(string bundleId)
    {
        try
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "getBundleStatuses",
                @params = new object[] { new[] { bundleId } }
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(_bundleApiUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);
            
            if (result.TryGetProperty("result", out var statuses) &&
                statuses.TryGetProperty("value", out var values) &&
                values.GetArrayLength() > 0)
            {
                var status = values[0];
                
                var confirmationStatus = status.TryGetProperty("confirmation_status", out var cs)
                    ? cs.GetString() ?? "unknown"
                    : "unknown";
                
                var slot = status.TryGetProperty("slot", out var s) ? s.GetInt64() : (long?)null;
                
                var transactions = new List<string>();
                if (status.TryGetProperty("transactions", out var txs))
                {
                    foreach (var tx in txs.EnumerateArray())
                    {
                        transactions.Add(tx.GetString() ?? "");
                    }
                }
                
                return new JitoBundleStatus(
                    BundleId: bundleId,
                    Status: confirmationStatus,
                    Slot: slot,
                    Transactions: transactions,
                    Error: null
                );
            }
            
            return new JitoBundleStatus(
                BundleId: bundleId,
                Status: "not_found",
                Slot: null,
                Transactions: new List<string>(),
                Error: "Bundle not found"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Jito bundle status for {BundleId}", bundleId);
            
            return new JitoBundleStatus(
                BundleId: bundleId,
                Status: "error",
                Slot: null,
                Transactions: new List<string>(),
                Error: ex.Message
            );
        }
    }
    
    public async Task<JitoTipEstimate> GetTipEstimateAsync()
    {
        try
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "getTipAccounts",
                @params = Array.Empty<object>()
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(_bundleApiUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            // Default tip estimates based on network conditions
            // In production, these would be dynamically calculated
            return new JitoTipEstimate(
                MinTipLamports: 1000,           // 0.000001 SOL
                MedianTipLamports: 10000,       // 0.00001 SOL
                P75TipLamports: 50000,          // 0.00005 SOL
                P95TipLamports: 100000,         // 0.0001 SOL
                RecommendedTipLamports: 25000,  // 0.000025 SOL
                Timestamp: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Jito tip estimate");
            
            // Return conservative defaults
            return new JitoTipEstimate(
                MinTipLamports: 10000,
                MedianTipLamports: 50000,
                P75TipLamports: 100000,
                P95TipLamports: 500000,
                RecommendedTipLamports: 75000,
                Timestamp: DateTime.UtcNow
            );
        }
    }
    
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

// Jito-specific models
public record JitoBundle(
    string BundleId,
    List<string> Transactions,  // Base64-encoded transactions
    long TipLamports,
    bool SkipPreflight = false,
    int MaxRetries = 3
);

public record JitoBundleResult(
    string BundleId,
    bool Success,
    string? Reason,
    string? BlockHash,
    long? Slot,
    int? BundleIndex,
    decimal TipPaid,
    long SubmittedAtNanos,
    long? ConfirmedAtNanos,
    List<string> LandedTransactions
);

public record JitoBundleStatus(
    string BundleId,
    string Status,  // "pending", "landed", "failed", "not_found"
    long? Slot,
    List<string> Transactions,
    string? Error
);

public record JitoTipEstimate(
    long MinTipLamports,
    long MedianTipLamports,
    long P75TipLamports,
    long P95TipLamports,
    long RecommendedTipLamports,
    DateTime Timestamp
);
