using System.Text.Json.Serialization;

namespace garge_operator.Models
{
    public class JwtTokenResponse
    {
        [JsonPropertyName("$id")]
        public required string Id { get; set; }
        [JsonPropertyName("token")]
        public required string Token { get; set; }
    }
}
