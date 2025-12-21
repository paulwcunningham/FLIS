namespace FLIS.DataCollector.Models;

/// <summary>
/// EVM chain configuration
/// </summary>
public record EVMChainConfig
{
    public int ChainId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string RpcUrl { get; init; } = string.Empty;
    public int BlockTime { get; init; } = 12;
}

/// <summary>
/// XRP Ledger configuration
/// </summary>
public record XRPLedgerConfig
{
    public string RpcUrl { get; init; } = string.Empty;
    public string WebSocketUrl { get; init; } = string.Empty;
}

/// <summary>
/// Blockchain configuration section
/// </summary>
public record BlockchainConfig
{
    public List<EVMChainConfig> EVMChains { get; init; } = new();
    public XRPLedgerConfig XRPLedger { get; init; } = new();
}

/// <summary>
/// Flash loan contract addresses per chain
/// </summary>
public record ChainContracts
{
    public string? AaveV3Pool { get; init; }
    public string? BalancerVault { get; init; }
    public string? UniswapV3Factory { get; init; }
    public string? dYdXSoloMargin { get; init; }
    public string? PancakeSwapV3Factory { get; init; }
    public string? VenusUnitroller { get; init; }

    /// <summary>
    /// Get all contract addresses as a list
    /// </summary>
    public IEnumerable<string> GetAllAddresses()
    {
        var addresses = new List<string>();
        if (!string.IsNullOrEmpty(AaveV3Pool)) addresses.Add(AaveV3Pool.ToLowerInvariant());
        if (!string.IsNullOrEmpty(BalancerVault)) addresses.Add(BalancerVault.ToLowerInvariant());
        if (!string.IsNullOrEmpty(UniswapV3Factory)) addresses.Add(UniswapV3Factory.ToLowerInvariant());
        if (!string.IsNullOrEmpty(dYdXSoloMargin)) addresses.Add(dYdXSoloMargin.ToLowerInvariant());
        if (!string.IsNullOrEmpty(PancakeSwapV3Factory)) addresses.Add(PancakeSwapV3Factory.ToLowerInvariant());
        if (!string.IsNullOrEmpty(VenusUnitroller)) addresses.Add(VenusUnitroller.ToLowerInvariant());
        return addresses;
    }
}

/// <summary>
/// Flash loan contracts configuration
/// </summary>
public record FlashLoanContractsConfig
{
    public ChainContracts Ethereum { get; init; } = new();
    public ChainContracts Polygon { get; init; } = new();
    public ChainContracts Arbitrum { get; init; } = new();
    public ChainContracts Optimism { get; init; } = new();
    public ChainContracts BSC { get; init; } = new();

    /// <summary>
    /// Get contracts for a specific chain
    /// </summary>
    public ChainContracts? GetContractsForChain(string chainName)
    {
        return chainName.ToLowerInvariant() switch
        {
            "ethereum" => Ethereum,
            "polygon" => Polygon,
            "arbitrum" => Arbitrum,
            "optimism" => Optimism,
            "bsc" => BSC,
            _ => null
        };
    }
}

/// <summary>
/// Collector settings
/// </summary>
public record CollectorSettings
{
    public int BatchSize { get; init; } = 50;
    public int PollingIntervalSeconds { get; init; } = 5;
    public int MaxBlocksPerBatch { get; init; } = 10;
    public bool EnableEVMCollector { get; init; } = true;
    public bool EnableXRPCollector { get; init; } = true;
}
