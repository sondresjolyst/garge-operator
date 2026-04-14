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

    // Cache current electricity price per area (keyed by area, valid for current delivery hour)
    private readonly Dictionary<string, (double Value, DateTime ValidUntil)> _priceCache = new();

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

        // Reconcile rule state on startup before entering the poll loop
        try
        {
            await ReconcileOnStartupAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup reconciliation.");
        }

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

    /// <summary>
    /// On startup: immediately resolve any expired timers, re-enforce active timers,
    /// and set correct state for non-timed rules — handles the operator-was-down case.
    /// </summary>
    private async Task ReconcileOnStartupAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running startup reconciliation pass.");

        var (client, rules) = await FetchRulesAsync(stoppingToken);
        if (client == null || rules == null) return;

        var apiBaseUrl = _configuration["Api:BaseUrl"]!;

        foreach (var rule in rules)
        {
            if (!string.Equals(rule.TargetType, "Switch", StringComparison.OrdinalIgnoreCase) || !rule.IsEnabled)
                continue;

            var targetSwitch = GetSwitch(rule.TargetId);
            if (targetSwitch == null) continue;

            var topic = $"garge/devices/{targetSwitch.Name}/set";

            if (rule.TimerDurationHours.HasValue)
            {
                if (rule.TimerActivatedAt.HasValue)
                {
                    var elapsed = DateTime.UtcNow - rule.TimerActivatedAt.Value.ToUniversalTime();
                    if (elapsed >= TimeSpan.FromHours(rule.TimerDurationHours.Value))
                    {
                        // Timer expired while operator was down — turn OFF and clear
                        _logger.LogInformation("Startup: timer expired for rule {RuleId}, turning OFF and clearing.", rule.Id);
                        await _mqttService.PublishSwitchDataAsync(topic, "off");
                        await CallApiPatchAsync(client, $"{apiBaseUrl}/api/automation/{rule.Id}/timer-clear", stoppingToken);
                    }
                    else
                    {
                        // Timer still active — re-enforce state, gated by price condition if set
                        bool priceOk = true;
                        if (!string.IsNullOrEmpty(rule.ElectricityPriceCondition) &&
                            rule.ElectricityPriceThreshold.HasValue &&
                            !string.IsNullOrEmpty(rule.ElectricityPriceArea))
                        {
                            var price = await GetCurrentPriceAsync(client, apiBaseUrl, rule.ElectricityPriceArea, stoppingToken);
                            if (price.HasValue)
                                priceOk = EvaluateCondition(price.Value, rule.ElectricityPriceCondition, rule.ElectricityPriceThreshold.Value);
                        }

                        var startupDesired = priceOk ? "on" : "off";
                        _logger.LogInformation("Startup: timer active for rule {RuleId}, enforcing '{Action}' (priceOk={PriceOk}).", rule.Id, startupDesired, priceOk);
                        await _mqttService.PublishSwitchDataAsync(topic, startupDesired);
                        _lastPublishedActions[rule.TargetId] = startupDesired;
                    }
                }
                // If TimerActivatedAt is null the rule is idle — no action needed on startup
            }
            else
            {
                // Non-timed rule: evaluate condition and enforce correct state
                var sensorValue = await GetSensorValueAsync(client, apiBaseUrl, rule.SensorId, stoppingToken);
                if (sensorValue == null) continue;

                bool conditionMet = EvaluateSensorCondition(sensorValue.Value, rule.Condition, rule.Threshold);

                if (!string.IsNullOrEmpty(rule.ElectricityPriceCondition) &&
                    rule.ElectricityPriceThreshold.HasValue &&
                    !string.IsNullOrEmpty(rule.ElectricityPriceArea))
                {
                    var price = await GetCurrentPriceAsync(client, apiBaseUrl, rule.ElectricityPriceArea, stoppingToken);
                    if (price.HasValue)
                    {
                        var priceConditionMet = EvaluateCondition(price.Value, rule.ElectricityPriceCondition, rule.ElectricityPriceThreshold!.Value);
                        var logicalOp = (rule.ElectricityPriceOperator ?? "AND").ToUpperInvariant();
                        conditionMet = logicalOp == "OR" ? conditionMet || priceConditionMet : conditionMet && priceConditionMet;
                    }
                }

                var desiredAction = conditionMet ? rule.Action.ToLowerInvariant() : (rule.Action.ToLowerInvariant() == "on" ? "off" : "on");
                _logger.LogInformation("Startup: enforcing '{Action}' for non-timed rule {RuleId}.", desiredAction, rule.Id);
                await _mqttService.PublishSwitchDataAsync(topic, desiredAction);
                _lastPublishedActions[rule.TargetId] = desiredAction;
            }
        }

        _logger.LogInformation("Startup reconciliation complete.");
    }

    private async Task PollAutomationsAsync(CancellationToken stoppingToken)
    {
        var (client, rules) = await FetchRulesAsync(stoppingToken);
        if (client == null || rules == null) return;

        var apiBaseUrl = _configuration["Api:BaseUrl"]!;

        _logger.LogInformation("Loaded {Count} automation rules.", rules.Count);

        foreach (var rule in rules)
        {
            _logger.LogInformation("Automation Rule: {Rule}", JsonSerializer.Serialize(rule));

            if (!string.Equals(rule.TargetType, "Switch", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Skipping rule {RuleId} because TargetType is not 'Switch'.", rule.Id);
                continue;
            }

            if (!rule.IsEnabled)
            {
                _logger.LogInformation("Skipping rule {RuleId} because it is disabled.", rule.Id);
                continue;
            }

            var targetSwitch = GetSwitch(rule.TargetId);
            if (targetSwitch == null)
            {
                _logger.LogWarning("Switch with ID {TargetId} not found in local list.", rule.TargetId);
                continue;
            }

            var topic = $"garge/devices/{targetSwitch.Name}/set";

            // ── Timed rule handling ───────────────────────────────────────────
            if (rule.TimerDurationHours.HasValue)
            {
                if (rule.TimerActivatedAt.HasValue)
                {
                    var elapsed = DateTime.UtcNow - rule.TimerActivatedAt.Value.ToUniversalTime();
                    if (elapsed >= TimeSpan.FromHours(rule.TimerDurationHours.Value))
                    {
                        _logger.LogInformation("Rule {RuleId}: timer elapsed ({Elapsed:hh\\:mm}), turning OFF.", rule.Id, elapsed);
                        await _mqttService.PublishSwitchDataAsync(topic, "off");
                        _lastPublishedActions[rule.TargetId] = "off";
                        await CallApiPatchAsync(client, $"{apiBaseUrl}/api/automation/{rule.Id}/timer-clear", stoppingToken);
                    }
                    else
                    {
                        // Timer still running — gate socket by price condition (sensor is trigger-only)
                        bool priceOk = true;
                        if (!string.IsNullOrEmpty(rule.ElectricityPriceCondition) &&
                            rule.ElectricityPriceThreshold.HasValue &&
                            !string.IsNullOrEmpty(rule.ElectricityPriceArea))
                        {
                            var price = await GetCurrentPriceAsync(client, apiBaseUrl, rule.ElectricityPriceArea, stoppingToken);
                            if (price.HasValue)
                                priceOk = EvaluateCondition(price.Value, rule.ElectricityPriceCondition, rule.ElectricityPriceThreshold.Value);
                        }

                        var desired = priceOk ? "on" : "off";
                        var last = _lastPublishedActions.GetValueOrDefault(rule.TargetId);
                        if (last != desired)
                        {
                            _logger.LogInformation("Rule {RuleId}: timer running, price gate changed → '{Action}'.", rule.Id, desired);
                            await _mqttService.PublishSwitchDataAsync(topic, desired);
                            _lastPublishedActions[rule.TargetId] = desired;
                        }
                    }
                    continue;
                }

                // Timer is idle — check trigger condition
                var sensorValue = await GetSensorValueAsync(client, apiBaseUrl, rule.SensorId, stoppingToken);
                if (sensorValue == null) continue;

                bool conditionMet = EvaluateSensorCondition(sensorValue.Value, rule.Condition, rule.Threshold);
                conditionMet = await CombineWithPriceConditionAsync(client, apiBaseUrl, rule, conditionMet, stoppingToken);

                if (conditionMet)
                {
                    _logger.LogInformation("Rule {RuleId}: condition met, starting {Duration}h timer, turning ON.", rule.Id, rule.TimerDurationHours.Value);
                    await _mqttService.PublishSwitchDataAsync(topic, "on");
                    _lastPublishedActions[rule.TargetId] = "on";
                    await CallApiPatchAsync(client, $"{apiBaseUrl}/api/automation/{rule.Id}/timer-start", stoppingToken);
                    await MarkTriggeredAsync(client, apiBaseUrl, rule.Id, stoppingToken);
                }
                continue;
            }

            // ── Standard (non-timed) rule handling ───────────────────────────
            var stdSensorValue = await GetSensorValueAsync(client, apiBaseUrl, rule.SensorId, stoppingToken);
            if (stdSensorValue == null) continue;

            bool stdConditionMet = EvaluateSensorCondition(stdSensorValue.Value, rule.Condition, rule.Threshold);
            stdConditionMet = await CombineWithPriceConditionAsync(client, apiBaseUrl, rule, stdConditionMet, stoppingToken);

            _logger.LogInformation("Rule {RuleId} conditionMet={ConditionMet}", rule.Id, stdConditionMet);

            if (!stdConditionMet)
            {
                _logger.LogInformation("Rule {RuleId} condition not met. Skipping.", rule.Id);
                continue;
            }

            var action = rule.Action.ToLowerInvariant();
            var currentStateDict = _mqttService.LastPublishedSwitchStates;
            var currentState = currentStateDict.TryGetValue(targetSwitch.Name, out var state) ? state.ToLowerInvariant() : null;

            if (currentState == action)
            {
                _logger.LogInformation("Skipping publish for switch {SwitchName} as action '{Action}' is unchanged.", targetSwitch.Name, action);
                continue;
            }

            _logger.LogInformation("Publishing action '{Action}' to switch {SwitchName} due to rule {RuleId}.", action, targetSwitch.Name, rule.Id);
            await _mqttService.PublishSwitchDataAsync(topic, action);
            _lastPublishedActions[rule.TargetId] = action;
            await MarkTriggeredAsync(client, apiBaseUrl, rule.Id, stoppingToken);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(HttpClient? Client, List<AutomationRuleDto>? Rules)> FetchRulesAsync(CancellationToken stoppingToken)
    {
        var client = _httpClientFactory.CreateClient();
        var apiBaseUrl = _configuration["Api:BaseUrl"];
        var token = await _mqttService.GetJwtTokenAsync();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"{apiBaseUrl}/api/automation", stoppingToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch automation rules. Status: {StatusCode}", response.StatusCode);
            return (null, null);
        }

        var json = await response.Content.ReadAsStringAsync(stoppingToken);
        var rules = JsonSerializer.Deserialize<List<AutomationRuleDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (rules == null)
        {
            _logger.LogWarning("No automation rules found.");
            return (null, null);
        }

        return (client, rules);
    }

    private Switch? GetSwitch(int targetId)
    {
        var switches = _mqttService
            .GetType()
            .GetField("_switches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(_mqttService) as List<Switch>;
        return switches?.FirstOrDefault(s => s.Id == targetId);
    }

    private async Task<double?> GetSensorValueAsync(HttpClient client, string apiBaseUrl, int sensorId, CancellationToken stoppingToken)
    {
        var sensorResponse = await client.GetAsync($"{apiBaseUrl}/api/sensors/{sensorId}/latest-data", stoppingToken);
        if (!sensorResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch latest data for sensor {SensorId}. Status: {StatusCode}", sensorId, sensorResponse.StatusCode);
            return null;
        }

        var sensorJson = await sensorResponse.Content.ReadAsStringAsync(stoppingToken);
        var valueStr = JsonDocument.Parse(sensorJson).RootElement.GetProperty("value").GetString();
        if (!double.TryParse(valueStr, out var sensorValue))
        {
            _logger.LogWarning("Could not parse sensor value '{Value}' for sensor {SensorId}.", valueStr, sensorId);
            return null;
        }

        return sensorValue;
    }

    private static bool EvaluateSensorCondition(double value, string condition, double threshold)
        => EvaluateCondition(value, condition, threshold);

    private static bool EvaluateCondition(double value, string condition, double threshold) => condition switch
    {
        "<"  => value < threshold,
        ">"  => value > threshold,
        "<=" => value <= threshold,
        ">=" => value >= threshold,
        "==" => value == threshold,
        "!=" => value != threshold,
        _    => false
    };

    private async Task<bool> CombineWithPriceConditionAsync(HttpClient client, string apiBaseUrl, AutomationRuleDto rule, bool sensorConditionMet, CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(rule.ElectricityPriceCondition) ||
            !rule.ElectricityPriceThreshold.HasValue ||
            string.IsNullOrEmpty(rule.ElectricityPriceArea))
            return sensorConditionMet;

        var currentPrice = await GetCurrentPriceAsync(client, apiBaseUrl, rule.ElectricityPriceArea, stoppingToken);
        if (!currentPrice.HasValue)
        {
            _logger.LogWarning("Rule {RuleId}: could not fetch electricity price for area {Area}, skipping.", rule.Id, rule.ElectricityPriceArea);
            return false;
        }

        var priceConditionMet = EvaluateCondition(currentPrice.Value, rule.ElectricityPriceCondition, rule.ElectricityPriceThreshold.Value);
        _logger.LogInformation("Rule {RuleId} priceConditionMet={Met} (price={Price})", rule.Id, priceConditionMet, currentPrice.Value);

        var logicalOp = (rule.ElectricityPriceOperator ?? "AND").ToUpperInvariant();
        return logicalOp == "OR" ? sensorConditionMet || priceConditionMet : sensorConditionMet && priceConditionMet;
    }

    private async Task<double?> GetCurrentPriceAsync(HttpClient client, string apiBaseUrl, string area, CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        if (_priceCache.TryGetValue(area, out var cached) && cached.ValidUntil > now)
            return cached.Value;

        try
        {
            var priceResponse = await client.GetAsync($"{apiBaseUrl}/api/electricity/current-price?area={area}", stoppingToken);
            if (!priceResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch current price for area {Area}. Status: {StatusCode}", area, priceResponse.StatusCode);
                return null;
            }

            var priceJson = await priceResponse.Content.ReadAsStringAsync(stoppingToken);
            var priceDoc = JsonDocument.Parse(priceJson).RootElement;
            var value = priceDoc.GetProperty("value").GetDouble();
            var deliveryEnd = priceDoc.GetProperty("deliveryEnd").GetDateTime().ToUniversalTime();

            _priceCache[area] = (value, deliveryEnd);
            return value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching current price for area {Area}.", area);
            return null;
        }
    }

    private async Task CallApiPatchAsync(HttpClient client, string url, CancellationToken stoppingToken)
    {
        try
        {
            var response = await client.PatchAsync(url, null, stoppingToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("PATCH {Url} failed with status {StatusCode}.", url, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not PATCH {Url}.", url);
        }
    }

    private async Task MarkTriggeredAsync(HttpClient client, string apiBaseUrl, int ruleId, CancellationToken stoppingToken)
        => await CallApiPatchAsync(client, $"{apiBaseUrl}/api/automation/{ruleId}/triggered", stoppingToken);
}
