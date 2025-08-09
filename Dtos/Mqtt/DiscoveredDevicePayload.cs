namespace garge_operator.Dtos.Mqtt
{
    public class DiscoveredDevicePayload
    {
        public required string DiscoveredBy { get; set; }
        public required string Target { get; set; }
        public required string Type { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
