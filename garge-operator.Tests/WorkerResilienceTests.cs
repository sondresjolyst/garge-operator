using Moq;

namespace garge_operator.Tests;

public class WorkerResilienceTests : WorkerTestBase
{
    [Fact]
    public async Task SensorLatestData_MalformedJson_DoesNotThrow_DoesNotPublish()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        // Malformed JSON from the latest-data endpoint must be swallowed (returns null), not throw.
        HttpHandler.OnGet($"{ApiBase}/api/sensors/5/latest-data", "{ this is not valid json");
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SensorLatestData_MissingValueField_DoesNotThrow_DoesNotPublish()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on");
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        // Valid JSON but no "value" property: the GetProperty guard must catch and return null.
        HttpHandler.OnGet($"{ApiBase}/api/sensors/5/latest-data", "{\"notValue\":\"123\"}");
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BadRule_DoesNotAbortCycle_RemainingRulesStillProcessed()
    {
        // Rule 1 targets a switch whose lookup throws; rule 2 is a valid standard rule.
        var badRule = MakeRule(id: 1, targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on");
        var goodRule = MakeRule(id: 2, targetId: 20, sensorId: 6, condition: ">", threshold: 20, action: "on");
        SetupRules(badRule, goodRule);

        MockMqtt.Setup(m => m.GetSwitch(10)).Throws(new InvalidOperationException("boom"));
        MockMqtt.Setup(m => m.GetSwitch(20)).Returns(new Switch { Id = 20, Name = "good-socket", Type = "socket" });
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(6, value: 25);
        HttpHandler.OnPatch($"{ApiBase}/api/automation/2/triggered");
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        // The bad rule threw, but the good rule must still publish.
        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/good-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task BadRule_FollowedByGoodRule_DoesNotThrowOutOfPoll()
    {
        var badRule = MakeRule(id: 1, targetId: 10);
        var goodRule = MakeRule(id: 2, targetId: 20, sensorId: 6, condition: ">", threshold: 20, action: "on");
        SetupRules(badRule, goodRule);

        MockMqtt.Setup(m => m.GetSwitch(10)).Throws(new InvalidOperationException("boom"));
        MockMqtt.Setup(m => m.GetSwitch(20)).Returns(new Switch { Id = 20, Name = "good-socket", Type = "socket" });
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(6, value: 25);
        HttpHandler.OnPatch($"{ApiBase}/api/automation/2/triggered");
        var worker = CreateWorker();

        // Must complete without an exception escaping the poll method.
        var ex = await Record.ExceptionAsync(() => worker.PollAutomationsAsync(CancellationToken.None));

        Assert.Null(ex);
    }
}
