using Moq;

namespace garge_operator.Tests;

/// <summary>
/// Pins the price-gating logic that is now shared between PollAutomationsAsync and
/// ReconcileOnStartupAsync (refactor #5). Each scenario is run through BOTH entry points using the
/// same setup, asserting identical outcomes so the two paths cannot silently diverge again. The
/// shared helpers under test are EvaluateTimerPriceGateAsync (running timed rules) and
/// CombineWithPriceConditionAsync (non-timed rules).
/// </summary>
public class WorkerSharedPriceLogicTests : WorkerTestBase
{
    public enum Path { Poll, Reconcile }

    private Task Run(Path path, Worker worker) => path switch
    {
        Path.Poll => worker.PollAutomationsAsync(CancellationToken.None),
        Path.Reconcile => worker.ReconcileOnStartupAsync(CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

    // ── Running timed rule: price gate (EvaluateTimerPriceGateAsync) ──────────

    [Theory]
    [InlineData(Path.Poll)]
    [InlineData(Path.Reconcile)]
    public async Task RunningTimer_PriceBelowThreshold_EnforcesOn(Path path)
    {
        var rule = MakeRule(targetId: 10, timerDurationHours: 2,
            timerActivatedAt: DateTime.UtcNow.AddMinutes(-30),
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupPrice("NO1", price: 0.3);

        await Run(path, CreateWorker());

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Theory]
    [InlineData(Path.Poll)]
    [InlineData(Path.Reconcile)]
    public async Task RunningTimer_PriceAboveThreshold_EnforcesOff(Path path)
    {
        var rule = MakeRule(targetId: 10, timerDurationHours: 2,
            timerActivatedAt: DateTime.UtcNow.AddMinutes(-30),
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        // Poll only publishes on change, so seed "on" as the last action via reconcile-like state.
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string> { ["test-socket"] = "on" });
        SetupPrice("NO1", price: 1.0);

        await Run(path, CreateWorker());

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "off"), Times.Once);
    }

    [Theory]
    [InlineData(Path.Poll)]
    [InlineData(Path.Reconcile)]
    public async Task RunningTimer_NoPriceCondition_EnforcesOn(Path path)
    {
        // With no price condition the gate defaults to allow-ON for both paths.
        var rule = MakeRule(targetId: 10, timerDurationHours: 2,
            timerActivatedAt: DateTime.UtcNow.AddMinutes(-30));
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());

        await Run(path, CreateWorker());

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    // ── Non-timed rule: combine (CombineWithPriceConditionAsync) ─────────────

    [Theory]
    [InlineData(Path.Poll)]
    [InlineData(Path.Reconcile)]
    public async Task NonTimed_PriceAnd_BothMet_PublishesOn(Path path)
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on",
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1", priceOperator: "AND");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 25);
        SetupPrice("NO1", price: 0.3);
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/triggered");

        await Run(path, CreateWorker());

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Theory]
    [InlineData(Path.Poll)]
    [InlineData(Path.Reconcile)]
    public async Task NonTimed_PriceAnd_PriceNotMet_DoesNotPublish(Path path)
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on",
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1", priceOperator: "AND");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 25);
        SetupPrice("NO1", price: 1.0);

        await Run(path, CreateWorker());

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(Path.Poll)]
    [InlineData(Path.Reconcile)]
    public async Task NonTimed_PriceOr_OnlyPriceMet_PublishesOn(Path path)
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on",
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1", priceOperator: "OR");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 15); // Sensor condition not met.
        SetupPrice("NO1", price: 0.3); // Price condition met.
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/triggered");

        await Run(path, CreateWorker());

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Theory]
    [InlineData(Path.Poll)]
    [InlineData(Path.Reconcile)]
    public async Task NonTimed_PriceConfigured_PriceFetchFails_DoesNotPublish(Path path)
    {
        // When a price condition is configured but the price endpoint fails, the shared combine
        // helper returns false for BOTH paths (no partial-condition action). No price handler is
        // registered, so the fetch returns 404.
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on",
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1", priceOperator: "AND");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 25);

        await Run(path, CreateWorker());

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
