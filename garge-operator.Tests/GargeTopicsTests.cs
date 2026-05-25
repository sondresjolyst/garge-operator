using garge_operator.Constants;

namespace garge_operator.Tests;

public class GargeTopicsTests
{
    [Fact]
    public void SetTopic_BuildsExpectedString()
    {
        Assert.Equal("garge/devices/my-device/set", GargeTopics.SetTopic("my-device"));
    }

    [Fact]
    public void DeviceWildcard_BuildsExpectedString()
    {
        Assert.Equal("garge/devices/my-device/#", GargeTopics.DeviceWildcard("my-device"));
    }

    [Fact]
    public void TryParseDeviceTopic_FourSegments_EntityFallsBackToDeviceId()
    {
        var ok = GargeTopics.TryParseDeviceTopic("garge/devices/dev1/config", out var deviceId, out var type, out var entity);

        Assert.True(ok);
        Assert.Equal("dev1", deviceId);
        Assert.Equal("config", type);
        Assert.Equal("dev1", entity); // No explicit entity segment: falls back to the device id.
    }

    [Fact]
    public void TryParseDeviceTopic_FiveSegments_ExtractsEntity()
    {
        var ok = GargeTopics.TryParseDeviceTopic("garge/devices/dev1/sensorA/state", out var deviceId, out var type, out var entity);

        Assert.True(ok);
        Assert.Equal("dev1", deviceId);
        Assert.Equal("state", type);
        Assert.Equal("sensorA", entity);
    }

    [Fact]
    public void TryParseDeviceTopic_SetTopic_RoundTrips()
    {
        var topic = GargeTopics.SetTopic("socket-7");

        var ok = GargeTopics.TryParseDeviceTopic(topic, out var deviceId, out var type, out var entity);

        Assert.True(ok);
        Assert.Equal("socket-7", deviceId);
        Assert.Equal("set", type);
        Assert.Equal("socket-7", entity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garge/devices/dev1")]            // Too few segments.
    [InlineData("garge/sensors/dev1/state")]      // Wrong second segment.
    [InlineData("foo/devices/dev1/state")]        // Wrong root segment.
    public void TryParseDeviceTopic_InvalidTopics_ReturnFalse(string topic)
    {
        var ok = GargeTopics.TryParseDeviceTopic(topic, out var deviceId, out var type, out var entity);

        Assert.False(ok);
        Assert.Equal(string.Empty, deviceId);
        Assert.Equal(string.Empty, type);
        Assert.Equal(string.Empty, entity);
    }

    [Fact]
    public void TryParseDiscoveryTopic_ValidTopic_ExtractsSegments()
    {
        var ok = GargeTopics.TryParseDiscoveryTopic(
            "garge/devices/gateway1/discovered_devices/target2/discovered",
            out var discoveredBy,
            out var target);

        Assert.True(ok);
        Assert.Equal("gateway1", discoveredBy);
        Assert.Equal("target2", target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garge/devices/gateway1/discovered_devices/target2")] // Too few segments.
    [InlineData("foo/devices/gateway1/discovered_devices/target2/discovered")] // Wrong root.
    public void TryParseDiscoveryTopic_InvalidTopics_ReturnFalse(string topic)
    {
        var ok = GargeTopics.TryParseDiscoveryTopic(topic, out var discoveredBy, out var target);

        Assert.False(ok);
        Assert.Equal(string.Empty, discoveredBy);
        Assert.Equal(string.Empty, target);
    }
}
