using System.Net;

namespace garge_operator.Tests;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, HttpStatusCode Status, string Content)> _rules = new();

    /// <summary>
    /// Ordered log of every request the handler matched, in "{METHOD} {url}" form. Used by tests
    /// that assert relative ordering of API calls against MQTT publishes.
    /// </summary>
    public List<string> MatchedRequests { get; } = new();

    /// <summary>
    /// Body of every matched request that carried content, keyed by the same "{METHOD} {url}" form
    /// as <see cref="MatchedRequests"/>.
    /// </summary>
    public List<(string Request, string Body)> RequestBodies { get; } = new();

    /// <summary>
    /// Optional sink invoked with each matched request URL. Tests can point this at the same list a
    /// mock publish callback writes to, producing a single ordered log that interleaves API calls
    /// with MQTT publishes so their relative order can be asserted.
    /// </summary>
    public Action<string>? OnMatched { get; set; }

    public void OnGet(string url, string content, HttpStatusCode status = HttpStatusCode.OK)
        => _rules.Add((r => r.Method == HttpMethod.Get && r.RequestUri!.ToString() == url, status, content));

    public void OnPatch(string url, HttpStatusCode status = HttpStatusCode.OK)
        => _rules.Add((r => r.Method == HttpMethod.Patch && r.RequestUri!.ToString() == url, status, "ok"));

    public void OnPost(string url, string content = "ok", HttpStatusCode status = HttpStatusCode.OK)
        => _rules.Add((r => r.Method == HttpMethod.Post && r.RequestUri!.ToString() == url, status, content));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var (match, status, content) in _rules)
        {
            if (match(request))
            {
                var key = $"{request.Method} {request.RequestUri}";
                MatchedRequests.Add(key);
                if (request.Content != null)
                    RequestBodies.Add((key, await request.Content.ReadAsStringAsync(cancellationToken)));
                OnMatched?.Invoke(request.RequestUri!.ToString());
                return new HttpResponseMessage(status) { Content = new StringContent(content) };
            }
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No handler: {request.Method} {request.RequestUri}")
        };
    }
}
