namespace FLIS.Executor.Models;

public record SmartContractConfig(
    string ChainName,
    string ContractAddress,
    string Abi
);
