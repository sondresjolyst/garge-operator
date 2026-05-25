namespace garge_operator.Services
{
    /// <summary>
    /// Supplies (and caches) the JWT bearer token used to authenticate against garge-api.
    /// Kept separate from <see cref="IMqttService"/> so the login request itself can use an
    /// unauthenticated HttpClient and the <see cref="BearerTokenHandler"/> can attach tokens
    /// without recursing through the authenticated client.
    /// </summary>
    public interface ITokenProvider
    {
        Task<string> GetJwtTokenAsync(CancellationToken cancellationToken = default);
    }
}
