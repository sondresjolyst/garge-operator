namespace garge_operator.Constants
{
    /// <summary>
    /// Centralizes construction and parsing of the custom garge MQTT topic scheme.
    /// All topic strings produced here match the historical hand-built strings exactly;
    /// callers should prefer these helpers over positional <c>topic.Split('/')</c> indexing.
    /// </summary>
    public static class GargeTopics
    {
        public const string Root = "garge";
        public const string Devices = "devices";
        private const string Prefix = Root + "/" + Devices + "/";

        /// <summary>The command topic for a device, e.g. <c>garge/devices/{deviceId}/set</c>.</summary>
        public static string SetTopic(string deviceId) => $"{Prefix}{deviceId}/set";

        /// <summary>The wildcard subscription covering all subtopics of a device, e.g. <c>garge/devices/{deviceId}/#</c>.</summary>
        public static string DeviceWildcard(string deviceId) => $"{Prefix}{deviceId}/#";

        /// <summary>
        /// Parses a standard device topic of the form <c>garge/devices/{deviceId}/[entity/]{type}</c>.
        /// On success returns the device id, the leaf type segment (config/state/set), and the entity
        /// segment when present (otherwise the entity falls back to the device id, matching prior behavior).
        /// Returns false for topics that do not match the expected structure.
        /// </summary>
        public static bool TryParseDeviceTopic(
            string topic,
            out string deviceId,
            out string type,
            out string entity)
        {
            deviceId = string.Empty;
            type = string.Empty;
            entity = string.Empty;

            if (string.IsNullOrEmpty(topic)) return false;

            var parts = topic.Split('/');
            if (parts.Length < 4 || parts[0] != Root || parts[1] != Devices)
                return false;

            deviceId = parts[2];
            type = parts[^1];
            // Five-segment topics carry an explicit entity segment; shorter topics use the device id.
            entity = parts.Length == 5 ? parts[3] : deviceId;
            return true;
        }

        /// <summary>
        /// Parses a discovery topic of the form
        /// <c>garge/devices/{discoveredBy}/discovered_devices/{target}/discovered</c>.
        /// Returns false when the topic does not have the expected discovery structure.
        /// </summary>
        public static bool TryParseDiscoveryTopic(
            string topic,
            out string discoveredBy,
            out string target)
        {
            discoveredBy = string.Empty;
            target = string.Empty;

            if (string.IsNullOrEmpty(topic)) return false;

            var parts = topic.Split('/');
            if (parts.Length < 6 || parts[0] != Root || parts[1] != Devices)
                return false;

            discoveredBy = parts[2];
            target = parts[4];
            return true;
        }
    }
}
