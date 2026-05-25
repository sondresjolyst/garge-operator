namespace garge_operator.Services
{
    /// <summary>
    /// Shared identifiers for the named HttpClients registered in <c>Program.cs</c>.
    /// </summary>
    public static class GargeApiClient
    {
        /// <summary>
        /// The authenticated garge-api client. Requests on this client carry the JWT bearer
        /// automatically via <see cref="BearerTokenHandler"/>.
        /// </summary>
        public const string Authorized = "GargeApi";
    }
}
