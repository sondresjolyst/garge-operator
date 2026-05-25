using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using System.Text;
using System.Text.Json;
using garge_operator.Models;
using MQTTnet.Packets;
using garge_operator.Dtos.Mqtt;
using garge_operator.Constants;
using Microsoft.Extensions.Options;

namespace garge_operator.Services
{
    public class MqttService : IMqttService
    {
        private readonly IManagedMqttClient _mqttClient;
        private readonly ManagedMqttClientOptions _mqttClientOptions;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ITokenProvider _tokenProvider;
        private readonly ILogger<MqttService> _logger;
        private readonly string _apiBaseUrl;
        private List<Sensor> _sensors;
        private List<Switch> _switches;
        private Dictionary<string, string> _sensorUniqIds;
        private Dictionary<string, string> _switchUniqIds;
        private readonly object _stateLock = new();
        private readonly object _listsLock = new();
        private readonly Dictionary<string, string> _lastPublishedSwitchStates = new();

        // Reused across all deserialization calls to avoid per-call allocation.
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        // Tracks the last command sent per switch so the /state handler can distinguish a
        // genuine post-switch confirmation from a WiZ pre-switch echo. A WiZ device reports its
        // current pre-toggle state before the relay physically moves. Within the grace window,
        // contradicting /state messages are treated as echoes and ignored.
        private readonly Dictionary<string, (string Action, DateTime SentAt)> _lastCommandedAt = new();
        internal static readonly TimeSpan CommandEchoGrace = TimeSpan.FromSeconds(30);

        internal static bool IsPreSwitchEcho(
            string incomingState,
            (string Action, DateTime SentAt)? lastCommand,
            DateTime now,
            TimeSpan graceWindow)
        {
            if (lastCommand is not { } cmd) return false;
            if (string.Equals(incomingState, cmd.Action, StringComparison.Ordinal)) return false;
            return (now - cmd.SentAt) < graceWindow;
        }

        public MqttService(
            IHttpClientFactory httpClientFactory,
            ITokenProvider tokenProvider,
            IOptions<MqttOptions> mqttOptions,
            IOptions<ApiOptions> apiOptions,
            ILogger<MqttService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _tokenProvider = tokenProvider;
            _logger = logger;
            _sensorUniqIds = new Dictionary<string, string>();
            _switchUniqIds = new Dictionary<string, string>();
            _sensors = new List<Sensor>();
            _switches = new List<Switch>();

            // Configuration is bound and validated at startup (see Program.cs ValidateOnStart),
            // so the values are guaranteed present here.
            var mqtt = mqttOptions.Value;
            _apiBaseUrl = apiOptions.Value.BaseUrl;

            var factory = new MqttFactory();
            _mqttClient = factory.CreateManagedMqttClient();

            var clientOptions = new MqttClientOptionsBuilder()
                .WithClientId($"garge-operator-{Guid.NewGuid()}")
                .WithTcpServer(mqtt.Broker, mqtt.Port)
                .WithTlsOptions(o => { o.UseTls(); })
                .WithCredentials(mqtt.Username, mqtt.Password)
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

        public Switch? GetSwitch(int targetId)
        {
            lock (_listsLock)
            {
                return _switches.FirstOrDefault(s => s.Id == targetId);
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            // Ensure login is successful before connecting to MQTT broker.
            var token = await _tokenProvider.GetJwtTokenAsync(cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                // Without a token MQTT can never authenticate. Throw rather than return so the
                // hosted Worker fails fast: with BackgroundServiceExceptionBehavior.StopHost the
                // process exits and Kubernetes reschedules the pod, instead of running forever
                // with a permanently dead MQTT connection.
                _logger.LogError("Cannot connect to MQTT broker without a valid JWT token.");
                throw new InvalidOperationException("Cannot connect to MQTT broker without a valid JWT token.");
            }

            // Get all sensors from the API
            _sensors = await GetAllSensorsAsync(cancellationToken);

            // Get all switches from the API
            _switches = await GetAllSwitchesAsync(cancellationToken);

            // Subscribe to the message received event
            _mqttClient.ApplicationMessageReceivedAsync += HandleReceivedMessage;

            // Log connection status
            _mqttClient.ConnectedAsync += async e =>
            {
                // This runs as an unobserved async continuation; an unhandled throw here would be
                // swallowed by the MQTT client. Guard the subscribe so failures are logged.
                try
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
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to subscribe to MQTT topics after connecting.");
                }
            };

            _mqttClient.DisconnectedAsync += e =>
            {
                _logger.LogWarning("Disconnected from MQTT broker.");
                if (e.Exception != null)
                    _logger.LogError(e.Exception, "MQTT client disconnected due to an error.");
                else
                    _logger.LogWarning("MQTT client disconnected.");
                // IManagedMqttClient handles reconnection automatically.
                return Task.CompletedTask;
            };

            // Connect to the broker
            _logger.LogInformation("Connecting to MQTT broker...");
            await _mqttClient.StartAsync(_mqttClientOptions);
        }

        private static bool IsValidDeviceId(string id)
            => !string.IsNullOrEmpty(id) && System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9_\-]+$");

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

                // Topic structure: garge/devices/{discoveredBy}/discovered_devices/{target}/discovered
                if (!GargeTopics.TryParseDiscoveryTopic(topic, out var topicDiscoveredBy, out var topicTarget))
                {
                    _logger.LogWarning("Discovery topic has unexpected structure: {Topic}", topic);
                    return;
                }

                if (!string.Equals(devicePayload.DiscoveredBy, topicDiscoveredBy, StringComparison.Ordinal) ||
                    !string.Equals(devicePayload.Target, topicTarget, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Discovery payload fields don't match topic segments. Ignoring. Topic={Topic}", topic);
                    return;
                }
                if (!IsValidDeviceId(devicePayload.DiscoveredBy) || !IsValidDeviceId(devicePayload.Target))
                {
                    _logger.LogWarning("Discovery payload contains invalid device IDs. Ignoring.");
                    return;
                }

                _logger.LogInformation("Handling discovery event for topic {Topic}", topic);

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
                    _logger.LogInformation("Received discovery event on topic {Topic}", topic);
                    await HandleDiscoveryEvent(topic, payload);
                    return;
                }

                if (!GargeTopics.TryParseDeviceTopic(topic, out var deviceId, out var type, out var entity))
                {
                    _logger.LogWarning("Topic does not match expected structure: {Topic}", topic);
                    return;
                }

                _logger.LogDebug("Received message on topic {Topic} (type {Type}).", topic, type);

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
                                _logger.LogError(ex, "Error deserializing switch config payload.");
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
                                _logger.LogError(ex, "Error deserializing sensor config payload.");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Received unknown or incomplete config payload on topic {Topic}", topic);
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

                        bool isSwitchEntity, isSensorEntity;
                        lock (_listsLock)
                        {
                            isSwitchEntity = _switchUniqIds.ContainsKey(entity);
                            isSensorEntity = _sensorUniqIds.ContainsKey(entity);
                        }

                        if (isSwitchEntity)
                        {
                            var normalizedState = payload.ToUpperInvariant();
                            bool isPreSwitchEcho;
                            lock (_stateLock)
                            {
                                (string, DateTime)? lastCmd = _lastCommandedAt.TryGetValue(entity, out var lc)
                                    ? (lc.Action, lc.SentAt)
                                    : null;
                                isPreSwitchEcho = IsPreSwitchEcho(normalizedState, lastCmd, DateTime.UtcNow, CommandEchoGrace);
                            }

                            if (isPreSwitchEcho)
                            {
                                _logger.LogInformation(
                                    "Ignoring pre-switch echo state '{State}' for {Entity} — device echoed its current state before physically switching.",
                                    normalizedState, entity);
                                break;
                            }

                            await SendSwitchDataToApi(entity, "state", normalizedState);
                            lock (_stateLock)
                            {
                                _lastPublishedSwitchStates[entity] = normalizedState;
                                _lastCommandedAt.Remove(entity);
                            }
                            _logger.LogInformation("Updated local switch state for {Entity} to {State}.", entity, normalizedState);
                        }
                        else if (isSensorEntity)
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
                if (!IsValidDeviceId(sensorConfig.UniqId))
                {
                    _logger.LogWarning("Rejecting sensor config with invalid UniqId: {UniqId}", sensorConfig.UniqId);
                    return;
                }

                if (!garge_operator.Constants.SensorTypes.IsAllowed(sensorConfig.DevCla))
                {
                    _logger.LogWarning("Rejecting sensor config with unsupported type '{Type}' for {UniqId}", sensorConfig.DevCla, sensorConfig.UniqId);
                    return;
                }

                bool sensorExists;
                lock (_listsLock)
                {
                    _logger.LogDebug("Current sensors: {Sensors}", string.Join(", ", _sensors.Select(s => s.Name)));
                    sensorExists = _sensors.Any(s => s.Name == sensorConfig.UniqId);
                }

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
                        lock (_listsLock)
                        {
                            _sensors.Add(createSensorData);
                        }
                    }
                }

                // Store the uniq_id for the sensor type
                lock (_listsLock)
                {
                    _sensorUniqIds[sensorConfig.UniqId] = sensorConfig.UniqId;
                }

                _logger.LogDebug("Stored uniq_id for sensor {UniqId}", sensorConfig.UniqId);
                lock (_listsLock)
                {
                    _logger.LogDebug("Current uniq_id keys: {Keys}", string.Join(", ", _sensorUniqIds.Keys));
                }
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
                if (!IsValidDeviceId(switchConfig.UniqId))
                {
                    _logger.LogWarning("Rejecting switch config with invalid UniqId: {UniqId}", switchConfig.UniqId);
                    return;
                }
                if (!switchConfig.CommandTopic.StartsWith("garge/devices/", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Rejecting switch config with unexpected CommandTopic: {Topic}", switchConfig.CommandTopic);
                    return;
                }

                bool switchExists;
                Switch? createSwitchData = null;
                lock (_listsLock)
                {
                    _logger.LogDebug("Current switches: {Switches}", string.Join(", ", _switches.Select(s => s.Name)));
                    switchExists = _switches.Any(s => s.Name == switchConfig.UniqId);
                    if (!switchExists)
                    {
                        createSwitchData = new Switch
                        {
                            Name = switchConfig.UniqId,
                            Type = switchConfig.Device.Model
                        };
                        // Add optimistically before the async call to prevent a duplicate creation
                        // if a second config message arrives before TryCreateSwitch returns.
                        // Rolled back on genuine failure; 409 (already exists) is treated as success.
                        _switches.Add(createSwitchData);
                    }
                }

                if (createSwitchData != null)
                {
                    var created = await TryCreateSwitch(createSwitchData);
                    if (!created)
                    {
                        lock (_listsLock)
                        {
                            _switches.Remove(createSwitchData);
                        }
                    }
                }

                lock (_listsLock)
                {
                    _switchUniqIds[switchConfig.UniqId] = switchConfig.UniqId;
                }
                _logger.LogDebug("Stored uniq_id for switch {UniqId}", switchConfig.UniqId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling switch config.");
            }
        }

        // Resolves the authenticated garge-api client. The JWT bearer is attached automatically
        // by BearerTokenHandler, so call sites no longer fetch tokens or set headers themselves.
        private HttpClient CreateApiClient() => _httpClientFactory.CreateClient(GargeApiClient.Authorized);

        private async Task<bool> GrantDeviceControlAsync(string granteeDeviceId, string targetDeviceId)
        {
            try
            {
                var client = CreateApiClient();

                var topic = GargeTopics.DeviceWildcard(targetDeviceId);
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

                    var response = await HttpJson.PostJsonAsync(client, $"{_apiBaseUrl}/api/mqtt/acl", aclPayload);

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
                var client = CreateApiClient();

                var response = await HttpJson.PostJsonAsync(client, $"{_apiBaseUrl}/api/mqtt/discovered-device", devicePayload);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Posted discovered device: {DiscoveredBy} -> {Target}", devicePayload.DiscoveredBy, devicePayload.Target);
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
                var client = CreateApiClient();
                var response = await HttpJson.PostJsonAsync(client, $"{_apiBaseUrl}/api/sensors", createSensorData);
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
                var client = CreateApiClient();
                var response = await HttpJson.PostJsonAsync(client, $"{_apiBaseUrl}/api/switches", createSwitchData);
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

        private async Task<List<Sensor>> GetAllSensorsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var client = CreateApiClient();
                var response = await client.GetAsync($"{_apiBaseUrl}/api/sensors", cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to retrieve sensors. Status code: {StatusCode}, Reason: {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                    return new List<Sensor>();
                }

                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Sensors response length: {Length}", responseContent.Length);

                var sensors = JsonSerializer.Deserialize<List<Sensor>>(responseContent, JsonOptions);

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

        private async Task<List<Switch>> GetAllSwitchesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var client = CreateApiClient();
                var response = await client.GetAsync($"{_apiBaseUrl}/api/switches", cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to retrieve switches. Status code: {StatusCode}, Reason: {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                    return new List<Switch>();
                }

                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Switches response length: {Length}", responseContent.Length);

                var switches = JsonSerializer.Deserialize<List<Switch>>(responseContent, JsonOptions);

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

        private async Task SendDataToApi(string uniqId, string value, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Preparing to send data for sensor {SensorId} to API.", uniqId);

                var client = CreateApiClient();
                var url = $"{_apiBaseUrl}/api/sensors/name/{Uri.EscapeDataString(uniqId)}/data";
                _logger.LogInformation("Sending data to API: {Url}", url);
                var response = await HttpJson.PostJsonAsync(client, url, new { value = value }, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent data for sensor {SensorId} to API.", uniqId);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Failed to send data for sensor {SensorId} to API. Status code: {StatusCode}, Reason: {ReasonPhrase}, Response: {ResponseContent}", uniqId, response.StatusCode, response.ReasonPhrase, responseContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending data to API.");
            }
        }

        private async Task SendSwitchDataToApi(string uniqId, string key, string value, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Preparing to send data for switch {UniqId} to API with key: {Key}", uniqId, key);

                var client = CreateApiClient();
                var url = $"{_apiBaseUrl}/api/switches/name/{Uri.EscapeDataString(uniqId)}/data";
                _logger.LogInformation("Sending data to API: {Url}", url);
                var response = await HttpJson.PostJsonAsync(client, url, new { key, value }, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent data for switch {UniqId} to API.", uniqId);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Failed to send data for switch {UniqId} to API. Status code: {StatusCode}, Reason: {ReasonPhrase}, Response: {ResponseContent}", uniqId, response.StatusCode, response.ReasonPhrase, responseContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending data to API.");
            }
        }

        /// <summary>
        /// Returns the cached JWT bearer used to authenticate against garge-api. Retained on
        /// <see cref="IMqttService"/> for callers (for example the SignalR hub access-token
        /// provider) that need the raw token; the value is sourced from <see cref="ITokenProvider"/>.
        /// </summary>
        public Task<string> GetJwtTokenAsync() => _tokenProvider.GetJwtTokenAsync();

        public async Task PublishSwitchDataAsync(string topic, string payload)
        {
            try
            {
                var normalizedPayload = payload.ToUpperInvariant();
                // The device id segment of the topic identifies the switch (e.g. garge/devices/{name}/set).
                var switchNameForPublish = GargeTopics.TryParseDeviceTopic(topic, out var deviceId, out _, out _)
                    ? deviceId
                    : topic.Split('/')[2];
                lock (_stateLock)
                {
                    if (_lastPublishedSwitchStates.TryGetValue(switchNameForPublish, out var currentState) &&
                        string.Equals(currentState, normalizedPayload, StringComparison.Ordinal))
                    {
                        var sanitizedPayload = normalizedPayload.Replace("\r", "").Replace("\n", "");
                        _logger.LogInformation("Skipping publish for switch {SwitchName} as the state {State} is unchanged.", switchNameForPublish, sanitizedPayload);
                        return;
                    }

                    _lastPublishedSwitchStates[switchNameForPublish] = normalizedPayload;
                    _lastCommandedAt[switchNameForPublish] = (normalizedPayload, DateTime.UtcNow);
                }

                var messagePayload = normalizedPayload;

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

        public async Task HandleSwitchEventAsync(SwitchEvent evt)
        {
            try
            {
                _logger.LogDebug("Received switch event for switch: {SwitchName}", evt.Switch?.Name);

                if (evt.Switch == null)
                {
                    _logger.LogWarning("Switch event payload does not contain a valid switch.");
                    return;
                }

                var switchName = evt.Switch.Name;
                if (!IsValidDeviceId(switchName))
                {
                    _logger.LogWarning("Switch event payload contains invalid switch name: {Name}", switchName);
                    return;
                }

                var state = evt.Value.ToUpperInvariant();

                bool knownSwitch;
                lock (_listsLock)
                {
                    knownSwitch = _switchUniqIds.ContainsKey(switchName);
                }

                var topic = GargeTopics.SetTopic(switchName);

                if (knownSwitch)
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
                _logger.LogError(ex, "Error handling switch event.");
            }
        }
    }
}
