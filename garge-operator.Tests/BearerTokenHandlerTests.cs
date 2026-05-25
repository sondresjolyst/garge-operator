using System.Net;
using garge_operator.Services;
using Moq;

namespace garge_operator.Tests;

/// <summary>
/// Unit tests for <see cref="BearerTokenHandler"/>, the DelegatingHandler that attaches the JWT
/// bearer to every outgoing request on the authenticated garge-api client. The handler is wired
/// with a fake <see cref="ITokenProvider"/> and a capturing inner handler, then driven through an
/// <see cref="HttpClient"/> so the Authorization header on the actual outgoing request can be
/// asserted — including that a refreshed token replaces the previous one on a later send.
/// </summary>
public class BearerTokenHandlerTests
{
    // Records the Authorization header seen on each request reaching the inner handler.
    private sealed class CapturingInnerHandler : HttpMessageHandler
    {
        public List<string?> SeenAuthorizationHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenAuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }
    }

    private static HttpClient BuildClient(ITokenProvider provider, out CapturingInnerHandler inner)
    {
        inner = new CapturingInnerHandler();
        var handler = new BearerTokenHandler(provider) { InnerHandler = inner };
        return new HttpClient(handler);
    }

    [Fact]
    public async Task SendAsync_AttachesBearerTokenFromProvider()
    {
        var tokenProvider = new Mock<ITokenProvider>();
        tokenProvider.Setup(p => p.GetJwtTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token-123");
        using var client = BuildClient(tokenProvider.Object, out var inner);

        var response = await client.GetAsync("http://api/anything");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "Bearer test-token-123" }, inner.SeenAuthorizationHeaders);
    }

    [Fact]
    public async Task SendAsync_RequestsTokenOncePerSend()
    {
        var tokenProvider = new Mock<ITokenProvider>();
        tokenProvider.Setup(p => p.GetJwtTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token-123");
        using var client = BuildClient(tokenProvider.Object, out _);

        await client.GetAsync("http://api/anything");

        // The header is set exactly once per request: the provider is consulted a single time and
        // there is no double-attach.
        tokenProvider.Verify(p => p.GetJwtTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_SecondSend_UsesRefreshedToken()
    {
        // The provider returns a refreshed token on the second call (mirrors cache expiry). Each
        // send must carry the token current at that moment — the header is overwritten, not appended.
        var tokenProvider = new Mock<ITokenProvider>();
        tokenProvider.SetupSequence(p => p.GetJwtTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("token-old")
            .ReturnsAsync("token-new");
        using var client = BuildClient(tokenProvider.Object, out var inner);

        await client.GetAsync("http://api/first");
        await client.GetAsync("http://api/second");

        Assert.Equal(
            new[] { "Bearer token-old", "Bearer token-new" },
            inner.SeenAuthorizationHeaders);
    }

    [Fact]
    public async Task SendAsync_ThreadsCancellationTokenToProvider()
    {
        // The handler must thread the (linked) request cancellation token through to the token
        // provider. HttpClient links the caller's token with its own, so identity cannot be
        // asserted; instead a provider that observes cancellation proves the token reached it: an
        // already-cancelled request surfaces as a cancellation rather than being ignored.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tokenProvider = new Mock<ITokenProvider>();
        tokenProvider.Setup(p => p.GetJwtTokenAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult("scoped-token");
            });
        using var client = BuildClient(tokenProvider.Object, out var inner);

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://api/scoped");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendAsync(request, cts.Token));
        // The request never reached the inner handler because the token fetch was cancelled first.
        Assert.Empty(inner.SeenAuthorizationHeaders);
    }
}
