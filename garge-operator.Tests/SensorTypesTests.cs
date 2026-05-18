using garge_operator.Constants;
using Xunit;

namespace garge_operator.Tests;

public class SensorTypesTests
{
    [Theory]
    [InlineData("voltage", true)]
    [InlineData("temperature", true)]
    [InlineData("humidity", true)]
    [InlineData("Voltage", true)]
    [InlineData("VOLTAGE", true)]
    [InlineData("battery", false)]
    [InlineData("battery_health", false)]
    [InlineData("pressure", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsAllowed_MatchesAllowlistCaseInsensitive(string? type, bool expected)
    {
        Assert.Equal(expected, SensorTypes.IsAllowed(type));
    }

    [Fact]
    public void Allowed_ContainsExactlyTheThreeSupportedTypes()
    {
        Assert.Equal(3, SensorTypes.Allowed.Count);
        Assert.Contains("voltage", SensorTypes.Allowed);
        Assert.Contains("temperature", SensorTypes.Allowed);
        Assert.Contains("humidity", SensorTypes.Allowed);
    }
}
