// Wire-format DTO for device settings events received from garge-api's
// SignalR DeviceHub. Matches DeviceSettingsEventDto on the API side.
public class DeviceSettingsEvent
{
    public int SensorId { get; set; }
    public string DeviceName { get; set; } = null!;
    public int SleepSeconds { get; set; }
    public bool SecurityEnabled { get; set; }
    public int? FloorMillivolts { get; set; }
    public long Version { get; set; }
}
