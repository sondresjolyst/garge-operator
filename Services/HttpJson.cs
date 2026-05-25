using System.Text;
using System.Text.Json;

namespace garge_operator.Services
{
    /// <summary>
    /// Centralizes the repeated "serialize body to JSON → POST as application/json" pattern.
    /// Callers retain control over success/error branching and logging so existing behavior is
    /// preserved; this helper only owns the serialization + StringContent + PostAsync steps and
    /// reuses a single shared <see cref="JsonSerializerOptions"/> instance.
    /// </summary>
    public static class HttpJson
    {
        // Reused across all serialization calls to avoid per-call allocation. Matches the
        // previous default JsonSerializer.Serialize behavior (no custom naming policy).
        internal static readonly JsonSerializerOptions JsonOptions = new();

        public static Task<HttpResponseMessage> PostJsonAsync<T>(
            HttpClient client, string url, T body, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            return client.PostAsync(url, content, cancellationToken);
        }
    }
}
