using System.Text;
using System.Text.Json;
using garge_operator.Models;
using Microsoft.Extensions.Options;

namespace garge_operator.Services
{
    /// <summary>
    /// Fetches and caches the JWT bearer token from garge-api's login endpoint. The login call
    /// uses a plain, unauthenticated <see cref="HttpClient"/> created from the default factory
    /// pipeline so it never routes through <see cref="BearerTokenHandler"/> (which would deadlock
    /// trying to attach a token while fetching one).
    /// </summary>
    public class JwtTokenProvider : ITokenProvider
    {
        // Named client without the bearer handler, used solely for the unauthenticated login call.
        public const string LoginClientName = "GargeApiLogin";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ApiOptions _apiOptions;
        private readonly ILogger<JwtTokenProvider> _logger;

        private string? _cachedToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _tokenCacheLock = new(1, 1);

        public JwtTokenProvider(
            IHttpClientFactory httpClientFactory,
            IOptions<ApiOptions> apiOptions,
            ILogger<JwtTokenProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _apiOptions = apiOptions.Value;
            _logger = logger;
        }

        public async Task<string> GetJwtTokenAsync(CancellationToken cancellationToken = default)
        {
            await _tokenCacheLock.WaitAsync(cancellationToken);
            try
            {
                if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
                    return _cachedToken;

                var client = _httpClientFactory.CreateClient(LoginClientName);
                var loginData = new { Email = _apiOptions.Email, Password = _apiOptions.Password };
                var content = new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{_apiOptions.BaseUrl}/api/auth/login", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to retrieve JWT token. Status code: {StatusCode}, Reason: {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                    throw new InvalidOperationException("Failed to retrieve JWT token.");
                }

                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Successfully retrieved JWT token.");

                var tokenResponse = JsonSerializer.Deserialize<JwtTokenResponse>(responseContent);
                if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.Token))
                {
                    _logger.LogError("JWT token is null or empty.");
                    throw new InvalidOperationException("Failed to retrieve JWT token.");
                }

                _cachedToken = tokenResponse.Token;
                _tokenExpiry = ParseJwtExpiry(_cachedToken).AddSeconds(-60);
                return _cachedToken;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving JWT token.");
                throw;
            }
            finally
            {
                _tokenCacheLock.Release();
            }
        }

        private DateTime ParseJwtExpiry(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return DateTime.UtcNow.AddMinutes(14);
                var padding = (4 - parts[1].Length % 4) % 4;
                var base64 = parts[1].PadRight(parts[1].Length + padding, '=').Replace('-', '+').Replace('_', '/');
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var exp))
                    return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
            }
            catch (Exception ex)
            {
                // Falling back to a conservative default expiry; log so the failure isn't silent.
                _logger.LogDebug(ex, "Could not parse JWT expiry; using default refresh window.");
            }
            return DateTime.UtcNow.AddMinutes(14);
        }
    }
}
