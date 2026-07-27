using System.Text.Json.Serialization;

namespace garge_operator.Dtos.Mqtt
{
    /// <summary>
    /// One-shot pairing message published by a device during setup.
    /// Expected format: { "token": "ABC123" }
    /// </summary>
    public class PairingPayload
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }
}
