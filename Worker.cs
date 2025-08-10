using System.Text.Json;
using garge_operator.Services;
using garge_operator.Dtos.Automation;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly MqttService _mqttService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    // Cache last published action per switchId
    private readonly Dictionary<int, string> _lastPublishedActions = new();

    public Worker(
        ILogger<Worker> logger,
        MqttService mqttService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _mqttService = mqttService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _mqttService.ConnectAsync();

        var lastAutomationPoll = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);

            if (DateTimeOffset.Now - lastAutomationPoll > TimeSpan.FromMinutes(1))
            {
                try
                {
                    await PollAutomationsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during automation polling.");
                }
                lastAutomationPoll = DateTimeOffset.Now;
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task PollAutomationsAsync(CancellationToken stoppingToken)
    {
        var client = _httpClientFactory.CreateClient();
        var apiBaseUrl = _configuration["Api:BaseUrl"];
        var token = await _mqttService.GetJwtTokenAsync();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Get automation rules
        var response = await client.GetAsync($"{apiBaseUrl}/api/automation", stoppingToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch automation rules. Status: {StatusCode}", response.StatusCode);
            return;
        }

        var json = await response.Content.ReadAsStringAsync(stoppingToken);
        var doc = JsonDocument.Parse(json);
        var rules = doc.RootElement.TryGetProperty("$values", out var valuesElement)
            ? JsonSerializer.Deserialize<List<AutomationRuleDto>>(valuesElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            : null;
        if (rules == null)
        {
            _logger.LogWarning("No automation rules found.");
            return;
        }

        _logger.LogInformation("Loaded {Count} automation rules.", rules.Count);

        foreach (var rule in rules)
        {
            _logger.LogInformation("Automation Rule: {Rule}", JsonSerializer.Serialize(rule));

            if (!string.Equals(rule.TargetType, "Switch", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Skipping rule {RuleId} because TargetType is not 'Switch'.", rule.Id);
                continue;
            }

            // Get latest sensor value
            var sensorResponse = await client.GetAsync($"{apiBaseUrl}/api/sensors/{rule.SensorId}/latest-data", stoppingToken);
            if (!sensorResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch latest data for sensor {SensorId}. Status: {StatusCode}", rule.SensorId, sensorResponse.StatusCode);
                continue;
            }

            var sensorJson = await sensorResponse.Content.ReadAsStringAsync(stoppingToken);
            var sensorData = JsonDocument.Parse(sensorJson).RootElement;
            var valueStr = sensorData.GetProperty("value").GetString();
            if (!double.TryParse(valueStr, out var sensorValue))
            {
                _logger.LogWarning("Could not parse sensor value '{Value}' for sensor {SensorId}.", valueStr, rule.SensorId);
                continue;
            }

            _logger.LogInformation("Evaluating rule {RuleId}: sensorValue={SensorValue}, condition={Condition}, threshold={Threshold}, action={Action}", rule.Id, sensorValue, rule.Condition, rule.Threshold, rule.Action);

            // Evaluate condition
            bool conditionMet = rule.Condition switch
            {
                "<" => sensorValue < rule.Threshold,
                ">" => sensorValue > rule.Threshold,
                "<=" => sensorValue <= rule.Threshold,
                ">=" => sensorValue >= rule.Threshold,
                "==" => sensorValue == rule.Threshold,
                "!=" => sensorValue != rule.Threshold,
                _ => false
            };

            _logger.LogInformation("Rule {RuleId} conditionMet={ConditionMet}", rule.Id, conditionMet);

            if (!conditionMet)
            {
                _logger.LogInformation("Rule {RuleId} condition not met. Skipping.", rule.Id);
                continue;
            }

            // Find switch name by TargetId
            var switchObj = _mqttService
                .GetType()
                .GetField("_switches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(_mqttService) as List<Switch>;
            if (switchObj == null)
            {
                _logger.LogWarning("Could not access _switches list in MqttService.");
                continue;
            }
            var targetSwitch = switchObj.FirstOrDefault(s => s.Id == rule.TargetId);

            if (targetSwitch == null)
            {
                _logger.LogWarning("Switch with ID {TargetId} not found in local list.", rule.TargetId);
                continue;
            }
            else
            {
                _logger.LogInformation("Found switch {SwitchName} (ID: {TargetId}) for rule {RuleId}.", targetSwitch.Name, rule.TargetId, rule.Id);
            }

            var topic = $"garge/devices/{targetSwitch.Name}/set";
            var action = rule.Action.ToLowerInvariant();

            // Check current state from MqttService
            var currentStateDict = _mqttService.LastPublishedSwitchStates;
            var currentState = currentStateDict.TryGetValue(targetSwitch.Name, out var state) ? state.ToLowerInvariant() : null;

            // Only publish if action is different from current state
            if (currentState == action)
            {
                _logger.LogInformation("Skipping publish for switch {SwitchName} (ID: {TargetId}) as action '{Action}' is unchanged (actual state: {CurrentState}).", targetSwitch.Name, rule.TargetId, action, currentState);
                continue;
            }

            _logger.LogInformation("Publishing action '{Action}' to switch {SwitchName} (ID: {TargetId}) due to automation rule {RuleId}.", action, targetSwitch.Name, rule.TargetId, rule.Id);
            await _mqttService.PublishSwitchDataAsync(topic, action);
            _lastPublishedActions[rule.TargetId] = action;
            _logger.LogInformation("Published action '{Action}' to switch {SwitchName} (ID: {TargetId}) due to automation rule {RuleId}.", action, targetSwitch.Name, rule.TargetId, rule.Id);
        }
    }
}
