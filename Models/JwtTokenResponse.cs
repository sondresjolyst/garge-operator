using System.Text.Json.Serialization;

namespace garge_operator.Models
{
    public class JwtTokenResponse
    {
        [JsonPropertyName("token")]
        public required string Token { get; set; }
    }
}
