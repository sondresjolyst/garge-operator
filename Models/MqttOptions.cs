using System.ComponentModel.DataAnnotations;

namespace garge_operator.Models
{
    /// <summary>
    /// Strongly-typed binding for the <c>Mqtt</c> configuration section. The keys mirror the
    /// historical stringly-typed reads (<c>Mqtt:Broker</c>, <c>Mqtt:Port</c>, etc.) so existing
    /// appsettings and environment variables continue to bind unchanged. Validation that was
    /// previously performed by hand in the MqttService constructor now lives in DataAnnotations
    /// and is enforced at startup via ValidateOnStart.
    /// </summary>
    public class MqttOptions
    {
        public const string SectionName = "Mqtt";

        [Required(AllowEmptyStrings = false, ErrorMessage = "Mqtt:Broker not set in configuration.")]
        public string Broker { get; set; } = string.Empty;

        [Range(1, 65535, ErrorMessage = "Mqtt:Port is invalid or not set in configuration.")]
        public int Port { get; set; }

        [Required(AllowEmptyStrings = false, ErrorMessage = "Mqtt:Username or Mqtt:Password not set in configuration.")]
        public string Username { get; set; } = string.Empty;

        [Required(AllowEmptyStrings = false, ErrorMessage = "Mqtt:Username or Mqtt:Password not set in configuration.")]
        public string Password { get; set; } = string.Empty;
    }
}
