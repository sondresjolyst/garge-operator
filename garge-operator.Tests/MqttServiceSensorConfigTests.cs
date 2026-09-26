using System.Net;
using System.Text.Json;
using garge_operator.Models;

namespace garge_operator.Tests;

public class MqttServiceSensorConfigTests : MqttServiceTestBase
{
    private const string UniqId = "garge_0a1b2c3d4e5f_voltage";
    private const string ConfigTopic = "garge/devices/garge_0a1b2c3d4e5f/garge_0a1b2c3d4e5f_voltage/config";
    private const string ReportedSettingsUrl = $"{ApiBase}/api/sensors/name/{UniqId}/reported-settings";

    private const string LegacyConfig =
        """{"name":"Garge 0a1b2c3d4e5f voltage","stat_cla":"measurement","stat_t":"garge/devices/garge_0a1b2c3d4e5f/garge_0a1b2c3d4e5f_voltage/state","unit_of_meas":"V","dev_cla":"voltage","frc_upd":true,"uniq_id":"garge_0a1b2c3d4e5f_voltage","val_tpl":"{{ value_json.value }}","parent_name":"garge_0a1b2c3d4e5f","version":"v1.14.0"}""";

    private const string UnversionedConfig =
        """{"name":"Garge 0a1b2c3d4e5f voltage","stat_cla":"measurement","stat_t":"garge/devices/garge_0a1b2c3d4e5f/garge_0a1b2c3d4e5f_voltage/state","unit_of_meas":"V","dev_cla":"voltage","frc_upd":true,"uniq_id":"garge_0a1b2c3d4e5f_voltage","val_tpl":"{{ value_json.value }}","parent_name":"garge_0a1b2c3d4e5f"}""";

    private const string AckConfig =
        """{"name":"Garge 0a1b2c3d4e5f voltage","stat_cla":"measurement","stat_t":"garge/devices/garge_0a1b2c3d4e5f/garge_0a1b2c3d4e5f_voltage/state","unit_of_meas":"V","dev_cla":"voltage","frc_upd":true,"uniq_id":"garge_0a1b2c3d4e5f_voltage","val_tpl":"{{ value_json.value }}","parent_name":"garge_0a1b2c3d4e5f","version":"v1.15.0","sleep_s":600,"security":true}""";

    [Theory]
    [InlineData(LegacyConfig, "v1.14.0")]
    [InlineData(UnversionedConfig, null)]
    public void LegacyConfig_Deserializes_WithoutSettingsFields(string payload, string? expectedVersion)
    {
        var config = JsonSerializer.Deserialize<SensorConfig>(payload);

        Assert.NotNull(config);
        Assert.Equal(UniqId, config.UniqId);
        Assert.Null(config.SleepS);
        Assert.Null(config.Security);
        Assert.Equal(expectedVersion, config.Version);
    }

    [Theory]
    [InlineData(LegacyConfig)]
    [InlineData(UnversionedConfig)]
    public async Task LegacyConfig_RegistersSensor_PostsNoReportedSettings(string payload)
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, status: HttpStatusCode.NoContent);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, payload));

        Assert.Contains($"POST {ApiBase}/api/sensors", HttpHandler.MatchedRequests);
        Assert.DoesNotContain($"POST {ReportedSettingsUrl}", HttpHandler.MatchedRequests);
    }

    [Fact]
    public async Task ConfigWithSettingsFields_PostsReportedSettings()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, status: HttpStatusCode.NoContent);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, AckConfig));

        var (_, body) = Assert.Single(HttpHandler.RequestBodies, r => r.Request == $"POST {ReportedSettingsUrl}");
        Assert.Equal("{\"sleepSeconds\":600,\"securityEnabled\":true,\"version\":\"v1.15.0\"}", body);
    }

    [Fact]
    public async Task RetainedConfigWithSettingsFields_RegistersSensor_PostsNoReportedSettings()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, status: HttpStatusCode.NoContent);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, AckConfig, retain: true));

        Assert.Contains($"POST {ApiBase}/api/sensors", HttpHandler.MatchedRequests);
        Assert.DoesNotContain($"POST {ReportedSettingsUrl}", HttpHandler.MatchedRequests);
    }

    [Fact]
    public async Task ConfigWithSettingsFields_ReportedSettingsRejected_StillRegistersSensor()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, "not found", HttpStatusCode.NotFound);
        var service = CreateService();

        var ex = await Record.ExceptionAsync(() => service.HandleReceivedMessage(Received(ConfigTopic, AckConfig)));

        Assert.Null(ex);
        Assert.Contains($"POST {ApiBase}/api/sensors", HttpHandler.MatchedRequests);
        Assert.Contains($"POST {ReportedSettingsUrl}", HttpHandler.MatchedRequests);
    }
}
