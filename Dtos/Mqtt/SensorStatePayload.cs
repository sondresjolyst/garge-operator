using System.Text.Json;
using System.Text.Json.Serialization;

namespace garge_operator.Dtos.Mqtt
{
    /// <summary>
    /// JSON payload sent by sensors that report a single numeric reading.
    /// Expected format: { "value": 12.34 }
    /// </summary>
    public class SensorStatePayload
    {
        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }
    }
}
