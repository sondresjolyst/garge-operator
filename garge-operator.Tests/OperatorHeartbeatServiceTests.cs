using System.Net;
using garge_operator.Models;
using garge_operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace garge_operator.Tests;

public class OperatorHeartbeatServiceTests
{
    private const string ApiBase = "http://test-api";
    private const string HeartbeatUrl = $"{ApiBase}/api/operator/heartbeat";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly Mock<IMqttService> _mockMqtt = new();
    private readonly FakeHttpMessageHandler _httpHandler = new();

    private OperatorHeartbeatService CreateService()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_httpHandler));
        return new OperatorHeartbeatService(
            _mockMqtt.Object,
            factory.Object,
            Options.Create(new ApiOptions { BaseUrl = ApiBase }),
            NullLogger<OperatorHeartbeatService>.Instance,
            interval: TimeSpan.FromMilliseconds(20),
            connectPollInterval: TimeSpan.FromMilliseconds(5));
    }

    private List<string> HeartbeatBodies()
        => _httpHandler.RequestBodies.Where(r => r.Request == $"POST {HeartbeatUrl}").Select(r => r.Body).ToList();

    [Theory]
    [InlineData(true, "{\"mqttConnected\":true}")]
    [InlineData(false, "{\"mqttConnected\":false}")]
    public async Task SendHeartbeat_PostsMqttConnectionState(bool connected, string expectedBody)
    {
        _mockMqtt.Setup(m => m.IsConnected).Returns(connected);
        _httpHandler.OnPost(HeartbeatUrl, status: HttpStatusCode.NoContent);
        var service = CreateService();

        await service.SendHeartbeatAsync(CancellationToken.None);

        Assert.Equal(new[] { expectedBody }, HeartbeatBodies());
    }

    [Fact]
    public async Task SendHeartbeat_Rejected_DoesNotThrow()
    {
        _mockMqtt.Setup(m => m.IsConnected).Returns(true);
        _httpHandler.OnPost(HeartbeatUrl, "boom", HttpStatusCode.InternalServerError);
        var service = CreateService();

        var ex = await Record.ExceptionAsync(() => service.SendHeartbeatAsync(CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SendHeartbeat_Throws_DoesNotThrow()
    {
        _mockMqtt.Setup(m => m.IsConnected).Throws(new InvalidOperationException("boom"));
        var service = CreateService();

        var ex = await Record.ExceptionAsync(() => service.SendHeartbeatAsync(CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Run_WaitsForMqttConnection_BeforeFirstBeat()
    {
        using var connected = new ManualResetEventSlim(false);
        var disconnectedPolls = 0;
        var polledWhileDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockMqtt.Setup(m => m.IsConnected).Returns(() =>
        {
            if (!connected.IsSet && Interlocked.Increment(ref disconnectedPolls) >= 3)
                polledWhileDisconnected.TrySetResult();
            return connected.IsSet;
        });
        _httpHandler.OnPost(HeartbeatUrl, status: HttpStatusCode.NoContent);
        var beats = 0;
        var firstBeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.OnMatched = url =>
        {
            if (url != HeartbeatUrl) return;
            Interlocked.Increment(ref beats);
            firstBeat.TrySetResult();
        };
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await polledWhileDisconnected.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        var beatsBeforeConnect = Volatile.Read(ref beats);
        connected.Set();
        await firstBeat.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, beatsBeforeConnect);
        Assert.Equal("{\"mqttConnected\":true}", HeartbeatBodies()[0]);
    }

    [Fact]
    public async Task Run_AfterFirstConnect_KeepsBeatingThroughDisconnect()
    {
        using var connected = new ManualResetEventSlim(true);
        _mockMqtt.Setup(m => m.IsConnected).Returns(() => connected.IsSet);
        _httpHandler.OnPost(HeartbeatUrl, status: HttpStatusCode.NoContent);
        var beats = 0;
        var secondBeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.OnMatched = url =>
        {
            if (url != HeartbeatUrl) return;
            if (Interlocked.Increment(ref beats) == 1)
                connected.Reset();
            else
                secondBeat.TrySetResult();
        };
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await secondBeat.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        var bodies = HeartbeatBodies();
        Assert.Equal("{\"mqttConnected\":true}", bodies[0]);
        Assert.Equal("{\"mqttConnected\":false}", bodies[1]);
    }

    [Fact]
    public async Task Run_FailedBeat_KeepsBeating()
    {
        _mockMqtt.Setup(m => m.IsConnected).Returns(true);
        _httpHandler.OnPost(HeartbeatUrl, "boom", HttpStatusCode.InternalServerError);
        var beats = 0;
        var secondBeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.OnMatched = url =>
        {
            if (url == HeartbeatUrl && Interlocked.Increment(ref beats) >= 2)
                secondBeat.TrySetResult();
        };
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await secondBeat.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.True(Volatile.Read(ref beats) >= 2);
    }
}
