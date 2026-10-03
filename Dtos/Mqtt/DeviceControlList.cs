namespace garge_operator.Dtos.Mqtt
{
    /// <summary>
    /// The targets one gateway currently holds the lease for. A gateway that discovered a device
    /// but does not hold its lease is absent from its own list, which is how it knows to report
    /// the device without acting on it.
    /// </summary>
    public class DeviceControlList
    {
        public string GatewayDeviceName { get; set; } = string.Empty;
        public List<string> Targets { get; set; } = [];
    }
}
