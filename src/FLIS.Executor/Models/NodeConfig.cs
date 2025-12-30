namespace FLIS.Executor.Models;

public record NodeConfig(
    string ChainName,
    string RpcUrl,
    int ChainId
);
