using System.ComponentModel.DataAnnotations;

namespace garge_operator.Models
{
    public class SensorData
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int SensorId { get; set; }

        [Required]
        public required string Value { get; set; }

        [Required]
        public DateTime Timestamp { get; set; }
    }
}
