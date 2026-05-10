using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;

namespace garge_operator.Services
{
    public class OperatorHubClient : BackgroundService
    {
        private const string SwitchEventName = "switch";

        private readonly IConfiguration _configuration;
        private readonly IMqttService _mqttService;
        private readonly ILogger<OperatorHubClient> _logger;
        private HubConnection? _connection;

        public OperatorHubClient(
            IConfiguration configuration,
            IMqttService mqttService,
            ILogger<OperatorHubClient> logger)
        {
            _configuration = configuration;
            _mqttService = mqttService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var apiBase = _configuration["Api:BaseUrl"]
                          ?? throw new InvalidOperationException("Api:BaseUrl not configured");
            var hubUrl = $"{apiBase.TrimEnd('/')}/hubs/devices";

            _connection = new HubConnectionBuilder()
                .WithUrl(hubUrl, opts =>
                {
                    opts.AccessTokenProvider = async () => await _mqttService.GetJwtTokenAsync();
                })
                .WithAutomaticReconnect()
                .Build();

            _connection.On<WebhookPayload>(SwitchEventName, async payload =>
            {
                try
                {
                    await _mqttService.HandleWebhookDataAsync(payload);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OperatorHubClient: failed to handle switch event");
                }
            });

            _connection.Reconnecting += error =>
            {
                _logger.LogWarning(error, "OperatorHubClient: reconnecting");
                return Task.CompletedTask;
            };
            _connection.Reconnected += connectionId =>
            {
                _logger.LogInformation("OperatorHubClient: reconnected ({ConnectionId})", connectionId);
                return Task.CompletedTask;
            };
            _connection.Closed += error =>
            {
                _logger.LogWarning(error, "OperatorHubClient: connection closed");
                return Task.CompletedTask;
            };

            // Retry start until success or shutdown.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _connection.StartAsync(stoppingToken);
                    _logger.LogInformation("OperatorHubClient: connected to {HubUrl}", hubUrl);
                    break;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "OperatorHubClient: initial connect failed, retrying in 5s");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_connection != null)
            {
                await _connection.DisposeAsync();
            }
            await base.StopAsync(cancellationToken);
        }
    }
}
