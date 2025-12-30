using Nethereum.Web3;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Numerics;

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
            var contractConfig = _configuration.GetSection("SmartContracts")
                .Get<List<SmartContractConfig>>()
                ?.FirstOrDefault(c => c.ChainName == opportunity.ChainName);

            if (contractConfig == null)
            {
                _logger.LogError("No contract configuration found for chain {ChainName}", opportunity.ChainName);
                return (false, 0);
            }

            // Load the contract
            var contract = web3.Eth.GetContract(contractConfig.Abi, contractConfig.ContractAddress);

            // Determine which function to call based on opportunity type
            Function? function = null;
            object[]? parameters = null;

            if (opportunity.Strategy == "CrossDex" &&
                !string.IsNullOrEmpty(opportunity.SourceDex) &&
                !string.IsNullOrEmpty(opportunity.TargetDex))
            {
                function = contract.GetFunction("executeCrossDexArbitrage");
                parameters = new object[]
                {
                    opportunity.Asset,
                    Web3.Convert.ToWei(opportunity.Amount),
                    opportunity.SourceDex,
                    opportunity.TargetDex,
                    Web3.Convert.ToWei(opportunity.MinProfit)
                };
            }
            else if (opportunity.Strategy == "MultiHop" && !string.IsNullOrEmpty(opportunity.Path))
            {
                function = contract.GetFunction("executeMultiHopArbitrage");
                var pathAddresses = opportunity.Path.Split(',', StringSplitOptions.RemoveEmptyEntries);

                parameters = new object[]
                {
                    opportunity.Asset,
                    Web3.Convert.ToWei(opportunity.Amount),
                    pathAddresses,
                    Web3.Convert.ToWei(opportunity.MinProfit)
                };
            }
            else
            {
                _logger.LogWarning("Invalid strategy or missing parameters: {Strategy}", opportunity.Strategy);
                return (false, 0);
            }

            // Build the call input for eth_call simulation
            var callInput = new CallInput
            {
                To = contractConfig.ContractAddress,
                Data = function.GetData(parameters),
                Gas = new HexBigInteger(gasBid.GasLimit),
                GasPrice = new HexBigInteger(Web3.Convert.ToWei(gasBid.GasPriceGwei, Nethereum.Util.UnitConversion.EthUnit.Gwei))
            };

            // Execute the simulation using eth_call
            _logger.LogDebug("Simulating transaction on {Chain} for {Strategy}",
                opportunity.ChainName, opportunity.Strategy);

            var result = await web3.Eth.Transactions.Call.SendRequestAsync(callInput);

            // If we get here without exception, the transaction would succeed
            _logger.LogDebug("Simulation succeeded, result: {Result}", result);

            // Calculate profitability after gas costs
            var gasCostWei = Web3.Convert.ToWei(gasBid.GasPriceGwei, Nethereum.Util.UnitConversion.EthUnit.Gwei) * gasBid.GasLimit;
            var gasCostEth = Web3.Convert.FromWei(gasCostWei);

            // Gas cost in USD (using EstimatedCostUsd from gas bid)
            var gasCostUsd = gasBid.EstimatedCostUsd;

            // Flash loan fee (typically 0.09% = 0.0009)
            var flashLoanFeeUsd = opportunity.Amount * 0.0009m;

            var totalCost = gasCostUsd + flashLoanFeeUsd;
            var expectedProfit = opportunity.MinProfit;
            var netProfit = expectedProfit - totalCost;

            _logger.LogInformation(
                "Simulation result for {Chain} {Strategy}: Expected={Expected:F4}, GasCost={GasCost:F4}, FlashLoanFee={Fee:F4}, Net={Net:F4}, Profitable={IsProfitable}",
                opportunity.ChainName,
                opportunity.Strategy,
                expectedProfit,
                gasCostUsd,
                flashLoanFeeUsd,
                netProfit,
                netProfit > 0
            );

            return (netProfit > 0, netProfit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simulation failed for {Chain} {Strategy}: {Message}",
                opportunity.ChainName, opportunity.Strategy, ex.Message);
            return (false, 0);
        }
    }
}
