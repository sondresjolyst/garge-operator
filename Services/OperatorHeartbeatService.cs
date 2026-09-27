using garge_operator.Models;
using Microsoft.Extensions.Options;

namespace garge_operator.Services
{
    public class OperatorHeartbeatService : BackgroundService
    {
        private readonly IMqttService _mqttService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OperatorHeartbeatService> _logger;
        private readonly string _apiBaseUrl;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _connectPollInterval;

        public OperatorHeartbeatService(
            IMqttService mqttService,
            IHttpClientFactory httpClientFactory,
            IOptions<ApiOptions> apiOptions,
            ILogger<OperatorHeartbeatService> logger)
            : this(mqttService, httpClientFactory, apiOptions, logger, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1))
        {
        }

        internal OperatorHeartbeatService(
            IMqttService mqttService,
            IHttpClientFactory httpClientFactory,
            IOptions<ApiOptions> apiOptions,
            ILogger<OperatorHeartbeatService> logger,
            TimeSpan interval,
            TimeSpan connectPollInterval)
        {
            _mqttService = mqttService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _apiBaseUrl = apiOptions.Value.BaseUrl;
            _interval = interval;
            _connectPollInterval = connectPollInterval;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!_mqttService.IsConnected)
            {
                await Task.Delay(_connectPollInterval, stoppingToken);
            }

            using var timer = new PeriodicTimer(_interval);

            do
            {
                await SendHeartbeatAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        internal async Task SendHeartbeatAsync(CancellationToken stoppingToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(GargeApiClient.Authorized);
                var response = await HttpJson.PostJsonAsync(client, $"{_apiBaseUrl}/api/operator/heartbeat", new { mqttConnected = _mqttService.IsConnected }, stoppingToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Heartbeat failed with status {StatusCode}.", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not send heartbeat.");
            }
        }
    }
}
