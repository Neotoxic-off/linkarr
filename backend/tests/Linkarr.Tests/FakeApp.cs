using System.Net;
using System.Text;
using Linkarr.Api.Apps;

namespace Linkarr.Tests;

// A fake app server: records every request (method, path and query, body) and answers from a route function.
// A null answer means the app is down (the request throws like a refused connection).
public sealed class FakeApp(Func<string, string, string, (HttpStatusCode Status, string Body)?> route) : HttpMessageHandler
{
    public List<(string Method, string Path, string Body, string? ApiKey)> Requests { get; } = [];

    public static FakeApp Down() => new((_, _, _) => null);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        string path = request.RequestUri!.PathAndQuery;
        string? key = request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? keys) ? keys.First()
            : request.Headers.TryGetValues("X-Emby-Token", out IEnumerable<string>? tokens) ? tokens.First() : null;
        Requests.Add((request.Method.Method, path, body, key));
        (HttpStatusCode status, string answer) = route(request.Method.Method, path, body)
            ?? throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
        return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
    }

    public ModularrClient Modularr() => new(Client(), new Uri("http://modularr:8080/"), "modularr-key");

    public VisularrClient Visularr() => new(Client(), new Uri("http://visularr:8096/"), "visularr-key");

    private HttpClient Client()
    {
        HttpClient http = new(this, disposeHandler: false);
        AppHttp.Configure(http);
        return http;
    }
}
