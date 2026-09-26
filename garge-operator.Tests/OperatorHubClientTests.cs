using System.Net;
using garge_operator.Models;
using garge_operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace garge_operator.Tests;

public class OperatorHubClientTests
{
    private const string ApiBase = "http://test-api";
    private const string DeviceSettingsUrl = $"{ApiBase}/api/sensors/device-settings";

    private const string DeviceSettingsJson =
        """[{"sensorId":7,"deviceName":"garge_0a1b2c3d4e5f","sleepSeconds":600,"securityEnabled":true,"floorMillivolts":12550,"version":1757337600123}]""";

    private readonly Mock<IMqttService> _mockMqtt = new();
    private readonly FakeHttpMessageHandler _httpHandler = new();

    private OperatorHubClient CreateClient()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_httpHandler));
        var apiOptions = Options.Create(new ApiOptions { BaseUrl = ApiBase });
        var deviceSettingsSync = new DeviceSettingsSync(factory.Object, _mockMqtt.Object, apiOptions, NullLogger<DeviceSettingsSync>.Instance);
        return new OperatorHubClient(apiOptions, _mockMqtt.Object, deviceSettingsSync, NullLogger<OperatorHubClient>.Instance);
    }

    [Fact]
    public async Task OnReconnected_RepublishesDeviceSettings()
    {
        _httpHandler.OnGet(DeviceSettingsUrl, DeviceSettingsJson);
        var client = CreateClient();

        await client.OnReconnectedAsync("connection-1");

        _mockMqtt.Verify(m => m.HandleDeviceSettingsEventAsync(It.Is<DeviceSettingsEvent>(e => e.DeviceName == "garge_0a1b2c3d4e5f")), Times.Once);
    }

    [Fact]
    public async Task OnConnected_RepublishesDeviceSettings()
    {
        _httpHandler.OnGet(DeviceSettingsUrl, DeviceSettingsJson);
        var client = CreateClient();

        await client.OnConnectedAsync($"{ApiBase}/hubs/devices");

        _mockMqtt.Verify(m => m.HandleDeviceSettingsEventAsync(It.Is<DeviceSettingsEvent>(e => e.DeviceName == "garge_0a1b2c3d4e5f")), Times.Once);
    }

    [Fact]
    public async Task OnReconnected_RepublishFails_DoesNotThrow()
    {
        _httpHandler.OnGet(DeviceSettingsUrl, "boom", HttpStatusCode.InternalServerError);
        var client = CreateClient();

        var ex = await Record.ExceptionAsync(() => client.OnReconnectedAsync("connection-1"));

        Assert.Null(ex);
        _mockMqtt.Verify(m => m.HandleDeviceSettingsEventAsync(It.IsAny<DeviceSettingsEvent>()), Times.Never);
    }
}
