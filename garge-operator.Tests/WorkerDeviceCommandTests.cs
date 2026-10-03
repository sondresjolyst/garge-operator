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
