using garge_operator.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace garge_operator.Services
{
    /// <summary>
    /// Maintains a SignalR connection to garge-api's DeviceHub.
    /// Supervision strategy:
    ///   - Automatic reconnect handles short interruptions (about 30 seconds, 4 attempts).
    ///   - When the connection closes after automatic reconnect is exhausted, a fresh
    ///     StartAsync is scheduled with a longer back-off so a multi-minute API outage
    ///     still recovers without restarting the operator process.
    /// </summary>
    public class OperatorHubClient : BackgroundService
    {
        private const string SwitchEventName = "switch";
        private const string DeviceSettingsEventName = "deviceSettings";
        private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ClosedRestartDelay = TimeSpan.FromSeconds(60);

        private readonly string _apiBaseUrl;
        private readonly IMqttService _mqttService;
        private readonly DeviceSettingsSync _deviceSettingsSync;
        private readonly ILogger<OperatorHubClient> _logger;
        private HubConnection? _connection;
        private CancellationToken _stoppingToken;

        public OperatorHubClient(
            IOptions<ApiOptions> apiOptions,
            IMqttService mqttService,
            DeviceSettingsSync deviceSettingsSync,
            ILogger<OperatorHubClient> logger)
        {
            _apiBaseUrl = apiOptions.Value.BaseUrl;
            _mqttService = mqttService;
            _deviceSettingsSync = deviceSettingsSync;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stoppingToken = stoppingToken;

            var hubUrl = $"{_apiBaseUrl.TrimEnd('/')}/hubs/devices";

            _connection = new HubConnectionBuilder()
                .WithUrl(hubUrl, opts =>
                {
                    opts.AccessTokenProvider = async () => await _mqttService.GetJwtTokenAsync();
                })
                .WithAutomaticReconnect()
                .Build();

            _connection.On<SwitchEvent>(SwitchEventName, async evt =>
            {
                try
                {
                    await _mqttService.HandleSwitchEventAsync(evt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OperatorHubClient: failed to handle switch event");
                }
            });

            _connection.On<DeviceSettingsEvent>(DeviceSettingsEventName, async evt =>
            {
                try
                {
                    await _mqttService.HandleDeviceSettingsEventAsync(evt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OperatorHubClient: failed to handle device settings event");
                }
            });

            _connection.Reconnecting += error =>
            {
                _logger.LogWarning(error, "OperatorHubClient: reconnecting");
                return Task.CompletedTask;
            };
            _connection.Reconnected += OnReconnectedAsync;
            _connection.Closed += async error =>
            {
                if (_stoppingToken.IsCancellationRequested) return;
                _logger.LogWarning(error,
                    "OperatorHubClient: connection closed past auto-reconnect, restarting in {Delay}",
                    ClosedRestartDelay);
                try
                {
                    await Task.Delay(ClosedRestartDelay, _stoppingToken);
                    await StartWithRetriesAsync(hubUrl);
                }
                catch (OperationCanceledException) { }
            };

            await StartWithRetriesAsync(hubUrl);

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task StartWithRetriesAsync(string hubUrl)
        {
            while (!_stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_connection!.State == HubConnectionState.Connected) return;
                    await _connection!.StartAsync(_stoppingToken);
                }
                catch (Exception ex) when (!_stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "OperatorHubClient: connect failed, retrying in {Delay}",
                        InitialRetryDelay);
                    await Task.Delay(InitialRetryDelay, _stoppingToken);
                    continue;
                }

                await OnConnectedAsync(hubUrl);
                return;
            }
        }

        internal async Task OnConnectedAsync(string hubUrl)
        {
            _logger.LogInformation("OperatorHubClient: connected to {HubUrl}", hubUrl);
            await _deviceSettingsSync.RepublishAsync(_stoppingToken);
        }

        internal async Task OnReconnectedAsync(string? connectionId)
        {
            _logger.LogInformation("OperatorHubClient: reconnected ({ConnectionId})", connectionId);
            await _deviceSettingsSync.RepublishAsync(_stoppingToken);
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
