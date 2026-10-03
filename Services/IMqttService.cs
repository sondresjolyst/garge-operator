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

    /// <summary>
    /// Publishes a command to a device's set topic, optionally forcing a publish the unchanged
    /// state check would otherwise skip. A redelivery needs that: the earlier publish may have
    /// reached nobody.
    /// </summary>
    Task PublishSwitchDataAsync(string topic, string payload, bool force);

    /// <summary>
    /// Tells one gateway which targets it may act on, retained so it is delivered on subscribe
    /// rather than only when the list next changes.
    /// </summary>
    Task PublishDeviceControlsAsync(string gatewayDeviceName, IReadOnlyList<string> targets);
}
