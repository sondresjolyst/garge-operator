using System.Text.Json;
using System.Text.Json.Serialization;

namespace garge_operator.Models
{
    /// <summary>
    /// Success body returned by <c>POST /api/pairing/claim</c>.
    /// </summary>
    public class PairingClaimResponse
    {
        [JsonPropertyName("claimedSensorIds")]
        public List<int>? ClaimedSensorIds { get; set; }

        [JsonPropertyName("claimedSwitchIds")]
        public List<int>? ClaimedSwitchIds { get; set; }

        [JsonPropertyName("skipped")]
        public int? Skipped { get; set; }
    }
}
