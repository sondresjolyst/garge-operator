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
    }
}
