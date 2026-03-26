using System.Text.Json.Serialization;

namespace garge_operator.Models
{
    public class BatteryHealthPayload
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("baseline")]
        public float Baseline { get; set; }

        [JsonPropertyName("last_charge")]
        public float LastCharge { get; set; }

        [JsonPropertyName("drop_pct")]
        public float DropPct { get; set; }

        [JsonPropertyName("charges_recorded")]
        public int ChargesRecorded { get; set; }
    }
}
