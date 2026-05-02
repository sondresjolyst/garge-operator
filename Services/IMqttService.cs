namespace garge_operator.Services;

public interface IMqttService
{
    IReadOnlyDictionary<string, string> LastPublishedSwitchStates { get; }
    Task ConnectAsync();
    Switch? GetSwitch(int targetId);
    Task<string> GetJwtTokenAsync();
    Task HandleWebhookDataAsync(WebhookPayload payload);
    Task PublishSwitchDataAsync(string topic, string payload);
}
