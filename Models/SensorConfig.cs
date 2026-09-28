using System.Text.Json;
using System.Text.Json.Serialization;

namespace garge_operator.Models
{
    public class SensorConfig
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }
        [JsonPropertyName("stat_cla")]
        public required string StatCla { get; set; }
        [JsonPropertyName("stat_t")]
        public required string StatT { get; set; }
        [JsonPropertyName("unit_of_meas")]
        public required string UnitOfMeas { get; set; }
        [JsonPropertyName("dev_cla")]
        public required string DevCla { get; set; }
        [JsonPropertyName("frc_upd")]
        public bool FrcUpd { get; set; }
        [JsonPropertyName("uniq_id")]
        public required string UniqId { get; set; }
        [JsonPropertyName("val_tpl")]
        public required string ValTpl { get; set; }
        [JsonPropertyName("parent_name")]
        public required string ParentName { get; set; }
        [JsonPropertyName("sleep_s")]
        public int? SleepS { get; set; }
        [JsonPropertyName("security")]
        public bool? Security { get; set; }
        // A JsonElement so the key's absence is distinguishable from a null value, and
        // so a value of any shape parses: a malformed floor must not take the whole
        // config down with it and leave the sensor unregistered.
        [JsonPropertyName("floor_mv")]
        public JsonElement FloorMv { get; set; }

        /// <summary>True when the device sent a floor_mv key at all, null included.
        /// Firmware that predates floor reporting omits it, which leaves the element
        /// Undefined and makes the API skip the floor check.</summary>
        [JsonIgnore]
        public bool FloorReported => FloorMv.ValueKind != JsonValueKind.Undefined;

        /// <summary>The reported floor, or null for an explicit null and for a value that
        /// is not an int32.</summary>
        [JsonIgnore]
        public int? FloorMillivolts =>
            FloorMv.ValueKind == JsonValueKind.Number && FloorMv.TryGetInt32(out var mv)
                ? mv
                : null;
        /// <summary>False when the device's EEPROM cannot hold the settings record, so it
        /// drops every settings message it is sent. Absent on firmware that predates the
        /// field.</summary>
        [JsonPropertyName("settings_store")]
        public bool? SettingsStore { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
