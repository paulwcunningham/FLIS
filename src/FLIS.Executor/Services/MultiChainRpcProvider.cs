using Nethereum.Web3;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;

namespace FLIS.Executor.Services;

public interface IMultiChainRpcProvider
{
    IWeb3 GetWeb3(string chainName);
    NodeConfig GetNodeConfig(string chainName);
}

public class MultiChainRpcProvider : IMultiChainRpcProvider
{
    private readonly Dictionary<string, IWeb3> _web3Clients = new();
    private readonly Dictionary<string, NodeConfig> _nodeConfigs = new();

    public MultiChainRpcProvider(IConfiguration configuration)
    {
        var nodes = configuration.GetSection("Nodes").Get<List<NodeConfig>>()
            ?? throw new InvalidOperationException("No nodes configured");

        foreach (var node in nodes)
        {
            _web3Clients[node.ChainName] = new Web3(node.RpcUrl);
            _nodeConfigs[node.ChainName] = node;
        }
    }

    public IWeb3 GetWeb3(string chainName)
    {
        if (!_web3Clients.TryGetValue(chainName, out var web3))
        {
            throw new ArgumentException($"No Web3 client configured for chain: {chainName}");
        }
        return web3;
    }

    public NodeConfig GetNodeConfig(string chainName)
    {
        if (!_nodeConfigs.TryGetValue(chainName, out var config))
        {
            throw new ArgumentException($"No node config for chain: {chainName}");
        }
        return config;
    }
}
