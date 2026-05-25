using System.Net;
using System.Text.Json;
using garge_operator.Services;

namespace garge_operator.Tests;

/// <summary>
/// Tests for the shared HttpJson.PostJsonAsync helper (refactor #3), which centralizes the
/// repeated "serialize → StringContent(application/json) → PostAsync" pattern.
/// </summary>
public class HttpJsonTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public HttpStatusCode StatusToReturn { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Observe the token so a cancelled token surfaces, proving it was threaded through.
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(StatusToReturn) { Content = new StringContent("ok") };
        }
    }

    [Fact]
    public async Task PostJsonAsync_SerializesBody_SetsJsonContentType_UsesPost()
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var body = new { value = "42", nested = new { area = "NO1" } };

        var response = await HttpJson.PostJsonAsync(client, "http://api/sensors/data", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://api/sensors/data", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);

        // The serialized body must match plain JsonSerializer output for the same value.
        Assert.Equal(JsonSerializer.Serialize(body), handler.LastBody);
    }

    [Fact]
    public async Task PostJsonAsync_ReturnsResponse_OnNonSuccessStatus_WithoutThrowing()
    {
        var handler = new CapturingHandler { StatusToReturn = HttpStatusCode.Conflict };
        using var client = new HttpClient(handler);

        var response = await HttpJson.PostJsonAsync(client, "http://api/switches", new { name = "s1" });

        // The helper only performs the request; success/error branching stays with the caller.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PostJsonAsync_PropagatesCancellationToken()
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // An already-cancelled token must surface as a cancellation, proving the token is threaded
        // through to the underlying request rather than ignored.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpJson.PostJsonAsync(client, "http://api/x", new { a = 1 }, cts.Token));
    }
}
