using System.Text.Json;
using garge_operator.Models;
using Microsoft.Extensions.Options;

namespace garge_operator.Services
{
    public class DeviceSettingsSync
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMqttService _mqttService;
        private readonly ILogger<DeviceSettingsSync> _logger;
        private readonly string _apiBaseUrl;

        public DeviceSettingsSync(
            IHttpClientFactory httpClientFactory,
            IMqttService mqttService,
            IOptions<ApiOptions> apiOptions,
            ILogger<DeviceSettingsSync> logger)
        {
            _httpClientFactory = httpClientFactory;
            _mqttService = mqttService;
            _logger = logger;
            _apiBaseUrl = apiOptions.Value.BaseUrl;
        }

        /// <summary>
        /// The reconnect handlers await this, and the shared client carries the 100 s
        /// default timeout, so an unresponsive API would stall every reconnect for that
        /// long. Devices keep their retained settings meanwhile; a late republish costs
        /// nothing, a blocked reconnect does.
        /// </summary>
        internal static readonly TimeSpan RepublishTimeout = TimeSpan.FromSeconds(15);

        public async Task RepublishAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RepublishTimeout);
                var cancellationTokenWithTimeout = timeout.Token;

                var client = _httpClientFactory.CreateClient(GargeApiClient.Authorized);
                var response = await client.GetAsync($"{_apiBaseUrl}/api/sensors/device-settings", cancellationTokenWithTimeout);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to fetch device settings. Status: {StatusCode}", response.StatusCode);
                    return;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationTokenWithTimeout);
                var settings = JsonSerializer.Deserialize<List<DeviceSettingsEvent>>(json, JsonOptions) ?? new List<DeviceSettingsEvent>();

                _logger.LogInformation("Republishing settings for {Count} devices.", settings.Count);
                foreach (var entry in settings)
                {
                    await _mqttService.HandleDeviceSettingsEventAsync(entry);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Republishing device settings timed out after {Timeout}.", RepublishTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error republishing device settings.");
            }
        }
    }
}
