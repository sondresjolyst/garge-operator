using garge_operator.Services;
using Xunit;

namespace garge_operator.Tests;

/// <summary>
/// Tests the pairing payload parsing in <see cref="MqttService.TryParsePairingToken"/>: the
/// firmware normally publishes JSON of the form {"token":"ABC123"}, but a plain-string payload
/// is tolerated by treating the trimmed payload itself as the token.
/// </summary>
public class MqttServicePairingTokenTests
{
    [Fact]
    public void JsonPayload_ReturnsToken()
    {
        var result = MqttService.TryParsePairingToken("{\"token\":\"ABC123\"}", out var token);

        Assert.True(result);
        Assert.Equal("ABC123", token);
    }

    [Fact]
    public void JsonPayload_TokenIsTrimmed()
    {
        var result = MqttService.TryParsePairingToken("{\"token\":\" ABC123 \"}", out var token);

        Assert.True(result);
        Assert.Equal("ABC123", token);
    }

    [Fact]
    public void JsonPayload_MissingToken_ReturnsFalse()
    {
        var result = MqttService.TryParsePairingToken("{}", out _);

        Assert.False(result);
    }

    [Fact]
    public void JsonPayload_NullToken_ReturnsFalse()
    {
        var result = MqttService.TryParsePairingToken("{\"token\":null}", out _);

        Assert.False(result);
    }

    [Fact]
    public void JsonPayload_EmptyToken_ReturnsFalse()
    {
        var result = MqttService.TryParsePairingToken("{\"token\":\"\"}", out _);

        Assert.False(result);
    }

    [Fact]
    public void PlainStringPayload_ReturnsTrimmedPayloadAsToken()
    {
        var result = MqttService.TryParsePairingToken(" ABC123\n", out var token);

        Assert.True(result);
        Assert.Equal("ABC123", token);
    }

    [Fact]
    public void EmptyPayload_ReturnsFalse()
    {
        var result = MqttService.TryParsePairingToken(string.Empty, out _);

        Assert.False(result);
    }

    [Fact]
    public void WhitespacePayload_ReturnsFalse()
    {
        var result = MqttService.TryParsePairingToken("   ", out _);

        Assert.False(result);
    }

    [Fact]
    public void RetryConstantsMatchSpec()
    {
        Assert.Equal(5, MqttService.PairingClaimMaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(3), MqttService.PairingClaimRetryDelay);
    }
}
