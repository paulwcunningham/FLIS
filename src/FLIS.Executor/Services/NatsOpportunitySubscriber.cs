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

        var subscription = _connection.SubscribeAsync(_subject, async (sender, args) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(args.Message.Data);
                var opportunity = JsonSerializer.Deserialize<FlashLoanOpportunity>(json);

                if (opportunity != null)
                {
                    await _transactionManager.ProcessOpportunityAsync(opportunity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing NATS message");
            }
        });

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override void Dispose()
    {
        _connection?.Close();
        base.Dispose();
    }
}
