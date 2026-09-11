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

        public async Task RepublishAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(GargeApiClient.Authorized);
                var response = await client.GetAsync($"{_apiBaseUrl}/api/sensors/device-settings", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to fetch device settings. Status: {StatusCode}", response.StatusCode);
                    return;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var settings = JsonSerializer.Deserialize<List<DeviceSettingsEvent>>(json, JsonOptions) ?? new List<DeviceSettingsEvent>();

                _logger.LogInformation("Republishing settings for {Count} devices.", settings.Count);
                foreach (var entry in settings)
                {
                    await _mqttService.HandleDeviceSettingsEventAsync(entry);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error republishing device settings.");
            }
        }
    }
}
