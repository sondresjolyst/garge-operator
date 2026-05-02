using System.Text.Json;
using garge_operator.Dtos.Automation;
using garge_operator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace garge_operator.Tests;

public abstract class WorkerTestBase
{
    protected const string ApiBase = "http://test-api";

    protected Mock<IMqttService> MockMqtt { get; } = new();
    protected FakeHttpMessageHandler HttpHandler { get; } = new();

    protected Worker CreateWorker()
    {
        var client = new HttpClient(HttpHandler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:BaseUrl"] = ApiBase })
            .Build();
        return new Worker(NullLogger<Worker>.Instance, MockMqtt.Object, factory.Object, config);
    }

    protected void SetupRules(params AutomationRuleDto[] rules)
    {
        MockMqtt.Setup(m => m.GetJwtTokenAsync()).ReturnsAsync("token");
        HttpHandler.OnGet($"{ApiBase}/api/automation", JsonSerializer.Serialize(rules));
    }

    protected void SetupSensor(int sensorId, double value)
        => HttpHandler.OnGet($"{ApiBase}/api/sensors/{sensorId}/latest-data",
            JsonSerializer.Serialize(new { value = value.ToString("G") }));

    protected void SetupPrice(string area, double price, DateTime? validUntil = null)
    {
        var end = (validUntil ?? DateTime.UtcNow.AddHours(1)).ToString("O");
        HttpHandler.OnGet($"{ApiBase}/api/electricity/current-price?area={Uri.EscapeDataString(area)}",
            JsonSerializer.Serialize(new { value = price, deliveryEnd = end }));
    }

    protected static AutomationRuleDto MakeRule(
        int id = 1,
        int targetId = 10,
        int sensorId = 5,
        string condition = ">",
        double threshold = 20,
        string action = "on",
        bool isEnabled = true,
        double? timerDurationHours = null,
        DateTime? timerActivatedAt = null,
        string? priceCondition = null,
        double? priceThreshold = null,
        string? priceArea = null,
        string? priceOperator = null) => new AutomationRuleDto
        {
            Id = id,
            TargetId = targetId,
            SensorId = sensorId,
            Condition = condition,
            Threshold = threshold,
            Action = action,
            IsEnabled = isEnabled,
            TargetType = "switch",
            SensorType = "sensor",
            TimerDurationHours = timerDurationHours,
            TimerActivatedAt = timerActivatedAt,
            ElectricityPriceCondition = priceCondition,
            ElectricityPriceThreshold = priceThreshold,
            ElectricityPriceArea = priceArea,
            ElectricityPriceOperator = priceOperator,
        };

    protected static Switch MakeSocket(int id = 10, string name = "test-socket") =>
        new Switch { Id = id, Name = name, Type = "socket" };
}
