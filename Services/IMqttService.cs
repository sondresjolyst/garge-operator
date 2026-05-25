namespace garge_operator.Services;

public interface IMqttService
{
    IReadOnlyDictionary<string, string> LastPublishedSwitchStates { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Switch? GetSwitch(int targetId);
    Task<string> GetJwtTokenAsync();
    Task HandleSwitchEventAsync(SwitchEvent evt);
    Task PublishSwitchDataAsync(string topic, string payload);
}
