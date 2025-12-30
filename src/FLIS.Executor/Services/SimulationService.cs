using Nethereum.Web3;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using System.Numerics;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FLIS.Executor.Services;

public interface ISimulationService
{
    Task<(bool IsProfitable, decimal EstimatedProfit)> SimulateAsync(
        FlashLoanOpportunity opportunity,
        GasBidResult gasBid);
}

public class SimulationService : ISimulationService
{
    private readonly IMultiChainRpcProvider _rpcProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SimulationService> _logger;

    public SimulationService(
        IMultiChainRpcProvider rpcProvider,
        IConfiguration configuration,
        ILogger<SimulationService> logger)
    {
        _rpcProvider = rpcProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<(bool IsProfitable, decimal EstimatedProfit)> SimulateAsync(
        FlashLoanOpportunity opportunity,
        GasBidResult gasBid)
    {
        try
        {
            // Get the Web3 instance for the chain
            var web3 = _rpcProvider.GetWeb3(opportunity.ChainName);

            // Get the contract configuration
            var contractConfigs = _configuration.GetSection("SmartContracts")
                .Get<List<SmartContractConfig>>();

            var contractConfig = contractConfigs?
                .FirstOrDefault(c => c.ChainName == opportunity.ChainName);

            if (contractConfig == null)
            {
                _logger.LogError("No contract configuration found for chain {ChainName}", opportunity.ChainName);
                return (false, 0);
            }

            // Load the contract
            var contract = web3.Eth.GetContract(contractConfig.Abi, contractConfig.ContractAddress);

            // Determine which function to call based on opportunity type
            Function function;
            object[] parameters;

            if (opportunity.Strategy == "CrossDex" && opportunity.SourceDex != null && opportunity.TargetDex != null)
            {
                function = contract.GetFunction("executeCrossDexArbitrage");
                parameters = new object[]
                {
                    opportunity.Asset,                              // address asset
                    Web3.Convert.ToWei(opportunity.Amount),         // uint256 amount
                    opportunity.SourceDex,                          // address sourceDex
                    opportunity.TargetDex,                          // address targetDex
                    Web3.Convert.ToWei(opportunity.MinProfit)       // uint256 minProfit
                };
            }
            else if (opportunity.Strategy == "MultiHop" && opportunity.Path != null)
            {
                function = contract.GetFunction("executeMultiHopArbitrage");
                parameters = new object[]
                {
                    opportunity.Asset,                              // address asset
                    Web3.Convert.ToWei(opportunity.Amount),         // uint256 amount
                    opportunity.Path.Split(','),                    // address[] path
                    Web3.Convert.ToWei(opportunity.MinProfit)       // uint256 minProfit
                };
            }
            else
            {
                _logger.LogWarning("Unknown strategy or missing parameters: {Strategy}", opportunity.Strategy);
                return (false, 0);
            }

            // Convert gas price from Gwei to Wei
            var gasPriceWei = Web3.Convert.ToWei(gasBid.GasPriceGwei, Nethereum.Util.UnitConversion.EthUnit.Gwei);

            // Simulate the transaction using eth_call
            var callInput = function.CreateCallInput(parameters);
            callInput.Gas = new HexBigInteger(gasBid.GasLimit);
            callInput.GasPrice = new HexBigInteger(gasPriceWei);
            callInput.From = contractConfig.ContractAddress; // Use contract address as caller

            // Execute the simulation
            var result = await web3.Eth.Transactions.Call.SendRequestAsync(callInput);

            // If we get here without exception, the transaction would succeed
            // Calculate if it's profitable after gas costs
            var gasCostWei = gasPriceWei * gasBid.GasLimit;
            var gasCostEth = Web3.Convert.FromWei(gasCostWei);

            // TODO: Get real-time ETH price - for now using the gas bid estimate
            var gasCostUsd = gasBid.EstimatedCostUsd;

            var flashLoanFee = opportunity.Amount * 0.0009m; // 0.09% Aave flash loan fee
            var expectedProfitUsd = opportunity.MinProfit;
            var totalCost = gasCostUsd + flashLoanFee;
            var netProfitUsd = expectedProfitUsd - totalCost;

            _logger.LogInformation(
                "Simulation successful for {ChainName} {Strategy}: Expected={ExpectedProfit:C}, Gas={GasCost:C}, FlashLoanFee={FlashLoanFee:C}, Net={NetProfit:C}",
                opportunity.ChainName,
                opportunity.Strategy,
                expectedProfitUsd,
                gasCostUsd,
                flashLoanFee,
                netProfitUsd
            );

            // Only execute if net profit is positive
            return (netProfitUsd > 0, netProfitUsd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simulation failed for {ChainName} {Strategy}",
                opportunity.ChainName, opportunity.Strategy);
            return (false, 0);
        }
    }
}
