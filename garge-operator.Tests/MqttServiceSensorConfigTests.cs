using System.Net;
using System.Text.Json;
using garge_operator.Models;
using garge_operator.Services;

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

    private const string AckConfigWithFloor =
        """{"name":"Garge 0a1b2c3d4e5f voltage","stat_cla":"measurement","stat_t":"garge/devices/garge_0a1b2c3d4e5f/garge_0a1b2c3d4e5f_voltage/state","unit_of_meas":"V","dev_cla":"voltage","frc_upd":true,"uniq_id":"garge_0a1b2c3d4e5f_voltage","val_tpl":"{{ value_json.value }}","parent_name":"garge_0a1b2c3d4e5f","version":"v1.15.0","sleep_s":600,"security":true,"floor_mv":12550}""";

    private const string AckConfigWithNullFloor =
        """{"name":"Garge 0a1b2c3d4e5f voltage","stat_cla":"measurement","stat_t":"garge/devices/garge_0a1b2c3d4e5f/garge_0a1b2c3d4e5f_voltage/state","unit_of_meas":"V","dev_cla":"voltage","frc_upd":true,"uniq_id":"garge_0a1b2c3d4e5f_voltage","val_tpl":"{{ value_json.value }}","parent_name":"garge_0a1b2c3d4e5f","version":"v1.15.0","sleep_s":3600,"security":false,"floor_mv":null}""";

    private const string AckConfigWithAStringFloor =
        """{"name":"Garge 0a1b2c3d4e5f voltage","stat_cla":"measurement","stat_t":"garge/devices/garge_0a1b2c3d4e5f/garge_0a1b2c3d4e5f_voltage/state","unit_of_meas":"V","dev_cla":"voltage","frc_upd":true,"uniq_id":"garge_0a1b2c3d4e5f_voltage","val_tpl":"{{ value_json.value }}","parent_name":"garge_0a1b2c3d4e5f","version":"v1.15.0","sleep_s":600,"security":true,"floor_mv":"12550"}""";

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
        Assert.Equal("{\"sleepSeconds\":600,\"securityEnabled\":true,\"version\":\"v1.15.0\",\"floorReported\":false,\"floorMillivolts\":null}", body);
    }

    // The server checks the reported floor against the one it asked for, so it has to
    // arrive. Firmware without the field omits it, which forwards as floorReported
    // false and leaves the server's older behaviour intact.
    [Fact]
    public async Task ConfigWithFloor_ForwardsTheFloorToTheApi()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, status: HttpStatusCode.NoContent);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, AckConfigWithFloor));

        var (_, body) = Assert.Single(HttpHandler.RequestBodies, r => r.Request == $"POST {ReportedSettingsUrl}");
        Assert.Equal("{\"sleepSeconds\":600,\"securityEnabled\":true,\"version\":\"v1.15.0\",\"floorReported\":true,\"floorMillivolts\":12550}", body);
    }

    // A device that has no floor sends the key with a null value. That is not the same
    // as firmware that never sends it: the server must see it and refuse to arm.
    [Fact]
    public async Task ConfigWithANullFloor_ReportsTheFloorAsSent()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, status: HttpStatusCode.NoContent);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, AckConfigWithNullFloor));

        var (_, body) = Assert.Single(HttpHandler.RequestBodies, r => r.Request == $"POST {ReportedSettingsUrl}");
        Assert.Equal("{\"sleepSeconds\":3600,\"securityEnabled\":false,\"version\":\"v1.15.0\",\"floorReported\":true,\"floorMillivolts\":null}", body);
    }

    // A floor of the wrong shape must not take the whole config down with it: the
    // sensor still has to register, and the ack still has to reach the server.
    [Fact]
    public async Task ConfigWithAFloorThatIsNotANumber_StillRegistersAndReports()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, status: HttpStatusCode.NoContent);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, AckConfigWithAStringFloor));

        Assert.Contains($"POST {ApiBase}/api/sensors", HttpHandler.MatchedRequests);
        var (_, body) = Assert.Single(HttpHandler.RequestBodies, r => r.Request == $"POST {ReportedSettingsUrl}");
        Assert.Equal("{\"sleepSeconds\":600,\"securityEnabled\":true,\"version\":\"v1.15.0\",\"floorReported\":true,\"floorMillivolts\":null}", body);
    }

    // The MQTT handler awaits the report, so the attempt sequence has to be bounded:
    // three of the HTTP client's 100 s default timeouts would stop every other message
    // for minutes.
    [Fact]
    public async Task ConfigWithSettingsFields_SlowApi_GivesUpAtTheTimeout()
    {
        var original = MqttService.ReportedSettingsTotalTimeout;
        MqttService.ReportedSettingsTotalTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            HttpHandler.OnPost($"{ApiBase}/api/sensors");
            HttpHandler.OnPost(ReportedSettingsUrl, status: HttpStatusCode.NoContent,
                delay: TimeSpan.FromSeconds(30));
            var service = CreateService();
            var started = DateTime.UtcNow;

            await service.HandleReceivedMessage(Received(ConfigTopic, AckConfig));

            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10),
                "reporting settings ran past the timeout");
        }
        finally
        {
            MqttService.ReportedSettingsTotalTimeout = original;
        }
    }

    [Fact]
    public async Task ConfigWithSettingsFields_ReportsOnce()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, status: HttpStatusCode.NoContent);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, AckConfig));

        Assert.Single(HttpHandler.MatchedRequests, r => r == $"POST {ReportedSettingsUrl}");
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

    // A dropped ack leaves the API without ArmedAt until the device publishes its
    // config again, a wake cycle away, so a server-side fault is worth retrying.
    [Fact]
    public async Task ConfigWithSettingsFields_ReportedSettingsServerError_IsRetried()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, "boom", HttpStatusCode.InternalServerError);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, AckConfig));

        Assert.Equal(
            MqttService.ReportedSettingsMaxRetries + 1,
            HttpHandler.MatchedRequests.Count(r => r == $"POST {ReportedSettingsUrl}"));
    }

    // A refusal is settled: the sensor is unknown, so asking again cannot help.
    [Fact]
    public async Task ConfigWithSettingsFields_ReportedSettingsRefused_IsNotRetried()
    {
        HttpHandler.OnPost($"{ApiBase}/api/sensors");
        HttpHandler.OnPost(ReportedSettingsUrl, "not found", HttpStatusCode.NotFound);
        var service = CreateService();

        await service.HandleReceivedMessage(Received(ConfigTopic, AckConfig));

        Assert.Single(HttpHandler.MatchedRequests, r => r == $"POST {ReportedSettingsUrl}");
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
