using System.Text.Json;
using garge_operator.Dtos.Mqtt;
using Moq;

namespace garge_operator.Tests;

/// <summary>
/// Verifies the redelivery of commands a device has not been seen to carry out. A command on a
/// device's set topic is unretained, so one published while no gateway held that device's lease
/// reached nobody and left no trace; the recorded intent is what lets it be sent again once a
/// gateway takes the device over.
/// </summary>
public class WorkerDeviceCommandTests : WorkerTestBase
{
    private const string Target = "wiz_SOCKET_6c2990a96cde";
    private const string Controller = "garge_48ca43597fd8";

    private void SetupPending(params PendingDeviceCommand[] commands)
    {
        MockMqtt.Setup(m => m.GetJwtTokenAsync()).ReturnsAsync("token");
        HttpHandler.OnGet($"{ApiBase}/api/mqtt/devices/pending-commands", JsonSerializer.Serialize(commands));
        HttpHandler.OnPost($"{ApiBase}/api/mqtt/devices/{Uri.EscapeDataString(Target)}/command-attempt");
    }

    private static PendingDeviceCommand Pending(
        string? controller = Controller, string desired = "ON", string? observed = null, int attempts = 0) => new()
    {
        Target = Target,
        DesiredState = desired,
        ObservedState = observed,
        ControllerDeviceName = controller,
        Attempts = attempts,
        DesiredStateAt = DateTime.UtcNow
    };

    [Fact]
    public async Task OutstandingCommand_WithAController_IsRepublished()
    {
        SetupPending(Pending());
        var worker = CreateWorker();

        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        // Forced, because the operator may already believe it sent this state: the earlier
        // publish is exactly the one that reached nobody.
        MockMqtt.Verify(m => m.PublishSwitchDataAsync($"garge/devices/{Target}/set", "ON", true), Times.Once);
    }

    [Fact]
    public async Task OutstandingCommand_WithNoController_IsHeldBack()
    {
        SetupPending(Pending(controller: null));
        var worker = CreateWorker();

        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        // Nobody is subscribed, so publishing would only burn an attempt.
        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task DisagreeingObservedState_IsStillRepublished()
    {
        SetupPending(Pending(desired: "ON", observed: "OFF"));
        var worker = CreateWorker();

        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync($"garge/devices/{Target}/set", "ON", true), Times.Once);
    }

    [Fact]
    public async Task NothingOutstanding_PublishesNothing()
    {
        SetupPending();
        var worker = CreateWorker();

        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task UnreadablePendingList_PublishesNothing()
    {
        MockMqtt.Setup(m => m.GetJwtTokenAsync()).ReturnsAsync("token");
        HttpHandler.OnGet($"{ApiBase}/api/mqtt/devices/pending-commands", "", System.Net.HttpStatusCode.ServiceUnavailable);
        var worker = CreateWorker();

        // A pass that cannot read the list waits for the next one rather than guessing.
        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task AttemptCountFailing_StillLeavesTheCommandPublished()
    {
        MockMqtt.Setup(m => m.GetJwtTokenAsync()).ReturnsAsync("token");
        HttpHandler.OnGet($"{ApiBase}/api/mqtt/devices/pending-commands", JsonSerializer.Serialize(new[] { Pending() }));
        HttpHandler.OnPost($"{ApiBase}/api/mqtt/devices/{Uri.EscapeDataString(Target)}/command-attempt",
            "", System.Net.HttpStatusCode.InternalServerError);
        var worker = CreateWorker();

        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        // Not counting the attempt is better than leaving the device in the wrong state.
        MockMqtt.Verify(m => m.PublishSwitchDataAsync($"garge/devices/{Target}/set", "ON", true), Times.Once);
    }

    [Fact]
    public async Task OneFailingCommand_DoesNotStopTheRest()
    {
        var second = "wiz_SOCKET_aaaaaaaaaaaa";
        MockMqtt.Setup(m => m.GetJwtTokenAsync()).ReturnsAsync("token");
        HttpHandler.OnGet($"{ApiBase}/api/mqtt/devices/pending-commands", JsonSerializer.Serialize(new[]
        {
            Pending(),
            new PendingDeviceCommand
            {
                Target = second, DesiredState = "OFF", ControllerDeviceName = Controller, DesiredStateAt = DateTime.UtcNow
            }
        }));
        HttpHandler.OnPost($"{ApiBase}/api/mqtt/devices/{Uri.EscapeDataString(Target)}/command-attempt");
        HttpHandler.OnPost($"{ApiBase}/api/mqtt/devices/{Uri.EscapeDataString(second)}/command-attempt");
        MockMqtt.Setup(m => m.PublishSwitchDataAsync($"garge/devices/{Target}/set", "ON", true))
            .ThrowsAsync(new InvalidOperationException("broker unreachable"));
        var worker = CreateWorker();

        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        // One unreachable device must not hold the others until the next pass.
        MockMqtt.Verify(m => m.PublishSwitchDataAsync($"garge/devices/{second}/set", "OFF", true), Times.Once);
    }

    [Fact]
    public async Task MalformedPendingList_PublishesNothing()
    {
        MockMqtt.Setup(m => m.GetJwtTokenAsync()).ReturnsAsync("token");
        HttpHandler.OnGet($"{ApiBase}/api/mqtt/devices/pending-commands", "{not json");
        var worker = CreateWorker();

        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ControlLists_ArePublishedPerGateway()
    {
        MockMqtt.Setup(m => m.GetJwtTokenAsync()).ReturnsAsync("token");
        HttpHandler.OnGet($"{ApiBase}/api/mqtt/devices/controls", JsonSerializer.Serialize(new[]
        {
            new { GatewayDeviceName = Controller, Targets = new[] { Target } },
            new { GatewayDeviceName = "garge_ffffffffffff", Targets = Array.Empty<string>() }
        }));
        var worker = CreateWorker();

        await worker.PublishDeviceControlsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishDeviceControlsAsync(Controller, It.Is<IReadOnlyList<string>>(t => t.Count == 1 && t[0] == Target)), Times.Once);
        // An empty list is still published: it is how a standby learns it controls nothing.
        MockMqtt.Verify(m => m.PublishDeviceControlsAsync("garge_ffffffffffff", It.Is<IReadOnlyList<string>>(t => t.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task UnreadableControlLists_PublishNothing()
    {
        MockMqtt.Setup(m => m.GetJwtTokenAsync()).ReturnsAsync("token");
        HttpHandler.OnGet($"{ApiBase}/api/mqtt/devices/controls", "", System.Net.HttpStatusCode.BadGateway);
        var worker = CreateWorker();

        await worker.PublishDeviceControlsAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishDeviceControlsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [Fact]
    public async Task EachOutstandingCommand_CountsOneAttempt()
    {
        var attempts = 0;
        SetupPending(Pending());
        HttpHandler.OnMatched = url =>
        {
            if (url.EndsWith("/command-attempt", StringComparison.Ordinal))
            {
                attempts++;
            }
        };
        var worker = CreateWorker();

        await worker.ReconcileDeviceCommandsAsync(CancellationToken.None);

        // Counted so an impossible command is abandoned instead of retried forever.
        Assert.Equal(1, attempts);
    }
}
