using System.Net;
using Moq;

namespace garge_operator.Tests;

/// <summary>
/// Tests for the timer-start ordering / idempotency fix (refactor #4).
///
/// Before: a timed rule firing published "on" first and only then called the API timer-start.
/// A crash between those two steps left the switch physically ON while the API had no
/// TimerActivatedAt, so the next poll re-evaluated the trigger and double-triggered.
///
/// After: the operator calls timer-start first and publishes "on" only if timer-start succeeded.
/// A crash after timer-start but before the publish leaves the API with TimerActivatedAt set, so
/// the next poll takes the idempotent "timer running" branch instead of re-triggering.
/// </summary>
public class WorkerTimerIdempotencyTests : WorkerTestBase
{
    [Fact]
    public async Task TimerIdle_ConditionMet_OrderingIsTimerStartThenPublish()
    {
        // Single shared event log captures both the API timer-start and the MQTT publish so their
        // relative order can be asserted directly.
        var events = new List<string>();

        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, timerDurationHours: 2);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 25);

        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/timer-start");
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/triggered");

        // Record the API timer-start and the MQTT publish into the same ordered log.
        HttpHandler.OnMatched = url =>
        {
            if (url == $"{ApiBase}/api/automation/1/timer-start") events.Add("timer-start");
        };
        MockMqtt.Setup(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"))
            .Callback(() => events.Add("publish:on"))
            .Returns(Task.CompletedTask);

        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        // timer-start must be recorded before the publish (the behavior change).
        Assert.Equal(new[] { "timer-start", "publish:on" }, events);
    }

    [Fact]
    public async Task TimerIdle_ConditionMet_TimerStartFails_DoesNotPublish()
    {
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, timerDurationHours: 2);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        SetupSensor(5, value: 25);
        // timer-start fails: the switch must NOT be turned on, keeping device and API state consistent.
        HttpHandler.OnPatch($"{ApiBase}/api/automation/1/timer-start", HttpStatusCode.InternalServerError);
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        // triggered must not be marked either, since nothing was actually triggered.
        Assert.DoesNotContain(HttpHandler.MatchedRequests, r => r.Contains("/triggered"));
    }

    [Fact]
    public async Task CrashAfterTimerStartBeforePublish_NextPoll_DoesNotDoubleTrigger()
    {
        // Models the post-crash state the new ordering guarantees: timer-start succeeded (so the API
        // now reports TimerActivatedAt), but the process died before the publish. On the next poll
        // the rule has a non-null TimerActivatedAt within its window, so it enters the idempotent
        // "timer running" branch — it must NOT call timer-start again or re-mark triggered.
        var activatedAt = DateTime.UtcNow.AddMinutes(-1);
        var rule = MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20,
            timerDurationHours: 2, timerActivatedAt: activatedAt);
        SetupRules(rule);
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        MockMqtt.Setup(m => m.LastPublishedSwitchStates).Returns(new Dictionary<string, string>());
        var worker = CreateWorker();

        await worker.PollAutomationsAsync(CancellationToken.None);

        // It idempotently enforces the desired (no price gate → ON) state once...
        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
        // ...but it must NOT re-trigger: no second timer-start and no triggered mark.
        Assert.DoesNotContain(HttpHandler.MatchedRequests, r => r.Contains("/timer-start"));
        Assert.DoesNotContain(HttpHandler.MatchedRequests, r => r.Contains("/triggered"));
    }
}
