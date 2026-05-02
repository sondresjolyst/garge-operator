using System.Net;

namespace garge_operator.Tests;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, HttpStatusCode Status, string Content)> _rules = new();

    public void OnGet(string url, string content, HttpStatusCode status = HttpStatusCode.OK)
        => _rules.Add((r => r.Method == HttpMethod.Get && r.RequestUri!.ToString() == url, status, content));

    public void OnPatch(string url, HttpStatusCode status = HttpStatusCode.OK)
        => _rules.Add((r => r.Method == HttpMethod.Patch && r.RequestUri!.ToString() == url, status, "ok"));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var (match, status, content) in _rules)
        {
            if (match(request))
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No handler: {request.Method} {request.RequestUri}")
        });
    }
}
