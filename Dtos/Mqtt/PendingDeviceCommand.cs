namespace garge_operator.Dtos.Mqtt
{
    /// <summary>
    /// A command the API is still waiting to see carried out: the state the device should be in,
    /// the state it was last seen in, and the gateway allowed to act on it. A null controller
    /// means no gateway currently holds the device's lease, so publishing would reach no
    /// subscriber and the command has to wait.
    /// </summary>
    public class PendingDeviceCommand
    {
        public string Target { get; set; } = string.Empty;
        public string DesiredState { get; set; } = string.Empty;
        public string? ObservedState { get; set; }
        public string? ControllerDeviceName { get; set; }
        public int Attempts { get; set; }
        public DateTime DesiredStateAt { get; set; }
    }
}
