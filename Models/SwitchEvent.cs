// Wire-format DTO for switch state events received from garge-api's
// SignalR DeviceHub. Matches SwitchEventDto on the API side.
public class SwitchEvent
{
    public int Id { get; set; }
    public int SwitchId { get; set; }
    public Switch Switch { get; set; } = null!;
    public string Value { get; set; } = null!;
    public DateTime Timestamp { get; set; }
}
