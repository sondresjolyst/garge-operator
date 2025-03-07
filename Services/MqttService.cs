using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using System.Text;
using System.Text.Json;
using garge_operator.Models;
using MQTTnet.Packets;

public class MqttService
{
    private readonly IManagedMqttClient _mqttClient;
    private readonly ManagedMqttClientOptions _mqttClientOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttService> _logger;
    private readonly string _apiBaseUrl;
    private List<Sensor> _sensors;
    private Dictionary<string, string> _sensorUniqIds;

    public MqttService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<MqttService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _sensorUniqIds = new Dictionary<string, string>();
        _sensors = new List<Sensor>();

        // Safely read broker & port
        var broker = configuration["Mqtt:Broker"];
        if (string.IsNullOrWhiteSpace(broker))
        {
            throw new ArgumentException("Mqtt:Broker not set in configuration.");
        }

        var portString = configuration["Mqtt:Port"];
        if (!int.TryParse(portString, out var port))
        {
            throw new ArgumentException("Mqtt:Port is invalid or not set in configuration.");
        }

        // Safely read API BaseUrl
        var baseUrl = configuration["Api:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Api:BaseUrl not set in configuration.");
        }
        _apiBaseUrl = baseUrl;

        // Safely read username & password
        var username = configuration["Mqtt:Username"];
        var password = configuration["Mqtt:Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Mqtt:Username or Mqtt:Password not set in configuration.");
        }

        // Create the client
        var factory = new MqttFactory();
        _mqttClient = factory.CreateManagedMqttClient();

        // Build the client options
        var clientOptions = new MqttClientOptionsBuilder()
            .WithClientId($"garge-operator-{Guid.NewGuid()}")
            .WithTcpServer(broker, port)
            .WithCredentials(username, password)
            .Build();

        _mqttClientOptions = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(clientOptions)
            .Build();
    }

    public async Task ConnectAsync()
    {
        // Ensure login is successful before connecting to MQTT broker
        var token = await GetJwtTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Cannot connect to MQTT broker without a valid JWT token.");
            return;
        }

        // Get all sensors from the API
        _sensors = await GetAllSensorsAsync(token);

        // Subscribe to the message received event
        _mqttClient.ApplicationMessageReceivedAsync += HandleReceivedMessage;

        // Log connection status
        _mqttClient.ConnectedAsync += async e =>
        {
            _logger.LogInformation("Connected to MQTT broker.");
            // Subscribe to all device config topics
            var topic = "homeassistant/+/+/config";
            await _mqttClient.SubscribeAsync(new List<MqttTopicFilter>
            {
                new MqttTopicFilterBuilder().WithTopic(topic).Build()
            });
            _logger.LogInformation($"Subscribed to topic: {topic}");
        };

        _mqttClient.DisconnectedAsync += async e =>
        {
            _logger.LogWarning("Disconnected from MQTT broker.");
            if (e.Exception != null)
            {
                _logger.LogError(e.Exception, "MQTT client disconnected due to an error.");
            }
            else
            {
                _logger.LogWarning("MQTT client disconnected.");
            }

            // Attempt to reconnect
            await Task.Delay(TimeSpan.FromSeconds(5));
            _logger.LogInformation("Reconnecting to MQTT broker...");
            await _mqttClient.StartAsync(_mqttClientOptions);
        };

        // Connect to the broker
        _logger.LogInformation("Connecting to MQTT broker...");
        await _mqttClient.StartAsync(_mqttClientOptions);
    }

    private async Task HandleReceivedMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            // Ensure ApplicationMessage is not null
            if (e.ApplicationMessage == null)
            {
                _logger.LogError("Received a message with no ApplicationMessage.");
                return;
            }

            // Ensure Topic is not null
            if (e.ApplicationMessage.Topic == null)
            {
                _logger.LogError("Received a message with no topic.");
                return;
            }

            // Log received message
            _logger.LogInformation($"Received message on topic: {e.ApplicationMessage.Topic}");

            // Convert the incoming payload
            var payloadSegment = e.ApplicationMessage.PayloadSegment;
            if (payloadSegment.Array == null)
            {
                _logger.LogError("Received a message with no payload.");
                return;
            }

            var payload = payloadSegment.Array;
            var json = Encoding.UTF8.GetString(payload, payloadSegment.Offset, payloadSegment.Count);

            // Log the received JSON payload
            _logger.LogInformation($"Received JSON payload: {json}");

            // Check if the message is a device config
            if (e.ApplicationMessage.Topic.StartsWith("homeassistant/") && e.ApplicationMessage.Topic.EndsWith("/config"))
            {
                var deviceConfig = JsonSerializer.Deserialize<DeviceConfig>(json);
                if (deviceConfig != null)
                {
                    await HandleDeviceConfig(deviceConfig);
                }
            }
            else
            {
                // Handle sensor data
                var sensorData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                var topicParts = e.ApplicationMessage.Topic.Split('/');
                if (topicParts.Length >= 3)
                {
                    var sensorName = topicParts[2];

                    if (sensorData != null)
                    {
                        foreach (var kvp in sensorData)
                        {
                            var key = kvp.Key;
                            var value = kvp.Value?.ToString();
                            if (value != null)
                            {
                                var sensorKey = $"{sensorName}_{key}";
                                if (_sensorUniqIds.TryGetValue(sensorKey, out var uniqId))
                                {
                                    // Check if the key matches the dev_cla
                                    var sensor = _sensors.FirstOrDefault(s => s.Name == uniqId);
                                    if (sensor != null && key.Equals(sensor.Type, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _logger.LogInformation($"Sending data to API for sensor {uniqId} with key: {key} and value: {value}");
                                        await SendDataToApi(uniqId, key, value);
                                    }
                                    else
                                    {
                                        _logger.LogWarning($"Key {key} does not match the sensor type {sensor?.Type} for sensor {uniqId}");
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning($"No uniq_id found for sensor {sensorKey}");
                                }
                            }
                            else
                            {
                                _logger.LogWarning($"Value for {key} is null or empty.");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Sensor data is null.");
                    }
                }
                else
                {
                    _logger.LogWarning("Topic does not contain enough parts to extract sensor name.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling received message.");
        }
    }

    private async Task HandleDeviceConfig(DeviceConfig deviceConfig)
    {
        try
        {
            // Log the current sensors for debugging
            _logger.LogInformation("Current sensors: " + string.Join(", ", _sensors.Select(s => s.Name)));

            // Check if the sensor exists in the list
            var sensorExists = _sensors.Any(s => s.Name == deviceConfig.UniqId);
            if (!sensorExists)
            {
                // Create a new sensor
                var createSensorData = new Sensor
                {
                    Name = deviceConfig.UniqId,
                    Type = deviceConfig.DevCla
                };
                var created = await TryCreateSensor(createSensorData);

                if (created)
                {
                    // Add the new sensor to the list
                    _sensors.Add(createSensorData);
                }
            }

            // Store the uniq_id for the sensor type
            _sensorUniqIds[deviceConfig.UniqId] = deviceConfig.UniqId;
            _logger.LogInformation($"Stored uniq_id for sensor {deviceConfig.UniqId}");

            // Log the current uniq_id mappings for debugging
            _logger.LogInformation("Current uniq_id mappings: " + string.Join(", ", _sensorUniqIds.Select(kvp => $"{kvp.Key}: {kvp.Value}")));

            // Subscribe to the sensor's state topic
            await _mqttClient.SubscribeAsync(new List<MqttTopicFilter>
            {
                new MqttTopicFilterBuilder().WithTopic(deviceConfig.StatT).Build()
            });
            _logger.LogInformation($"Subscribed to sensor state topic: {deviceConfig.StatT}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling device config.");
        }
    }

    private async Task<bool> TryCreateSensor(Sensor createSensorData)
    {
        try
        {
            var token = await GetJwtTokenAsync();
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var content = new StringContent(JsonSerializer.Serialize(createSensorData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{_apiBaseUrl}/api/Sensor", content);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation($"Successfully created sensor: {createSensorData.Name}");
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogWarning($"Sensor already exists: {createSensorData.Name}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating sensor.");
            return false;
        }
    }

    private async Task<List<Sensor>> GetAllSensorsAsync(string token)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await client.GetAsync($"{_apiBaseUrl}/api/Sensor");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to retrieve sensors. Status code: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                return new List<Sensor>();
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation($"Sensors response: {responseContent}");

            var jsonDocument = JsonDocument.Parse(responseContent);
            var sensors = jsonDocument.RootElement.GetProperty("$values").Deserialize<List<Sensor>>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (sensors == null || !sensors.Any())
            {
                _logger.LogWarning("No sensors found in the API response.");
            }
            else
            {
                _logger.LogInformation($"Deserialized sensors: {string.Join(", ", sensors.Select(s => s.Name))}");
            }

            return sensors ?? new List<Sensor>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving sensors.");
            return new List<Sensor>();
        }
    }

    private async Task CreateSensor(Sensor createSensorData)
    {
        try
        {
            var token = await GetJwtTokenAsync();
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var content = new StringContent(JsonSerializer.Serialize(createSensorData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{_apiBaseUrl}/api/Sensor", content);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation($"Successfully created sensor: {createSensorData.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating sensor.");
        }
    }

    private async Task SendDataToApi(string uniqId, string key, string value)
    {
        try
        {
            _logger.LogInformation($"Preparing to send data for sensor {uniqId} to API with key: {key}");

            var token = await GetJwtTokenAsync();
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var content = new StringContent(JsonSerializer.Serialize(new { key, value }), Encoding.UTF8, "application/json");

            _logger.LogInformation($"Sending data to API: {_apiBaseUrl}/api/Sensor/name/{uniqId}/data");
            var response = await client.PostAsync($"{_apiBaseUrl}/api/Sensor/name/{uniqId}/data", content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"Successfully sent data for sensor {uniqId} to API.");
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to send data for sensor {uniqId} to API. Status code: {response.StatusCode}, Reason: {response.ReasonPhrase}, Response: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending data to API.");
        }
    }

    private async Task<string> GetJwtTokenAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var loginData = new
            {
                Email = _configuration["Api:Email"],
                Password = _configuration["Api:Password"]
            };
            var content = new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{_apiBaseUrl}/api/auth/login", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to retrieve JWT token. Status code: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                throw new Exception("Failed to retrieve JWT token.");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Successfully retrieved JWT token.");

            var tokenResponse = JsonSerializer.Deserialize<JwtTokenResponse>(responseContent);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.Token))
            {
                _logger.LogError("JWT token is null or empty.");
                throw new Exception("Failed to retrieve JWT token.");
            }

            return tokenResponse.Token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving JWT token.");
            throw;
        }
    }
}
