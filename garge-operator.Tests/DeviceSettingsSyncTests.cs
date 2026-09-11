using System.Net;
using garge_operator.Models;
using garge_operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace garge_operator.Tests;

public class DeviceSettingsSyncTests
{
    private const string ApiBase = "http://test-api";
    private const string DeviceSettingsUrl = $"{ApiBase}/api/sensors/device-settings";

    private const string DeviceSettingsJson =
        """[{"sensorId":7,"deviceName":"garge_0a1b2c3d4e5f","sleepSeconds":600,"securityEnabled":true,"floorMillivolts":12550,"version":1757337600123},{"sensorId":9,"deviceName":"garge_6a7b8c9d0e1f","sleepSeconds":3600,"securityEnabled":false,"floorMillivolts":null,"version":1757337600456}]""";

    private readonly Mock<IMqttService> _mockMqtt = new();
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly FakeHttpMessageHandler _httpHandler = new();

    private DeviceSettingsSync CreateSync()
    {
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_httpHandler));
        return CreateSyncWithoutHttpClient();
    }

    private DeviceSettingsSync CreateSyncWithoutHttpClient()
        => new(
            _mockHttpClientFactory.Object,
            _mockMqtt.Object,
            Options.Create(new ApiOptions { BaseUrl = ApiBase }),
            NullLogger<DeviceSettingsSync>.Instance);

    [Fact]
    public async Task RepublishAsync_HandsEveryEntryToMqttService()
    {
        _httpHandler.OnGet(DeviceSettingsUrl, DeviceSettingsJson);
        var republished = new List<DeviceSettingsEvent>();
        _mockMqtt.Setup(m => m.HandleDeviceSettingsEventAsync(It.IsAny<DeviceSettingsEvent>()))
            .Callback<DeviceSettingsEvent>(republished.Add)
            .Returns(Task.CompletedTask);
        var sync = CreateSync();

        await sync.RepublishAsync(CancellationToken.None);

        Assert.Collection(republished,
            e =>
            {
                Assert.Equal(7, e.SensorId);
                Assert.Equal("garge_0a1b2c3d4e5f", e.DeviceName);
                Assert.Equal(600, e.SleepSeconds);
                Assert.True(e.SecurityEnabled);
                Assert.Equal(12550, e.FloorMillivolts);
                Assert.Equal(1757337600123, e.Version);
            },
            e =>
            {
                Assert.Equal(9, e.SensorId);
                Assert.Equal("garge_6a7b8c9d0e1f", e.DeviceName);
                Assert.Equal(3600, e.SleepSeconds);
                Assert.False(e.SecurityEnabled);
                Assert.Null(e.FloorMillivolts);
                Assert.Equal(1757337600456, e.Version);
            });
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "boom")]
    [InlineData(HttpStatusCode.OK, "{ this is not valid json")]
    public async Task RepublishAsync_BadResponse_DoesNotThrow_DoesNotPublish(HttpStatusCode status, string content)
    {
        _httpHandler.OnGet(DeviceSettingsUrl, content, status);
        var sync = CreateSync();

        var ex = await Record.ExceptionAsync(() => sync.RepublishAsync(CancellationToken.None));

        Assert.Null(ex);
        _mockMqtt.Verify(m => m.HandleDeviceSettingsEventAsync(It.IsAny<DeviceSettingsEvent>()), Times.Never);
    }

    [Fact]
    public async Task RepublishAsync_HttpThrows_DoesNotThrow()
    {
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Throws(new HttpRequestException("boom"));
        var sync = CreateSyncWithoutHttpClient();

        var ex = await Record.ExceptionAsync(() => sync.RepublishAsync(CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RepublishAsync_PublishThrows_DoesNotThrow()
    {
        _httpHandler.OnGet(DeviceSettingsUrl, DeviceSettingsJson);
        _mockMqtt.Setup(m => m.HandleDeviceSettingsEventAsync(It.IsAny<DeviceSettingsEvent>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var sync = CreateSync();

        var ex = await Record.ExceptionAsync(() => sync.RepublishAsync(CancellationToken.None));

        Assert.Null(ex);
    }
}
