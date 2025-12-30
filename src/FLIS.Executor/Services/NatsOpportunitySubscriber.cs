using NATS.Client;
using System.Text;
using System.Text.Json;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FLIS.Executor.Services;

public class NatsOpportunitySubscriber : BackgroundService
{
    private readonly ITransactionManager _transactionManager;
    private readonly ILogger<NatsOpportunitySubscriber> _logger;
    private readonly string _natsUrl;
    private readonly string _subject;
    private IConnection? _connection;

    public NatsOpportunitySubscriber(
        ITransactionManager transactionManager,
        IConfiguration configuration,
        ILogger<NatsOpportunitySubscriber> logger)
    {
        _transactionManager = transactionManager;
        _logger = logger;
        _natsUrl = configuration["Nats:Url"]
            ?? throw new InvalidOperationException("NATS URL not configured");
        _subject = configuration["Nats:OpportunitySubject"]
            ?? throw new InvalidOperationException("NATS subject not configured");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory();
        _connection = factory.CreateConnection(_natsUrl);

        _logger.LogInformation("Connected to NATS at {Url}, subscribing to {Subject}", _natsUrl, _subject);

        // Pass the NATS connection to TransactionManager so it can publish results
        _transactionManager.SetNatsConnection(_connection);

        var subscription = _connection.SubscribeAsync(_subject, async (sender, args) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(args.Message.Data);
                _logger.LogDebug("Received NATS message: {Json}", json);

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var opportunity = JsonSerializer.Deserialize<FlashLoanOpportunity>(json, options);

                if (opportunity != null)
                {
                    _logger.LogInformation(
                        "Processing opportunity {Id}: {Strategy} on {Chain}",
                        opportunity.Id,
                        opportunity.Strategy,
                        opportunity.ChainName
                    );

                    await _transactionManager.ProcessOpportunityAsync(opportunity);
                }
                else
                {
                    _logger.LogWarning("Failed to deserialize opportunity from NATS message");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing NATS message: {Message}", ex.Message);
            }
        });

        _logger.LogInformation("Subscription active, waiting for opportunities...");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override void Dispose()
    {
        _connection?.Close();
        base.Dispose();
    }
}
