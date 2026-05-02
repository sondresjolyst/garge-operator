using Moq;

namespace garge_operator.Tests;

public class WorkerPollTests : WorkerTestBase
{
    [Fact]
    public async Task DisabledRule_Skips_NoPublish()
    {
        var rule = MakeRule(isEnabled: false);
        SetupRules(rule);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SwitchNotFound_Skips_NoPublish()
    {
        var rule = MakeRule(targetId: 99);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(99)).Returns((Switch?)null);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NonSocketSwitch_Skips_NoPublish()
    {
        var rule = MakeRule(targetId: 10);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(new Switch { Id = 10, Name = "light", Type = "light" });
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TimerExpired_PublishesOff_ClearsTimer()
    {
        var activatedAt = DateTime.UtcNow.AddHours(-3);
        var rule = MakeRule(targetId: 10, timerDurationHours: 2, timerActivatedAt: activatedAt);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/timer-clear");
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "off"), Times.Once);
    }

    [Fact]
    public async Task TimerRunning_PriceAboveThreshold_PublishesOff()
    {
        var activatedAt = DateTime.UtcNow.AddMinutes(-30);
        var rule = MakeRule(targetId: 10, timerDurationHours: 2, timerActivatedAt: activatedAt,
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string> { ["test-socket"] = "on" });
        SetupPrice("NO1", price: 1.0);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "off"), Times.Once);
    }

    [Fact]
    public async Task TimerRunning_PriceBelowThreshold_PublishesOn()
    {
        var activatedAt = DateTime.UtcNow.AddMinutes(-30);
        var rule = MakeRule(targetId: 10, timerDurationHours: 2, timerActivatedAt: activatedAt,
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupPrice("NO1", price: 0.3);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task TimerRunning_StateUnchanged_DoesNotPublish()
    {
        var activatedAt = DateTime.UtcNow.AddMinutes(-30);
        var rule = MakeRule(targetId: 10, timerDurationHours: 2, timerActivatedAt: activatedAt,
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupPrice("NO1", price: 0.3);
        var worker = CreateWorker();

        // First call — records "on" internally
        await worker.PollAutomationsAsync(CancellationToken.None);
        MockMqtt.Invocations.Clear();

        // Second call — no price change, should not re-publish
        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TimerIdle_ConditionMet_StartsTimer_PublishesOn()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, timerDurationHours: 2);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 25);
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/timer-start");
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/triggered");
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task TimerIdle_ConditionNotMet_DoesNotPublish()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, timerDurationHours: 2);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupSensor(5, value: 15);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StandardRule_ConditionMet_PublishesAction()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 25);
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/triggered");
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task StandardRule_ConditionNotMet_DoesNotPublish()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupSensor(5, value: 15);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StandardRule_ActionAlreadyCurrentState_DoesNotPublish()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates)
            .Returns(new Dictionary<string, string> { ["test-socket"] = "on" });
        SetupSensor(5, value: 25);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StandardRule_PriceConditionAnd_BothMet_Publishes()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on",
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1", priceOperator: "AND");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 25);
        SetupPrice("NO1", price: 0.3);
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/triggered");
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task StandardRule_PriceConditionAnd_PriceNotMet_DoesNotPublish()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on",
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1", priceOperator: "AND");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupSensor(5, value: 25);
        SetupPrice("NO1", price: 1.0);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StandardRule_PriceConditionOr_OnlyPriceMet_Publishes()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on",
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1", priceOperator: "OR");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 15); // sensor condition NOT met
        SetupPrice("NO1", price: 0.3); // price condition met
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/triggered");
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }
}
