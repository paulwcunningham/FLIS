using System.Text;
using System.Text.Json;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FLIS.Executor.Services;

public interface IGasBiddingService
{
    Task<GasBidResult> GetOptimalGasBidAsync(FlashLoanOpportunity opportunity);
}

public class GasBiddingService : IGasBiddingService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GasBiddingService> _logger;
    private readonly string _baseUrl;
    private readonly string _endpoint;

    public GasBiddingService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GasBiddingService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        _baseUrl = configuration["MLOptimizer:BaseUrl"]
            ?? throw new InvalidOperationException("MLOptimizer:BaseUrl not configured");
        _endpoint = configuration["MLOptimizer:GasBiddingEndpoint"]
            ?? throw new InvalidOperationException("MLOptimizer:GasBiddingEndpoint not configured");
    }

    public async Task<GasBidResult> GetOptimalGasBidAsync(FlashLoanOpportunity opportunity)
    {
        var request = new
        {
            chainName = opportunity.ChainName,
            asset = opportunity.Asset,
            amount = opportunity.Amount,
            expectedProfit = opportunity.MinProfit
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync($"{_baseUrl}{_endpoint}", content);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GasBidResult>(responseJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return result ?? throw new InvalidOperationException("Failed to deserialize gas bid result");
    }
}
