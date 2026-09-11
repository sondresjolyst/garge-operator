namespace garge_operator.Services;

public interface IMqttService
{
    IReadOnlyDictionary<string, string> LastPublishedSwitchStates { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Switch? GetSwitch(int targetId);
    Task<string> GetJwtTokenAsync();
    Task HandleSwitchEventAsync(SwitchEvent evt);
    Task HandleDeviceSettingsEventAsync(DeviceSettingsEvent evt);
    Task PublishSwitchDataAsync(string topic, string payload);
}
