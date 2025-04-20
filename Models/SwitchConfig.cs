using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace garge_operator.Models
{
    public class SwitchConfig
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("command_topic")]
        public required string CommandTopic { get; set; }

        [JsonPropertyName("state_topic")]
        public required string StateTopic { get; set; }

        [JsonPropertyName("payload_on")]
        public required string PayloadOn { get; set; }

        [JsonPropertyName("payload_off")]
        public required string PayloadOff { get; set; }

        [JsonPropertyName("optimistic")]
        public bool Optimistic { get; set; }

        [JsonPropertyName("qos")]
        public int Qos { get; set; }

        [JsonPropertyName("retain")]
        public bool Retain { get; set; }

        [JsonPropertyName("uniq_id")]
        public required string UniqId { get; set; }

        [JsonPropertyName("device")]
        public required Device Device { get; set; }
    }

    public class Device
    {
        [JsonPropertyName("identifiers")]
        public required string Identifiers { get; set; }

        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("model")]
        public required string Model { get; set; }

        [JsonPropertyName("manufacturer")]
        public required string Manufacturer { get; set; }
    }
}
