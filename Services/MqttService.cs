using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using System.Text;
using System.Text.Json;
using garge_operator.Models;
using MQTTnet.Packets;
using garge_operator.Dtos.Mqtt;

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
        private readonly HashSet<string> _batteryHealthUniqIds = new();
        private readonly HashSet<string> _recentlyPublishedStates = new();
        private readonly object _stateLock = new();
        private readonly string _applicationId = Guid.NewGuid().ToString();
        private readonly Dictionary<string, string> _lastPublishedSwitchStates = new();
        private readonly HashSet<string> _subscribedTopics = new();

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
                throw new ArgumentException("Mqtt:Broker not set in configuration.");

            var portString = configuration["Mqtt:Port"];
            if (!int.TryParse(portString, out var port))
                throw new ArgumentException("Mqtt:Port is invalid or not set in configuration.");

            // Safely read API BaseUrl
            var baseUrl = configuration["Api:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Api:BaseUrl not set in configuration.");
            _apiBaseUrl = baseUrl;

            // Safely read username & password
            var username = configuration["Mqtt:Username"];
            var password = configuration["Mqtt:Password"];
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Mqtt:Username or Mqtt:Password not set in configuration.");

            // Create the client
            var factory = new MqttFactory();
            _mqttClient = factory.CreateManagedMqttClient();

            // Build the client options
            var clientOptions = new MqttClientOptionsBuilder()
                .WithClientId($"garge-operator-{Guid.NewGuid()}")
                .WithTcpServer(broker, port)
                .WithTlsOptions(o => { o.UseTls(); })
                .WithCredentials(username, password)
                .Build();

            _mqttClientOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(clientOptions)
                .Build();
        }

        public IReadOnlyDictionary<string, string> LastPublishedSwitchStates
        {
            get
            {
                lock (_stateLock)
                {
                    return new Dictionary<string, string>(_lastPublishedSwitchStates);
                }
            }
        }

        public Switch? GetSwitch(int targetId) => _switches.FirstOrDefault(s => s.Id == targetId);

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
                await _mqttClient.SubscribeAsync(new List<MqttTopicFilter>
                {
                    new MqttTopicFilterBuilder().WithTopic("garge/devices/+/config").Build(),
                    new MqttTopicFilterBuilder().WithTopic("garge/devices/+/+/config").Build(),
                    new MqttTopicFilterBuilder().WithTopic("garge/devices/+/+/state").Build(),
                    new MqttTopicFilterBuilder().WithTopic("garge/devices/+/+/set").Build(),
                    new MqttTopicFilterBuilder().WithTopic("garge/devices/+/discovered_devices/+/discovered").Build()
                });
                _logger.LogInformation("Subscribed to garge/devices/+/config, garge/devices/+/+/config, state, set topics and device discovery events.");
            };

            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogWarning("Disconnected from MQTT broker.");
                if (e.Exception != null)
                    _logger.LogError(e.Exception, "MQTT client disconnected due to an error.");
                else
                    _logger.LogWarning("MQTT client disconnected.");

                // Attempt to reconnect
                await Task.Delay(TimeSpan.FromSeconds(5));
                _logger.LogInformation("Reconnecting to MQTT broker...");
                await _mqttClient.StartAsync(_mqttClientOptions);
            };

            // Connect to the broker
            _logger.LogInformation("Connecting to MQTT broker...");
            await _mqttClient.StartAsync(_mqttClientOptions);
        }

        private async Task HandleDiscoveryEvent(string topic, string payload)
        {
            try
            {
                var devicePayload = JsonSerializer.Deserialize<DiscoveredDevicePayload>(payload);
                if (devicePayload == null)
                {
                    _logger.LogWarning("Could not deserialize discovery payload: {Payload}", payload);
                    return;
                }

                _logger.LogInformation("Handling discovery event for topic {Topic} with payload: {Payload}", topic, payload);

                await GrantDeviceControlAsync(devicePayload.DiscoveredBy, devicePayload.Target);
                await PostDiscoveredDevice(devicePayload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling discovery event for topic {Topic}", topic);
            }
        }

        private async Task HandleReceivedMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                if (e.ApplicationMessage == null || e.ApplicationMessage.Topic == null)
                {
                    _logger.LogError("Received a message with no ApplicationMessage or topic.");
                    return;
                }

                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

                // Handle discovery event
                if (topic.StartsWith("garge/devices/") && topic.Contains("/discovered_devices/") && topic.EndsWith("/discovered"))
                {
                    _logger.LogInformation("Received discovery event: {Payload}", payload);
                    await HandleDiscoveryEvent(topic, payload);
                    return;
                }

                var topicParts = topic.Split('/');
                if (topicParts.Length < 4 || topicParts[0] != "garge" || topicParts[1] != "devices")
                {
                    _logger.LogWarning("Topic does not match expected structure: {Topic}", topic);
                    return;
                }

                string deviceId = topicParts[2];
                string type = topicParts[^1]; // config, state, set
                string entity = topicParts.Length == 5 ? topicParts[3] : deviceId; // entity if present, else deviceId

                _logger.LogInformation("Raw config payload: {Payload}", payload);

                // Ignore self-triggered messages
                if (payload.Contains(_applicationId))
                {
                    _logger.LogInformation("Ignoring self-triggered MQTT message.");
                    return;
                }

                switch (type)
                {
                    case "config":
                        if (payload.Contains("\"command_topic\"") && payload.Contains("\"payload_on\""))
                        {
                            try
                            {
                                var switchConfig = JsonSerializer.Deserialize<SwitchConfig>(payload);
                                if (switchConfig != null)
                                    await HandleSwitchConfig(switchConfig);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error deserializing switch config payload: {Payload}", payload);
                            }
                        }
                        else if (payload.Contains("\"stat_cla\"") && payload.Contains("\"stat_t\""))
                        {
                            try
                            {
                                var sensorConfig = JsonSerializer.Deserialize<SensorConfig>(payload);
                                if (sensorConfig != null)
                                    await HandleSensorConfig(sensorConfig);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error deserializing sensor config payload: {Payload}", payload);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Received unknown or incomplete config payload: {Payload}", payload);
                        }
                        break;

                    case "state":
                        // Skip retained state messages — these are replayed by the broker on reconnect
                        // and do not represent new sensor readings. New publishes from firmware are
                        // forwarded by the broker with Retain=false even if the publisher set retain=true.
                        if (e.ApplicationMessage.Retain)
                        {
                            _logger.LogDebug("Ignoring retained state message on topic {Topic}.", topic);
                            break;
                        }
                        if (_switchUniqIds.ContainsKey(entity))
                        {
                            await SendSwitchDataToApi(entity, "state", payload);
                            lock (_stateLock)
                            {
                                _lastPublishedSwitchStates[entity] = payload.ToUpperInvariant();
                            }
                            _logger.LogInformation("Updated local switch state for {Entity} to {State}.", entity, payload.ToUpperInvariant());
                        }
                        else if (_sensorUniqIds.ContainsKey(entity))
                        {
                            if (_batteryHealthUniqIds.Contains(entity))
                            {
                                await SendBatteryHealthToApi(entity, payload);
                            }
                            else
                            {
                                try
                                {
                                    if (payload.TrimStart().StartsWith("{") || payload.TrimStart().StartsWith("["))
                                    {
                                        var data = JsonSerializer.Deserialize<SensorStatePayload>(payload);
                                        if (data != null && data.Value.ValueKind != JsonValueKind.Undefined)
                                        {
                                            await SendDataToApi(entity, data.Value.ToString());
                                        }
                                        else
                                        {
                                            _logger.LogWarning("Sensor state payload for {Entity} did not contain a valid 'value' property.", entity);
                                        }
                                    }
                                    else
                                    {
                                        await SendDataToApi(entity, payload);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to parse sensor state payload for {Entity}, sending as raw string.", entity);
                                    await SendDataToApi(entity, payload);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Entity {Entity} not found in switch or sensor lists.", entity);
                        }
                        break;
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
                _logger.LogInformation("Current sensors: {Sensors}", string.Join(", ", _sensors.Select(s => s.Name)));

                // Battery health is not stored as a sensor; derive the voltage sensor name by convention.
                if (sensorConfig.DevCla == "battery")
                {
                    _batteryHealthUniqIds.Add(sensorConfig.UniqId);
                    _sensorUniqIds[sensorConfig.UniqId] = sensorConfig.UniqId;
                    _logger.LogDebug("Tracked battery health uniq_id {UniqId}", sensorConfig.UniqId);
                    return;
                }

                // Check if the sensor exists in the list
                var sensorExists = _sensors.Any(s => s.Name == sensorConfig.UniqId);
                if (!sensorExists)
                {
                    // Create a new sensor
                    var createSensorData = new Sensor
                    {
                        Name = sensorConfig.UniqId,
                        Type = sensorConfig.DevCla,
                        ParentName = sensorConfig.ParentName,
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

                _logger.LogDebug("Stored uniq_id for sensor {UniqId}", sensorConfig.UniqId);
                _logger.LogDebug("Current uniq_id keys: {Keys}", string.Join(", ", _sensorUniqIds.Keys));
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
                _logger.LogInformation("Current switches: {Switches}", string.Join(", ", _switches.Select(s => s.Name)));
                // Check if the switch exists in the list
                var switchExists = _switches.Any(s => s.Name == switchConfig.UniqId);
                if (!switchExists)
                {
                    var createSwitchData = new Switch
                    {
                        Name = switchConfig.UniqId,
                        Type = switchConfig.Device.Model
                    };
                    // Add optimistically before the async call to prevent a duplicate creation
                    // if a second config message arrives before TryCreateSwitch returns.
                    // Rolled back on genuine failure; 409 (already exists) is treated as success.
                    _switches.Add(createSwitchData);
                    var created = await TryCreateSwitch(createSwitchData);
                    if (!created)
                        _switches.Remove(createSwitchData);
                }
                _switchUniqIds[switchConfig.UniqId] = switchConfig.UniqId;
                _logger.LogDebug("Stored uniq_id for switch {UniqId}", switchConfig.UniqId);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling switch config.");
            }
        }

        private async Task<bool> GrantDeviceControlAsync(string granteeDeviceId, string targetDeviceId)
        {
            try
            {
                var token = await GetJwtTokenAsync();
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var topic = $"garge/devices/{targetDeviceId}/#";
                bool allSucceeded = true;

                _logger.LogInformation("Granting publish rights for {GranteeDeviceId} to topic {Topic}...", granteeDeviceId, topic);

                foreach (var retain in new[] { false, true })
                {
                    var aclPayload = new
                    {
                        Username = granteeDeviceId,
                        Permission = "allow",
                        Action = "all",
                        Topic = topic,
                        Qos = 0,
                        Retain = retain ? 1 : 0
                    };

                    var content = new StringContent(JsonSerializer.Serialize(aclPayload), Encoding.UTF8, "application/json");
                    var response = await client.PostAsync($"{_apiBaseUrl}/api/mqtt/acl", content);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Granted publish rights for {GranteeDeviceId} to topic {Topic} (retain={Retain}).", granteeDeviceId, topic, retain);
                    }
                    else
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        _logger.LogError("Failed to grant ACL for {GranteeDeviceId} to topic {Topic} (retain={Retain}): StatusCode={StatusCode}, Response={Error}", granteeDeviceId, topic, retain, response.StatusCode, error);
                        allSucceeded = false;
                    }
                }

                return allSucceeded;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error granting device control ACL.");
                return false;
            }
        }

        private async Task PostDiscoveredDevice(DiscoveredDevicePayload devicePayload)
        {
            try
            {
                var token = await GetJwtTokenAsync();
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var content = new StringContent(JsonSerializer.Serialize(devicePayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{_apiBaseUrl}/api/mqtt/discovered-device", content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Posted discovered device: {DevicePayload}", JsonSerializer.Serialize(devicePayload));
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to post discovered device: StatusCode={StatusCode}, Response={Error}", response.StatusCode, error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error posting discovered device.");
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
                _logger.LogDebug("Successfully created sensor: {SensorName}", createSensorData.Name);
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogWarning("Sensor already exists: {SensorName}", createSensorData.Name);
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
                _logger.LogInformation("Successfully created switch: {SwitchName}", createSwitchData.Name);
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogWarning("Switch already exists: {SwitchName}", createSwitchData.Name);
                return true;
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
                    _logger.LogError("Failed to retrieve sensors. Status code: {StatusCode}, Reason: {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                    return new List<Sensor>();
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Sensors response length: {Length}", responseContent.Length);

                var jsonDocument = JsonDocument.Parse(responseContent);
                var sensors = jsonDocument.RootElement.Deserialize<List<Sensor>>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (sensors == null || !sensors.Any())
                {
                    _logger.LogWarning("No sensors found in the API response.");
                }
                else
                {
                    _logger.LogInformation("Deserialized {SensorCount} sensors.", sensors.Count);
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
                    _logger.LogError("Failed to retrieve switches. Status code: {StatusCode}, Reason: {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                    return new List<Switch>();
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Switches response length: {Length}", responseContent.Length);

                var jsonDocument = JsonDocument.Parse(responseContent);
                var switches = jsonDocument.RootElement.Deserialize<List<Switch>>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (switches == null || !switches.Any())
                {
                    _logger.LogWarning("No switches found in the API response.");
                }
                else
                {
                    _logger.LogInformation("Deserialized {SwitchCount} switches.", switches.Count);
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
                _logger.LogInformation("Successfully created sensor: {SensorName}", createSensorData.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating sensor.");
            }
        }

        private async Task SendDataToApi(string uniqId, string value)
        {
            try
            {
                _logger.LogInformation("Preparing to send data for sensor {SensorId} to API.", uniqId);

                var token = await GetJwtTokenAsync();
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var content = new StringContent(JsonSerializer.Serialize(new { value = value }), Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending data to API: {ApiBaseUrl}/api/sensors/name/{UniqId}/data", _apiBaseUrl, uniqId);
                var response = await client.PostAsync($"{_apiBaseUrl}/api/sensors/name/{uniqId}/data", content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent data for sensor {UniqId} to API.", uniqId);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to send data for sensor {UniqId} to API. Status code: {StatusCode}, Reason: {ReasonPhrase}, Response: {ResponseContent}", uniqId, response.StatusCode, response.ReasonPhrase, responseContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending data to API.");
            }
        }

        private async Task SendBatteryHealthToApi(string sensorName, string payload)
        {
            try
            {
                var voltageSensorName = sensorName.Replace("_battery_health", "_voltage");
                _logger.LogInformation("Preparing to send battery health for voltage sensor {VoltageSensorName} to API.", voltageSensorName);

                var batteryPayload = JsonSerializer.Deserialize<BatteryHealthPayload>(payload);
                if (batteryPayload == null)
                {
                    _logger.LogError("Failed to deserialize battery health payload for {SensorName}: {Payload}", sensorName, payload);
                    return;
                }

                var dto = new
                {
                    status = batteryPayload.Status,
                    baseline = batteryPayload.Baseline,
                    lastCharge = batteryPayload.LastCharge,
                    dropPct = batteryPayload.DropPct,
                    chargesRecorded = batteryPayload.ChargesRecorded
                };

                var token = await GetJwtTokenAsync();
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending battery health to API: {ApiBaseUrl}/api/battery-health/name/{VoltageSensorName}", _apiBaseUrl, voltageSensorName);
                var response = await client.PostAsync($"{_apiBaseUrl}/api/battery-health/name/{voltageSensorName}", content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent battery health for voltage sensor {VoltageSensorName} to API.", voltageSensorName);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to send battery health for voltage sensor {VoltageSensorName} to API. Status code: {StatusCode}, Reason: {ReasonPhrase}, Response: {ResponseContent}", voltageSensorName, response.StatusCode, response.ReasonPhrase, responseContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending battery health to API for sensor {SensorName}.", sensorName);
            }
        }

        private async Task SendSwitchDataToApi(string uniqId, string key, string value)
        {
            try
            {
                _logger.LogInformation("Preparing to send data for switch {UniqId} to API with key: {Key}", uniqId, key);

                var token = await GetJwtTokenAsync();
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var content = new StringContent(JsonSerializer.Serialize(new { key, value }), Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending data to API: {ApiBaseUrl}/api/switches/name/{UniqId}/data", _apiBaseUrl, uniqId);
                var response = await client.PostAsync($"{_apiBaseUrl}/api/switches/name/{uniqId}/data", content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent data for switch {UniqId} to API.", uniqId);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to send data for switch {UniqId} to API. Status code: {StatusCode}, Reason: {ReasonPhrase}, Response: {ResponseContent}", uniqId, response.StatusCode, response.ReasonPhrase, responseContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending data to API.");
            }
        }

        public async Task<string> GetJwtTokenAsync()
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
                    _logger.LogError("Failed to retrieve JWT token. Status code: {StatusCode}, Reason: {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                    throw new Exception("Failed to retrieve JWT token.");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Successfully retrieved JWT token.");

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
                    var switchName = topic.Split('/')[2];
                    if (_lastPublishedSwitchStates.TryGetValue(switchName, out var currentState) && currentState == payload)
                    {
                        var sanitizedPayload = payload.Replace("\r", "").Replace("\n", "");
                        _logger.LogInformation("Skipping publish for switch {SwitchName} as the state {State} is unchanged.", switchName, sanitizedPayload);
                        return;
                    }

                    _lastPublishedSwitchStates[switchName] = payload;
                }

                var messagePayload = payload.ToUpperInvariant();

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(messagePayload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag()
                    .Build();

                await _mqttClient.EnqueueAsync(message);
                _logger.LogDebug("Published Switch data to topic '{Topic}': {Payload}", topic, messagePayload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing Switch data to topic {Topic}.", topic);
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
                    _logger.LogError("Failed to register as a subscriber. Status code: {StatusCode}, Reason: {ReasonPhrase}, Response: {ResponseContent}", response.StatusCode, response.ReasonPhrase, responseContent);
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
                _logger.LogDebug("Received webhook data for switch: {SwitchName}, value: {Value}", payload.Switch?.Name, payload.Value);

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

                var topic = $"garge/devices/{switchName}/set";
                lock (_stateLock)
                {
                    if (_recentlyPublishedStates.Contains($"{topic}:{state}"))
                    {
                        var sanitizedState = state.Replace("\r", "").Replace("\n", "");
                        var sanitizedTopic = topic.Replace("\r", "").Replace("\n", "");
                        _logger.LogInformation("Ignoring self-triggered event for topic {Topic} with state {State}.", sanitizedTopic, sanitizedState);
                        _recentlyPublishedStates.Remove($"{topic}:{state}");
                        return;
                    }
                }

                if (_switchUniqIds.TryGetValue(switchName, out var switchUniqId))
                {
                    await PublishSwitchDataAsync(topic, state);
                    var sanitizedState = state.Replace("\r", "").Replace("\n", "");
                    var sanitizedTopic = topic.Replace("\r", "").Replace("\n", "");
                    _logger.LogInformation("Published switch state {State} to topic {Topic}.", sanitizedState, sanitizedTopic);
                }
                else
                {
                    _logger.LogWarning("No uniq_id found for switch {SwitchName}", switchName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling webhook data.");
            }
        }
    }
}
