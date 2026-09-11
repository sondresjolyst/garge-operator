using garge_operator.Models;
using garge_operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Packets;

namespace garge_operator.Tests;

public abstract class MqttServiceTestBase
{
    protected const string ApiBase = "http://test-api";

    protected Mock<IManagedMqttClient> MockClient { get; } = new();
    protected Mock<IHttpClientFactory> MockHttpClientFactory { get; } = new();
    protected FakeHttpMessageHandler HttpHandler { get; } = new();

    protected MqttService CreateService()
    {
        var client = new HttpClient(HttpHandler);
        MockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        var mqttOptions = Options.Create(new MqttOptions
        {
            Broker = "broker.example.com",
            Port = 8883,
            Username = "user",
            Password = "pass",
        });
        var apiOptions = Options.Create(new ApiOptions { BaseUrl = ApiBase });

        return new MqttService(
            MockClient.Object,
            MockHttpClientFactory.Object,
            Mock.Of<ITokenProvider>(),
            mqttOptions,
            apiOptions,
            NullLogger<MqttService>.Instance);
    }

    protected static MqttApplicationMessageReceivedEventArgs Received(string topic, string payload, bool retain = false)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();
        return new MqttApplicationMessageReceivedEventArgs("test-client", message, new MqttPublishPacket(), (_, _) => Task.CompletedTask);
    }
}
