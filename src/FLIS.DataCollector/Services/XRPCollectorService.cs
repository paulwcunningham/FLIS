using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FLIS.DataCollector.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FLIS.DataCollector.Services;

/// <summary>
/// Background service that collects DEX arbitrage transactions from XRP Ledger
/// </summary>
public sealed class XRPCollectorService : BackgroundService
{
    private readonly BlockchainConfig _blockchainConfig;
    private readonly CollectorSettings _settings;
    private readonly DatabaseService _database;
    private readonly ILogger<XRPCollectorService> _logger;
    private readonly HttpClient _httpClient;

    private long _lastProcessedLedger;

    // XRP price in USD
    private const decimal XRP_PRICE_USD = 2.20m;

    public XRPCollectorService(
        IOptions<BlockchainConfig> blockchainConfig,
        IOptions<CollectorSettings> settings,
        DatabaseService database,
        ILogger<XRPCollectorService> logger)
    {
        _blockchainConfig = blockchainConfig.Value;
        _settings = settings.Value;
        _database = database;
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.EnableXRPCollector)
        {
            _logger.LogInformation("[XRPCollector] Disabled by configuration");
            return;
        }

        _logger.LogInformation("[XRPCollector] Starting XRP Ledger DEX arbitrage collection");

        // Get starting ledger
        try
        {
            _lastProcessedLedger = await GetLatestLedgerIndexAsync(stoppingToken) - 10;
            _logger.LogInformation("[XRPCollector] Starting from ledger {Ledger}", _lastProcessedLedger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[XRPCollector] Failed to get initial ledger index");
            return;
        }

        var pollingInterval = TimeSpan.FromSeconds(_settings.PollingIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNewLedgersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[XRPCollector] Error processing ledgers");
            }

            await Task.Delay(pollingInterval, stoppingToken);
        }
    }

    private async Task<long> GetLatestLedgerIndexAsync(CancellationToken ct)
    {
        var request = new
        {
            method = "ledger",
            @params = new[]
            {
                new { ledger_index = "validated" }
            }
        };

        var response = await SendRpcRequestAsync<LedgerResponse>(request, ct);
        return response?.Result?.LedgerIndex ?? 0;
    }

    private async Task ProcessNewLedgersAsync(CancellationToken ct)
    {
        var latestLedger = await GetLatestLedgerIndexAsync(ct);

        if (latestLedger <= _lastProcessedLedger)
            return;

        var ledgersToProcess = Math.Min(
            (int)(latestLedger - _lastProcessedLedger),
            _settings.MaxBlocksPerBatch);

        _logger.LogDebug("[XRPCollector] Processing ledgers {From} to {To}",
            _lastProcessedLedger + 1, _lastProcessedLedger + ledgersToProcess);

        var arbitrageCount = 0;

        for (var i = 1; i <= ledgersToProcess; i++)
        {
            var ledgerIndex = _lastProcessedLedger + i;

            try
            {
                var transactions = await GetLedgerTransactionsAsync(ledgerIndex, ct);

                if (transactions == null) continue;

                // Group transactions by account to detect arbitrage patterns
                var txsByAccount = transactions
                    .Where(tx => tx.TransactionType == "OfferCreate" ||
                                 tx.TransactionType == "Payment")
                    .GroupBy(tx => tx.Account)
                    .ToList();

                foreach (var accountTxs in txsByAccount)
                {
                    // Look for arbitrage patterns (3+ trades in same ledger)
                    var offerTxs = accountTxs
                        .Where(tx => tx.TransactionType == "OfferCreate")
                        .OrderBy(tx => tx.Sequence)
                        .ToList();

                    if (offerTxs.Count >= 3 && accountTxs.Key != null)
                    {
                        var arbitrageTx = AnalyzeArbitragePattern(
                            ledgerIndex,
                            accountTxs.Key,
                            offerTxs);

                        if (arbitrageTx != null)
                        {
                            var flashLoanTx = ConvertToFlashLoanTransaction(arbitrageTx);
                            _database.QueueTransaction(flashLoanTx);
                            arbitrageCount++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[XRPCollector] Error processing ledger {Ledger}", ledgerIndex);
            }
        }

        _lastProcessedLedger += ledgersToProcess;

        if (arbitrageCount > 0)
        {
            _logger.LogInformation(
                "[XRPCollector] Found {Count} arbitrage transactions in ledgers {From}-{To}",
                arbitrageCount, _lastProcessedLedger - ledgersToProcess + 1, _lastProcessedLedger);
        }
    }

    private async Task<List<XRPTransaction>?> GetLedgerTransactionsAsync(long ledgerIndex, CancellationToken ct)
    {
        var request = new
        {
            method = "ledger",
            @params = new[]
            {
                new
                {
                    ledger_index = ledgerIndex,
                    transactions = true,
                    expand = true
                }
            }
        };

        var response = await SendRpcRequestAsync<LedgerWithTransactionsResponse>(request, ct);
        return response?.Result?.Ledger?.Transactions;
    }

    private XRPArbitrageTransaction? AnalyzeArbitragePattern(
        long ledgerIndex,
        string account,
        List<XRPTransaction> offerTxs)
    {
        try
        {
            var tradeSteps = new List<XRPTradeStep>();
            decimal totalFees = 0;

            for (var i = 0; i < offerTxs.Count; i++)
            {
                var tx = offerTxs[i];

                // Parse TakerGets and TakerPays to determine trade direction
                var fromCurrency = ParseCurrency(tx.TakerPays);
                var toCurrency = ParseCurrency(tx.TakerGets);
                var fromAmount = ParseAmount(tx.TakerPays);
                var toAmount = ParseAmount(tx.TakerGets);

                if (fromAmount > 0 && toAmount > 0)
                {
                    tradeSteps.Add(new XRPTradeStep
                    {
                        FromCurrency = fromCurrency,
                        ToCurrency = toCurrency,
                        FromAmount = fromAmount,
                        ToAmount = toAmount,
                        ExchangeRate = toAmount / fromAmount,
                        StepIndex = i
                    });
                }

                totalFees += ParseFee(tx.Fee);
            }

            // Check if it's a circular arbitrage (starts and ends with same currency)
            if (tradeSteps.Count < 3)
                return null;

            var startCurrency = tradeSteps[0].FromCurrency;
            var endCurrency = tradeSteps[^1].ToCurrency;

            if (startCurrency != endCurrency)
                return null; // Not a circular arbitrage

            // Calculate profit
            var startAmount = tradeSteps[0].FromAmount;
            var endAmount = CalculateEndAmount(tradeSteps);
            var profit = endAmount - startAmount;
            var profitAfterFees = profit - (totalFees * XRP_PRICE_USD);

            // Use first transaction hash as identifier
            var txHash = offerTxs[0].Hash ?? Guid.NewGuid().ToString();

            return new XRPArbitrageTransaction
            {
                TxHash = txHash,
                LedgerIndex = ledgerIndex,
                Timestamp = DateTimeOffset.UtcNow, // Would get from ledger close time
                Account = account,
                TradeSteps = tradeSteps,
                StartCurrency = startCurrency,
                StartAmount = startAmount,
                EndAmount = endAmount,
                ProfitAmount = profitAfterFees,
                TransactionCostXRP = totalFees,
                IsProfitable = profitAfterFees > 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[XRPCollector] Error analyzing arbitrage pattern");
            return null;
        }
    }

    private FlashLoanTransaction ConvertToFlashLoanTransaction(XRPArbitrageTransaction arbTx)
    {
        var actions = arbTx.TradeSteps.Select(step => new FlashLoanAction
        {
            ActionType = "swap",
            FromToken = step.FromCurrency,
            ToToken = step.ToCurrency,
            Amount = step.FromAmount,
            OrderIndex = step.StepIndex
        }).ToList();

        return new FlashLoanTransaction
        {
            TxHash = arbTx.TxHash,
            BlockNumber = arbTx.LedgerIndex,
            Timestamp = arbTx.Timestamp,
            ChainId = 0, // XRP Ledger doesn't have chain ID like EVM
            Protocol = "XRP_DEX_Arbitrage",
            ContractAddress = arbTx.Account, // Use account as "contract"
            Actions = actions,
            ProfitToken = arbTx.StartCurrency,
            ProfitAmount = arbTx.ProfitAmount,
            GasUsed = 0, // XRP uses fixed fees, not gas
            GasPriceGwei = 0,
            EffectiveGasCostUsd = arbTx.TransactionCostXRP * XRP_PRICE_USD,
            IsProfitable = arbTx.IsProfitable
        };
    }

    private string ParseCurrency(object? amountObj)
    {
        if (amountObj == null) return "XRP";

        if (amountObj is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return "XRP"; // Drops (native XRP)

            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("currency", out var currency))
                    return currency.GetString() ?? "Unknown";
            }
        }

        return "XRP";
    }

    private decimal ParseAmount(object? amountObj)
    {
        if (amountObj == null) return 0;

        if (amountObj is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                // XRP drops
                if (decimal.TryParse(element.GetString(), out var drops))
                    return drops / 1_000_000m;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("value", out var value))
                {
                    if (decimal.TryParse(value.GetString(), out var amount))
                        return amount;
                }
            }
        }

        return 0;
    }

    private decimal ParseFee(string? fee)
    {
        if (string.IsNullOrEmpty(fee)) return 0;
        if (decimal.TryParse(fee, out var drops))
            return drops / 1_000_000m; // Convert drops to XRP
        return 0;
    }

    private decimal CalculateEndAmount(List<XRPTradeStep> steps)
    {
        // Trace through the trade sequence
        var amount = steps[0].FromAmount;

        foreach (var step in steps)
        {
            amount = amount * step.ExchangeRate;
        }

        return amount;
    }

    private async Task<T?> SendRpcRequestAsync<T>(object request, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                _blockchainConfig.XRPLedger.RpcUrl,
                request,
                ct);

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[XRPCollector] RPC request failed");
            return default;
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}

// XRP RPC response models
internal class LedgerResponse
{
    [JsonPropertyName("result")]
    public LedgerResult? Result { get; set; }
}

internal class LedgerResult
{
    [JsonPropertyName("ledger_index")]
    public long LedgerIndex { get; set; }
}

internal class LedgerWithTransactionsResponse
{
    [JsonPropertyName("result")]
    public LedgerWithTransactionsResult? Result { get; set; }
}

internal class LedgerWithTransactionsResult
{
    [JsonPropertyName("ledger")]
    public LedgerData? Ledger { get; set; }
}

internal class LedgerData
{
    [JsonPropertyName("transactions")]
    public List<XRPTransaction>? Transactions { get; set; }

    [JsonPropertyName("close_time")]
    public long CloseTime { get; set; }
}

internal class XRPTransaction
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("TransactionType")]
    public string? TransactionType { get; set; }

    [JsonPropertyName("Account")]
    public string? Account { get; set; }

    [JsonPropertyName("Sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("Fee")]
    public string? Fee { get; set; }

    [JsonPropertyName("TakerGets")]
    public object? TakerGets { get; set; }

    [JsonPropertyName("TakerPays")]
    public object? TakerPays { get; set; }
}
