namespace FLIS.Executor.Models;

public class FlashLoanOpportunity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChainName { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Strategy { get; set; } = string.Empty; // "CrossDex" or "MultiHop"
    public string? IntermediateToken { get; set; }
    public int? UniPoolFee { get; set; }
    public bool? UniFirst { get; set; }
    public string? Path { get; set; } // For multi-hop (comma-separated addresses)
    public string? SourceDex { get; set; } // For CrossDex strategy
    public string? TargetDex { get; set; } // For CrossDex strategy
    public decimal MinProfit { get; set; }
    public int Deadline { get; set; }
}
