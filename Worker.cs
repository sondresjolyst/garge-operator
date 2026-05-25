using System.Text.Json;
using garge_operator.Services;
using garge_operator.Dtos.Automation;
using garge_operator.Constants;

public class Worker : BackgroundService
{
    // Reused across all deserialization calls to avoid per-call allocation.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<Worker> _logger;
    private readonly IMqttService _mqttService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    // Caches the last published action per switch, keyed by switch ID.
    private readonly Dictionary<int, string> _lastPublishedActions = new();

    // Caches the electricity price per area. Each entry is valid until the end of its delivery hour.
    private readonly Dictionary<string, (double Value, DateTime ValidUntil)> _priceCache = new();

    public Worker(
        ILogger<Worker> logger,
        IMqttService mqttService,
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

        // Reconcile rule state before entering the poll loop.
        try
        {
            await ReconcileOnStartupAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup reconciliation.");
        }

        // Poll automation rules once per minute. The first tick fires one minute after startup,
        // preserving the prior cadence where reconciliation runs first and the poll loop follows.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollAutomationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during automation polling.");
            }
        }
    }

    /// <summary>
    /// Reconciles rule state at startup: resolves expired timers, re-enforces active timers,
    /// and sets the correct state for non-timed rules. Recovers state after operator downtime.
    /// </summary>
    internal async Task ReconcileOnStartupAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running startup reconciliation pass.");

        var (client, rules) = await FetchRulesAsync(stoppingToken);
        if (client == null || rules == null) return;

        var apiBaseUrl = _configuration["Api:BaseUrl"]!;

        foreach (var rule in rules)
        {
            if (!rule.IsEnabled)
                continue;

            var targetSwitch = _mqttService.GetSwitch(rule.TargetId);
            if (targetSwitch == null) continue;

            if (!new[] { SwitchTypes.Socket }.Contains(targetSwitch.Type, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Skipping rule {RuleId}: switch type '{Type}' is not actionable.", rule.Id, targetSwitch.Type);
                continue;
            }

            var topic = GargeTopics.SetTopic(targetSwitch.Name);

            if (rule.TimerDurationHours.HasValue)
            {
                if (rule.TimerActivatedAt.HasValue)
                {
                    var elapsed = DateTime.UtcNow - rule.TimerActivatedAt.Value.ToUniversalTime();
                    if (elapsed >= TimeSpan.FromHours(rule.TimerDurationHours.Value))
                    {
                        // Timer expired during operator downtime: turn the switch off and clear the timer.
                        _logger.LogInformation("Startup: timer expired for rule {RuleId}, turning OFF and clearing.", rule.Id);
                        await _mqttService.PublishSwitchDataAsync(topic, SwitchActions.Off);
                        await CallApiPatchAsync(client, $"{apiBaseUrl}/api/automation/{rule.Id}/timer-clear", stoppingToken);
                    }
                    else
                    {
                        // Timer still active: re-enforce state, gated by the price condition when set.
                        bool priceOk = true;
                        if (!string.IsNullOrEmpty(rule.ElectricityPriceCondition) &&
                            rule.ElectricityPriceThreshold.HasValue &&
                            !string.IsNullOrEmpty(rule.ElectricityPriceArea))
                        {
                            var price = await GetCurrentPriceAsync(client, apiBaseUrl, rule.ElectricityPriceArea, stoppingToken);
                            if (price.HasValue)
                                priceOk = EvaluateCondition(price.Value, rule.ElectricityPriceCondition, rule.ElectricityPriceThreshold.Value);
                        }

                        var startupDesired = priceOk ? SwitchActions.On : SwitchActions.Off;
                        _logger.LogInformation("Startup: timer active for rule {RuleId}, enforcing '{Action}' (priceOk={PriceOk}).", rule.Id, startupDesired, priceOk);
                        await _mqttService.PublishSwitchDataAsync(topic, startupDesired);
                        _lastPublishedActions[rule.TargetId] = startupDesired;
                    }
                }
                // A null TimerActivatedAt means the rule is idle and requires no startup action.
            }
            else
            {
                // Non-timed rule: evaluate the condition and enforce the correct state.
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

                if (rule.Action.ToLowerInvariant() != SwitchActions.On && rule.Action.ToLowerInvariant() != SwitchActions.Off)
                {
                    _logger.LogWarning("Rule {RuleId} has invalid action '{Action}', skipping.", rule.Id, rule.Action);
                    continue;
                }

                if (!conditionMet)
                {
                    _logger.LogInformation("Startup: rule {RuleId} condition not met, skipping.", rule.Id);
                    continue;
                }

                var desiredAction = rule.Action.ToLowerInvariant();
                _logger.LogInformation("Startup: enforcing '{Action}' for non-timed rule {RuleId}.", desiredAction, rule.Id);
                await _mqttService.PublishSwitchDataAsync(topic, desiredAction);
                _lastPublishedActions[rule.TargetId] = desiredAction;
            }
        }

        _logger.LogInformation("Startup reconciliation complete.");
    }

    internal async Task PollAutomationsAsync(CancellationToken stoppingToken)
    {
        var (client, rules) = await FetchRulesAsync(stoppingToken);
        if (client == null || rules == null) return;

        var apiBaseUrl = _configuration["Api:BaseUrl"]!;

        _logger.LogInformation("Loaded {Count} automation rules.", rules.Count);

        foreach (var rule in rules)
        {
            // Isolate each rule: a failure on one rule (bad data, transient HTTP error, etc.)
            // must not abort the rest of the polling cycle.
            try
            {
            _logger.LogDebug("Automation Rule: {Rule}", JsonSerializer.Serialize(rule));

            if (!rule.IsEnabled)
            {
                _logger.LogInformation("Skipping rule {RuleId} because it is disabled.", rule.Id);
                continue;
            }

            var targetSwitch = _mqttService.GetSwitch(rule.TargetId);
            if (targetSwitch == null)
            {
                _logger.LogWarning("Switch with ID {TargetId} not found in local list.", rule.TargetId);
                continue;
            }

            if (!new[] { SwitchTypes.Socket }.Contains(targetSwitch.Type, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Skipping rule {RuleId}: switch type '{Type}' is not actionable.", rule.Id, targetSwitch.Type);
                continue;
            }

            var topic = GargeTopics.SetTopic(targetSwitch.Name);

            // ── Timed rule handling ───────────────────────────────────────────
            if (rule.TimerDurationHours.HasValue)
            {
                if (rule.TimerActivatedAt.HasValue)
                {
                    var elapsed = DateTime.UtcNow - rule.TimerActivatedAt.Value.ToUniversalTime();
                    if (elapsed >= TimeSpan.FromHours(rule.TimerDurationHours.Value))
                    {
                        _logger.LogInformation("Rule {RuleId}: timer elapsed ({Elapsed:hh\\:mm}), turning OFF.", rule.Id, elapsed);
                        await _mqttService.PublishSwitchDataAsync(topic, SwitchActions.Off);
                        _lastPublishedActions[rule.TargetId] = SwitchActions.Off;
                        await CallApiPatchAsync(client, $"{apiBaseUrl}/api/automation/{rule.Id}/timer-clear", stoppingToken);
                    }
                    else
                    {
                        // Timer still running: gate the socket on the price condition. The sensor only acts as a trigger.
                        bool priceOk = true;
                        if (!string.IsNullOrEmpty(rule.ElectricityPriceCondition) &&
                            rule.ElectricityPriceThreshold.HasValue &&
                            !string.IsNullOrEmpty(rule.ElectricityPriceArea))
                        {
                            var price = await GetCurrentPriceAsync(client, apiBaseUrl, rule.ElectricityPriceArea, stoppingToken);
                            if (price.HasValue)
                                priceOk = EvaluateCondition(price.Value, rule.ElectricityPriceCondition, rule.ElectricityPriceThreshold.Value);
                        }

                        var desired = priceOk ? SwitchActions.On : SwitchActions.Off;
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

                // Timer idle: check the trigger condition.
                var sensorValue = await GetSensorValueAsync(client, apiBaseUrl, rule.SensorId, stoppingToken);
                if (sensorValue == null) continue;

                bool conditionMet = EvaluateSensorCondition(sensorValue.Value, rule.Condition, rule.Threshold);
                conditionMet = await CombineWithPriceConditionAsync(client, apiBaseUrl, rule, conditionMet, stoppingToken);

                if (conditionMet)
                {
                    _logger.LogInformation("Rule {RuleId}: condition met, starting {Duration}h timer, turning ON.", rule.Id, rule.TimerDurationHours.Value);
                    await _mqttService.PublishSwitchDataAsync(topic, SwitchActions.On);
                    _lastPublishedActions[rule.TargetId] = SwitchActions.On;
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
            if (action != SwitchActions.On && action != SwitchActions.Off)
            {
                _logger.LogWarning("Rule {RuleId} has invalid action '{Action}', skipping.", rule.Id, rule.Action);
                continue;
            }
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing automation rule {RuleId}; continuing with remaining rules.", rule.Id);
                continue;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="HttpClient"/> with a fresh Bearer token attached.
    /// Centralizes the token-fetch + factory + Authorization-header boilerplate.
    /// </summary>
    private async Task<HttpClient> CreateAuthorizedClientAsync()
    {
        var client = _httpClientFactory.CreateClient();
        var token = await _mqttService.GetJwtTokenAsync();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(HttpClient? Client, List<AutomationRuleDto>? Rules)> FetchRulesAsync(CancellationToken stoppingToken)
    {
        var apiBaseUrl = _configuration["Api:BaseUrl"];
        var client = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync($"{apiBaseUrl}/api/automation", stoppingToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch automation rules. Status: {StatusCode}", response.StatusCode);
            return (null, null);
        }

        var json = await response.Content.ReadAsStringAsync(stoppingToken);
        var rules = JsonSerializer.Deserialize<List<AutomationRuleDto>>(json, JsonOptions);
        if (rules == null)
        {
            _logger.LogWarning("No automation rules found.");
            return (null, null);
        }

        return (client, rules);
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

        string? valueStr;
        try
        {
            // Guard against malformed JSON or a missing "value" field in the API response,
            // mirroring the guarded price parsing in GetCurrentPriceAsync.
            valueStr = JsonDocument.Parse(sensorJson).RootElement.GetProperty("value").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse latest-data JSON for sensor {SensorId}.", sensorId);
            return null;
        }

        if (!double.TryParse(valueStr, out var sensorValue))
        {
            _logger.LogWarning("Could not parse sensor value '{Value}' for sensor {SensorId}.", valueStr, sensorId);
            return null;
        }

        return sensorValue;
    }

    private static bool EvaluateSensorCondition(double value, string condition, double threshold)
        => EvaluateCondition(value, condition, threshold);

    internal static bool EvaluateCondition(double value, string condition, double threshold) => condition switch
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
            var priceResponse = await client.GetAsync($"{apiBaseUrl}/api/electricity/current-price?area={Uri.EscapeDataString(area)}", stoppingToken);
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
