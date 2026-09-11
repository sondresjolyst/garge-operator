using System.Text;
using garge_operator.Services;
using MQTTnet;
using MQTTnet.Protocol;

namespace garge_operator.Tests;

public class MqttServiceDeviceSettingsTests : MqttServiceTestBase
{
    private const string DeviceName = "garge_0a1b2c3d4e5f";

    private static DeviceSettingsEvent MakeEvent(string deviceName = DeviceName, long version = 1757337600123) => new()
    {
        SensorId = 7,
        DeviceName = deviceName,
        SleepSeconds = 600,
        SecurityEnabled = true,
        FloorMillivolts = 12550,
        Version = version,
    };

    private List<string> CapturePublishedPayloads()
    {
        var payloads = new List<string>();
        MockClient.Setup(c => c.EnqueueAsync(It.IsAny<MqttApplicationMessage>()))
            .Callback<MqttApplicationMessage>(m => payloads.Add(Encoding.UTF8.GetString(m.PayloadSegment)))
            .Returns(Task.CompletedTask);
        return payloads;
    }

    [Fact]
    public void BuildSettingsPayload_ProducesCompactWireFormat()
    {
        var payload = MqttService.BuildSettingsPayload(MakeEvent());

        Assert.Equal("{\"sleep_s\":600,\"security\":true,\"floor_mv\":12550,\"v\":1757337600123}", payload);
    }

    [Fact]
    public void BuildSettingsPayload_NullFloor_WritesNull()
    {
        var evt = new DeviceSettingsEvent
        {
            SensorId = 7,
            DeviceName = DeviceName,
            SleepSeconds = 3600,
            SecurityEnabled = false,
            FloorMillivolts = null,
            Version = 1757337600456,
        };

        var payload = MqttService.BuildSettingsPayload(evt);

        Assert.Equal("{\"sleep_s\":3600,\"security\":false,\"floor_mv\":null,\"v\":1757337600456}", payload);
    }

    [Fact]
    public async Task HandleDeviceSettingsEventAsync_ValidDevice_PublishesRetainedAtLeastOnce()
    {
        MqttApplicationMessage? published = null;
        MockClient.Setup(c => c.EnqueueAsync(It.IsAny<MqttApplicationMessage>()))
            .Callback<MqttApplicationMessage>(m => published = m)
            .Returns(Task.CompletedTask);
        var service = CreateService();

        await service.HandleDeviceSettingsEventAsync(MakeEvent());

        Assert.NotNull(published);
        Assert.Equal("garge/devices/garge_0a1b2c3d4e5f/settings", published.Topic);
        Assert.True(published.Retain);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, published.QualityOfServiceLevel);
        Assert.Equal(
            "{\"sleep_s\":600,\"security\":true,\"floor_mv\":12550,\"v\":1757337600123}",
            Encoding.UTF8.GetString(published.PayloadSegment));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garge/+/#")]
    [InlineData("garge_0a1b 2c3d4e5f")]
    public async Task HandleDeviceSettingsEventAsync_InvalidDevice_DoesNotPublish(string deviceName)
    {
        var service = CreateService();

        await service.HandleDeviceSettingsEventAsync(MakeEvent(deviceName));

        MockClient.Verify(c => c.EnqueueAsync(It.IsAny<MqttApplicationMessage>()), Times.Never);
    }

    [Fact]
    public async Task HandleDeviceSettingsEventAsync_OlderVersion_IsSkipped()
    {
        var payloads = CapturePublishedPayloads();
        var service = CreateService();

        await service.HandleDeviceSettingsEventAsync(MakeEvent(version: 1757337600200));
        await service.HandleDeviceSettingsEventAsync(MakeEvent(version: 1757337600100));

        Assert.Equal(
            new[] { "{\"sleep_s\":600,\"security\":true,\"floor_mv\":12550,\"v\":1757337600200}" },
            payloads);
    }

    [Fact]
    public async Task HandleDeviceSettingsEventAsync_SameVersion_PublishesAgain()
    {
        var payloads = CapturePublishedPayloads();
        var service = CreateService();

        await service.HandleDeviceSettingsEventAsync(MakeEvent(version: 1757337600200));
        await service.HandleDeviceSettingsEventAsync(MakeEvent(version: 1757337600200));

        Assert.Equal(2, payloads.Count);
    }

    [Fact]
    public async Task HandleDeviceSettingsEventAsync_NewerVersion_Publishes()
    {
        var payloads = CapturePublishedPayloads();
        var service = CreateService();

        await service.HandleDeviceSettingsEventAsync(MakeEvent(version: 1757337600100));
        await service.HandleDeviceSettingsEventAsync(MakeEvent(version: 1757337600200));

        Assert.Equal(2, payloads.Count);
        Assert.EndsWith("\"v\":1757337600200}", payloads[1]);
    }

    [Fact]
    public async Task HandleDeviceSettingsEventAsync_VersionsTrackedPerDevice()
    {
        var payloads = CapturePublishedPayloads();
        var service = CreateService();

        await service.HandleDeviceSettingsEventAsync(MakeEvent(DeviceName, version: 1757337600200));
        await service.HandleDeviceSettingsEventAsync(MakeEvent("garge_6a7b8c9d0e1f", version: 1757337600100));

        Assert.Equal(2, payloads.Count);
    }

    [Fact]
    public async Task HandleDeviceSettingsEventAsync_PublishThrows_DoesNotThrow()
    {
        MockClient.Setup(c => c.EnqueueAsync(It.IsAny<MqttApplicationMessage>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var service = CreateService();

        var ex = await Record.ExceptionAsync(() => service.HandleDeviceSettingsEventAsync(MakeEvent()));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ReceivedSettingsTopic_IsIgnored()
    {
        var service = CreateService();

        await service.HandleReceivedMessage(Received(
            "garge/devices/garge_0a1b2c3d4e5f/settings",
            "{\"sleep_s\":600,\"security\":true,\"floor_mv\":12550,\"v\":1757337600123}"));

        MockHttpClientFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
        MockClient.Verify(c => c.EnqueueAsync(It.IsAny<MqttApplicationMessage>()), Times.Never);
    }
}
