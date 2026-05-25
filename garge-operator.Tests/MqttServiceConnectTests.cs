using garge_operator.Models;
using garge_operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace garge_operator.Tests;

/// <summary>
/// Tests the fail-fast guard at the top of <see cref="MqttService.ConnectAsync"/>: without a usable
/// JWT the operator must throw rather than continue connecting deaf. Paired with
/// BackgroundServiceExceptionBehavior.StopHost (Program.cs), throwing causes the pod to exit and be
/// rescheduled instead of running forever with a dead MQTT connection. The MQTT client itself is
/// never started in these tests — the token check runs before any broker interaction.
/// </summary>
public class MqttServiceConnectTests
{
    private static MqttService CreateService(ITokenProvider tokenProvider)
    {
        var factory = new Mock<IHttpClientFactory>();
        // Connect should fail at the token check before any HttpClient is needed, so a default
        // (unconfigured) factory is sufficient.
        var mqttOptions = Options.Create(new MqttOptions
        {
            Broker = "broker.example.com",
            Port = 8883,
            Username = "user",
            Password = "pass",
        });
        var apiOptions = Options.Create(new ApiOptions { BaseUrl = "http://test-api" });

        return new MqttService(
            factory.Object,
            tokenProvider,
            mqttOptions,
            apiOptions,
            NullLogger<MqttService>.Instance);
    }

    [Fact]
    public async Task ConnectAsync_EmptyToken_ThrowsInvalidOperationException()
    {
        var tokenProvider = new Mock<ITokenProvider>();
        tokenProvider.Setup(p => p.GetJwtTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        var service = CreateService(tokenProvider.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_NullToken_ThrowsInvalidOperationException()
    {
        var tokenProvider = new Mock<ITokenProvider>();
        tokenProvider.Setup(p => p.GetJwtTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string)null!);
        var service = CreateService(tokenProvider.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConnectAsync(CancellationToken.None));
    }
}
