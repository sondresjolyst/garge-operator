using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using System.Text;
using System.Text.Json;
using garge_operator.Models;
using MQTTnet.Packets;

namespace garge_operator.Services
{
    public class MqttService
    {
        private readonly IManagedMqttClient _mqttClient;
        private readonly ManagedMqttClientOptions _mqttClientOptions;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MqttService> _logger;
        private readonly string _apiBaseUrl;
        private List<Sensor> _sensors;
        private List<Switch> _switches;
        private Dictionary<string, string> _sensorUniqIds;
        private Dictionary<string, string> _switchUniqIds;
        private readonly HashSet<string> _recentlyPublishedStates = new();
        private readonly object _stateLock = new();
        private readonly string _applicationId = Guid.NewGuid().ToString();
        private readonly Dictionary<string, string> _lastPublishedSwitchStates = new();


        public MqttService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<MqttService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _sensorUniqIds = new Dictionary<string, string>();
            _switchUniqIds = new Dictionary<string, string>();
            _sensors = new List<Sensor>();
            _switches = new List<Switch>();

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

            // Register this service as a subscriber
            await RegisterAsSubscriberAsync();

            // Get all sensors from the API
            _sensors = await GetAllSensorsAsync(token);

            // Get all switches from the API
            _switches = await GetAllSwitchesAsync(token);

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

                // Log the raw payload
                _logger.LogInformation($"Raw payload: {json}");

                // Only update and post if state changed
                lock (_stateLock)
                {
                    // Only deduplicate for switch topics or ON/OFF payloads
                    if (json.Equals("ON", StringComparison.OrdinalIgnoreCase) || json.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
                        e.ApplicationMessage.Topic.Contains("/switch/"))
                    {
                        if (_lastPublishedSwitchStates.TryGetValue(e.ApplicationMessage.Topic, out var lastState) &&
                            lastState == json)
                        {
                            _logger.LogInformation($"Duplicate switch state '{json}' for topic '{e.ApplicationMessage.Topic}' ignored.");
                            return;
                        }
                        _lastPublishedSwitchStates[e.ApplicationMessage.Topic] = json;
                        _logger.LogInformation($"Updated internal switch state for topic '{e.ApplicationMessage.Topic}' to '{json}'.");
                    }
                }

                // Check if the payload is a plain string
                if (json.Equals("ON", StringComparison.OrdinalIgnoreCase) || json.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"Received plain string payload: {json}");

                    // Extract the device name from the topic
                    var topicParts = e.ApplicationMessage.Topic.Split('/');
                    if (topicParts.Length >= 3)
                    {
                        var deviceName = topicParts[2];

                        // Handle switch state
                        if (_switchUniqIds.TryGetValue(deviceName, out var switchUniqId))
                        {
                            _logger.LogInformation($"Sending switch state to API for switch {switchUniqId} with value: {json}");
                            await SendSwitchDataToApi(switchUniqId, "state", json);
                        }
                        else
                        {
                            _logger.LogWarning($"No uniq_id found for switch {deviceName}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Topic does not contain enough parts to extract device name.");
                    }

                    return; // Exit after handling plain string payload
                }

                // Attempt to deserialize as JSON
                var message = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                if (message != null && message.TryGetValue("Source", out var source) && source?.ToString() == _applicationId)
                {
                    _logger.LogInformation("Ignoring self-triggered MQTT message.");
                    return;
                }

                // Log the received payload
                _logger.LogInformation($"Received payload: {json}");

                // Check if the message is a device config
                if (e.ApplicationMessage.Topic.StartsWith("homeassistant/") && e.ApplicationMessage.Topic.EndsWith("/config"))
                {
                    if (e.ApplicationMessage.Topic.Contains("/switch/"))
                    {
                        var switchConfig = JsonSerializer.Deserialize<SwitchConfig>(json);
                        if (switchConfig != null)
                        {
                            await HandleSwitchConfig(switchConfig);
                        }
                    }
                    else
                    {
                        var sensorConfig = JsonSerializer.Deserialize<SensorConfig>(json);
                        if (sensorConfig != null)
                        {
                            await HandleSensorConfig(sensorConfig);
                        }
                    }
                }
                else
                {
                    // Handle sensor or switch data
                    var topicParts = e.ApplicationMessage.Topic.Split('/');
                    if (topicParts.Length >= 3)
                    {
                        var deviceName = topicParts[2];

                        // Assume the payload is a JSON object and handle sensor data
                        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                        if (data != null)
                        {
                            foreach (var kvp in data)
                            {
                                var key = kvp.Key;
                                var value = kvp.Value?.ToString();
                                if (value != null)
                                {
                                    var deviceKey = $"{deviceName}_{key}";
                                    if (_sensorUniqIds.TryGetValue(deviceKey, out var uniqId))
                                    {
                                        // Handle sensor data
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
                                        _logger.LogWarning($"No uniq_id found for device {deviceKey}");
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
                            _logger.LogWarning("Device data is null.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Topic does not contain enough parts to extract device name.");
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error deserializing JSON payload.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling received message.");
            }
        }

        private async Task HandleSensorConfig(SensorConfig sensorConfig)
        {
            try
            {
                // Log the current sensors for debugging
                _logger.LogInformation("Current sensors: " + string.Join(", ", _sensors.Select(s => s.Name)));

                // Check if the sensor exists in the list
                var sensorExists = _sensors.Any(s => s.Name == sensorConfig.UniqId);
                if (!sensorExists)
                {
                    // Create a new sensor
                    var createSensorData = new Sensor
                    {
                        Name = sensorConfig.UniqId,
                        Type = sensorConfig.DevCla
                    };
                    var created = await TryCreateSensor(createSensorData);

                    if (created)
                    {
                        // Add the new sensor to the list
                        _sensors.Add(createSensorData);
                    }
                }

                // Store the uniq_id for the sensor type
                _sensorUniqIds[sensorConfig.UniqId] = sensorConfig.UniqId;
                _logger.LogInformation($"Stored uniq_id for sensor {sensorConfig.UniqId}");

                // Log the current uniq_id mappings for debugging
                _logger.LogInformation("Current uniq_id mappings: " + string.Join(", ", _sensorUniqIds.Select(kvp => $"{kvp.Key}: {kvp.Value}")));

                // Subscribe to the sensor's state topic
                await _mqttClient.SubscribeAsync(new List<MqttTopicFilter>
            {
                new MqttTopicFilterBuilder().WithTopic(sensorConfig.StatT).Build()
            });
                _logger.LogInformation($"Subscribed to sensor state topic: {sensorConfig.StatT}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling device config.");
            }
        }

        private async Task HandleSwitchConfig(SwitchConfig switchConfig)
        {
            try
            {
                // Log the current switches for debugging
                _logger.LogInformation("Current switches: " + string.Join(", ", _switches.Select(s => s.Name)));

                // Check if the switch exists in the list
                var switchExists = _switches.Any(s => s.Name == switchConfig.UniqId);
                if (!switchExists)
                {
                    // Create a new switch
                    var createSwitchData = new Switch
                    {
                        Name = switchConfig.UniqId,
                        Type = "switch" // Assuming type is "switch"
                    };
                    var created = await TryCreateSwitch(createSwitchData);

                    if (created)
                    {
                        // Add the new switch to the list
                        _switches.Add(createSwitchData);
                    }
                }

                // Store the uniq_id for the switch
                _switchUniqIds[switchConfig.UniqId] = switchConfig.UniqId;
                _logger.LogInformation($"Stored uniq_id for switch {switchConfig.UniqId}");

                // Subscribe to the switch's state topic
                await _mqttClient.SubscribeAsync(new List<MqttTopicFilter>
            {
                new MqttTopicFilterBuilder().WithTopic(switchConfig.StateTopic).Build()
            });
                _logger.LogInformation($"Subscribed to switch state topic: {switchConfig.StateTopic}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling switch config.");
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
                var response = await client.PostAsync($"{_apiBaseUrl}/api/sensors", content);
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

        private async Task<bool> TryCreateSwitch(Switch createSwitchData)
        {
            try
            {
                var token = await GetJwtTokenAsync();
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var content = new StringContent(JsonSerializer.Serialize(createSwitchData), Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{_apiBaseUrl}/api/switches", content);
                response.EnsureSuccessStatusCode();
                _logger.LogInformation($"Successfully created switch: {createSwitchData.Name}");
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogWarning($"Switch already exists: {createSwitchData.Name}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating switch.");
                return false;
            }
        }

        private async Task<List<Sensor>> GetAllSensorsAsync(string token)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var response = await client.GetAsync($"{_apiBaseUrl}/api/sensors");

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

        private async Task<List<Switch>> GetAllSwitchesAsync(string token)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var response = await client.GetAsync($"{_apiBaseUrl}/api/switches");

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to retrieve switches. Status code: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                    return new List<Switch>();
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Switches response: {responseContent}");

                var jsonDocument = JsonDocument.Parse(responseContent);
                var switches = jsonDocument.RootElement.GetProperty("$values").Deserialize<List<Switch>>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (switches == null || !switches.Any())
                {
                    _logger.LogWarning("No switches found in the API response.");
                }
                else
                {
                    _logger.LogInformation($"Deserialized switches: {string.Join(", ", switches.Select(s => s.Name))}");
                }

                return switches ?? new List<Switch>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving switches.");
                return new List<Switch>();
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
                var response = await client.PostAsync($"{_apiBaseUrl}/api/sensors", content);
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

                _logger.LogInformation($"Sending data to API: {_apiBaseUrl}/api/sensors/name/{uniqId}/data");
                var response = await client.PostAsync($"{_apiBaseUrl}/api/sensors/name/{uniqId}/data", content);

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

        private async Task SendSwitchDataToApi(string uniqId, string key, string value)
        {
            try
            {
                _logger.LogInformation($"Preparing to send data for switch {uniqId} to API with key: {key}");

                var token = await GetJwtTokenAsync();
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var content = new StringContent(JsonSerializer.Serialize(new { key, value }), Encoding.UTF8, "application/json");

                _logger.LogInformation($"Sending data to API: {_apiBaseUrl}/api/switches/name/{uniqId}/data");
                var response = await client.PostAsync($"{_apiBaseUrl}/api/switches/name/{uniqId}/data", content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Successfully sent data for switch {uniqId} to API.");
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Failed to send data for switch {uniqId} to API. Status code: {response.StatusCode}, Reason: {response.ReasonPhrase}, Response: {responseContent}");
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

        public async Task PublishSwitchDataAsync(string topic, string payload)
        {
            try
            {
                lock (_stateLock)
                {
                    // Check if the desired state differs from the current state
                    if (_lastPublishedSwitchStates.TryGetValue(topic, out var currentState) && currentState == payload)
                    {
                        var sanitizedPayload = payload.Replace("\r", "").Replace("\n", "");
                        _logger.LogInformation($"Skipping publish for topic '{topic}' as the state '{sanitizedPayload}' is unchanged.");
                        return;
                    }

                    // Update the internal state to the new state
                    _lastPublishedSwitchStates[topic] = payload;
                }

                var messagePayload = JsonSerializer.Serialize(new
                {
                    State = payload,
                    Source = _applicationId
                });

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(messagePayload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag()
                    .Build();

                await _mqttClient.EnqueueAsync(message);
                _logger.LogInformation($"Published Switch data to topic '{topic}': {messagePayload}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error publishing Switch data to topic '{topic}'.");
            }
        }

        public async Task RegisterAsSubscriberAsync()
        {
            try
            {
                var token = await GetJwtTokenAsync(); // Get the JWT token for authentication
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                // Define the webhook URL for this application
                var webhookUrl = _configuration["Webhook:Url"];
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogError("Webhook URL is not configured.");
                    return;
                }

                // Create the payload for the subscription
                var payload = new
                {
                    WebhookUrl = webhookUrl
                };

                // Send the POST request to the API's subscription endpoint
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{_apiBaseUrl}/api/webhook", content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully registered as a subscriber.");
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Failed to register as a subscriber. Status code: {response.StatusCode}, Reason: {response.ReasonPhrase}, Response: {responseContent}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering as a subscriber.");
            }
        }
        public async Task HandleWebhookDataAsync(WebhookPayload payload)
        {
            try
            {
                _logger.LogInformation($"Received webhook data: {JsonSerializer.Serialize(payload)}");

                if (payload.Switch == null)
                {
                    _logger.LogWarning("Webhook payload does not contain a valid switch.");
                    return;
                }

                if (payload.Value == _applicationId)
                {
                    _logger.LogInformation("Ignoring self-triggered webhook notification.");
                    return;
                }

                var switchName = payload.Switch.Name;
                var state = payload.Value.ToUpperInvariant();

                var topic = $"home/storage/{switchName}/set";
                lock (_stateLock)
                {
                    if (_recentlyPublishedStates.Contains($"{topic}:{state}"))
                    {
                        var sanitizedState = state.Replace("\r", "").Replace("\n", "");
                        var sanitizedTopic = topic.Replace("\r", "").Replace("\n", "");
                        _logger.LogInformation($"Ignoring self-triggered event for topic '{sanitizedTopic}' with state '{sanitizedState}'.");
                        _recentlyPublishedStates.Remove($"{topic}:{state}");
                        return;
                    }
                }

                if (_switchUniqIds.TryGetValue(switchName, out var switchUniqId))
                {
                    await PublishSwitchDataAsync(topic, state);
                    var sanitizedState = state.Replace("\r", "").Replace("\n", "");
                    var sanitizedTopic = topic.Replace("\r", "").Replace("\n", "");
                    _logger.LogInformation($"Published switch state '{sanitizedState}' to topic '{sanitizedTopic}'.");
                }
                else
                {
                    _logger.LogWarning($"No uniq_id found for switch {switchName}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling webhook data.");
            }
        }
    }
}
