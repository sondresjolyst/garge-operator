namespace garge_operator.Constants
{
    public static class SensorTypes
    {
        public const string Voltage = "voltage";
        public const string Temperature = "temperature";
        public const string Humidity = "humidity";

        public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Voltage,
            Temperature,
            Humidity,
        };

        public static bool IsAllowed(string? type) =>
            !string.IsNullOrWhiteSpace(type) && Allowed.Contains(type);
    }
}
