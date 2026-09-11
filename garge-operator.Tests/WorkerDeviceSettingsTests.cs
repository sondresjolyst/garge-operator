using System.Net;

namespace garge_operator.Tests;

public class WorkerDeviceSettingsTests : WorkerTestBase
{
    private const string DeviceSettingsUrl = $"{ApiBase}/api/sensors/device-settings";

    private const string DeviceSettingsJson =
        """[{"sensorId":7,"deviceName":"garge_0a1b2c3d4e5f","sleepSeconds":600,"securityEnabled":true,"floorMillivolts":12550,"version":1757337600123},{"sensorId":9,"deviceName":"garge_6a7b8c9d0e1f","sleepSeconds":3600,"securityEnabled":false,"floorMillivolts":null,"version":1757337600456}]""";

    [Fact]
    public async Task Reconcile_RepublishesDeviceSettings()
    {
        SetupRules();
        HttpHandler.OnGet(DeviceSettingsUrl, DeviceSettingsJson);
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.HandleDeviceSettingsEventAsync(It.Is<DeviceSettingsEvent>(e => e.DeviceName == "garge_0a1b2c3d4e5f")), Times.Once);
        MockMqtt.Verify(m => m.HandleDeviceSettingsEventAsync(It.Is<DeviceSettingsEvent>(e => e.DeviceName == "garge_6a7b8c9d0e1f")), Times.Once);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "boom")]
    [InlineData(HttpStatusCode.OK, "{ this is not valid json")]
    public async Task Reconcile_DeviceSettingsFetchFails_StillReconcilesRules(HttpStatusCode status, string content)
    {
        HttpHandler.OnGet(DeviceSettingsUrl, content, status);
        SetupRules(MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on"));
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupSensor(5, value: 25);
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.HandleDeviceSettingsEventAsync(It.IsAny<DeviceSettingsEvent>()), Times.Never);
        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task Reconcile_RepublishThrows_StillReconcilesRules()
    {
        HttpHandler.OnGet(DeviceSettingsUrl, DeviceSettingsJson);
        MockMqtt.Setup(m => m.HandleDeviceSettingsEventAsync(It.IsAny<DeviceSettingsEvent>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        SetupRules(MakeRule(targetId: 10, sensorId: 5, condition: ">", threshold: 20, action: "on"));
        MockMqtt.Setup(m => m.GetSwitch(10)).Returns(MakeSocket());
        SetupSensor(5, value: 25);
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.PublishSwitchDataAsync("garge/devices/test-socket/set", "on"), Times.Once);
    }

    [Fact]
    public async Task Reconcile_RulesFetchFails_StillRepublishesDeviceSettings()
    {
        HttpHandler.OnGet($"{ApiBase}/api/automation", "boom", HttpStatusCode.InternalServerError);
        HttpHandler.OnGet(DeviceSettingsUrl, DeviceSettingsJson);
        var worker = CreateWorker();

        await worker.ReconcileOnStartupAsync(CancellationToken.None);

        MockMqtt.Verify(m => m.HandleDeviceSettingsEventAsync(It.IsAny<DeviceSettingsEvent>()), Times.Exactly(2));
    }
}
