using System.Net.Http.Headers;

namespace garge_operator.Services
{
    /// <summary>
    /// Attaches the JWT bearer token to every outgoing request on the authenticated garge-api
    /// HttpClient. Replaces the per-call-site "fetch token + set Authorization header" boilerplate
    /// with the idiomatic <see cref="DelegatingHandler"/> pattern. The token is sourced from
    /// <see cref="ITokenProvider"/>, which caches it and fetches over an unauthenticated client,
    /// so attaching a token here never recurses back through this handler.
    /// </summary>
    public class BearerTokenHandler : DelegatingHandler
    {
        private readonly ITokenProvider _tokenProvider;

        public BearerTokenHandler(ITokenProvider tokenProvider)
        {
            _tokenProvider = tokenProvider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _tokenProvider.GetJwtTokenAsync(cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
