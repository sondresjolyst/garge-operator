using Moq;

namespace garge_operator.Tests;

public class WorkerReconcileTests : WorkerTestBase
{
    [Fact]
    public async Task TimerExpiredWhileDown_PublishesOff_ClearsTimer()
    {
        var activatedAt = DateTime.UtcNow.AddHours(-5);
        var rule = MakeRule(targetId: 10, timerDurationHours: 2, timerActivatedAt: activatedAt);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/timer-clear");
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "off"), Times.Once);
    }

    [Fact]
    public async Task TimerStillActive_PriceOk_EnforcesOn()
    {
        var activatedAt = DateTime.UtcNow.AddMinutes(-30);
        var rule = MakeRule(targetId: 10, timerDurationHours: 2, timerActivatedAt: activatedAt,
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupPrice("NO1", price: 0.3);
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task TimerStillActive_PriceNotOk_EnforcesOff()
    {
        var activatedAt = DateTime.UtcNow.AddMinutes(-30);
        var rule = MakeRule(targetId: 10, timerDurationHours: 2, timerActivatedAt: activatedAt,
            priceCondition: "<", priceThreshold: 0.5, priceArea: "NO1");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupPrice("NO1", price: 1.0);
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "off"), Times.Once);
    }

    [Fact]
    public async Task TimerIdle_NoAction()
    {
        var rule = MakeRule(targetId: 10, timerDurationHours: 2, timerActivatedAt: null);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NonTimedRule_ConditionMet_EnforcesAction()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupSensor(5, value: 25);
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task NonTimedRule_ConditionNotMet_NoPublish()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupSensor(5, value: 15); // condition not met → leave switch alone
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisabledRule_Skips_NoPublish()
    {
        var rule = MakeRule(isEnabled: false);
        SetupRules(rule);
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
