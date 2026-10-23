using System.Net;
using System.Text.Json;

namespace Linkarr.Api.Apps;

// The one outbound HTTP setup for both apps: 10 s per call, no redirects (a key or a password never follows one elsewhere),
// and a response size cap enforced by the buffered read.
public static class AppHttp
{
    public const string ClientName = "apps";
    public const long MaxResponseBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // modularr answers camelCase; Jellyfin reads and writes PascalCase. Both are read case-insensitively.
    public static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions PascalCase = new() { PropertyNameCaseInsensitive = true };

    public static IServiceCollection AddAppHttp(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName, Configure).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        return services;
    }

    public static void Configure(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Timeout = Timeout;
        http.MaxResponseContentBufferSize = MaxResponseBytes;
    }

    // Whether an exception is an app being unreachable or answering badly, as opposed to a bug or a shutdown.
    public static bool IsAppFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or JsonException or InvalidOperationException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    // The message shown to an admin: never the key (it only travels in a header) nor the response body beyond a short excerpt.
    public static string Describe(Exception exception) => exception is OperationCanceledException
        ? $"no answer within {Timeout.TotalSeconds:0} s"
        : exception.Message;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string app, bool notFoundIsOk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || (notFoundIsOk && response.StatusCode == HttpStatusCode.NotFound))
        {
            return response;
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new HttpRequestException($"{app} answered with a redirect (HTTP {(int)response.StatusCode}); check its URL.", null, response.StatusCode);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        string excerpt = body.Length > 200 ? body[..200] + "…" : body;
        throw new HttpRequestException(
            response.StatusCode == HttpStatusCode.Unauthorized ? $"{app} refused the API key (HTTP 401)." : $"{app} answered HTTP {(int)response.StatusCode}: {excerpt}".TrimEnd(' ', ':'),
            null, response.StatusCode);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        using (response)
        {
            return await response.Content.ReadFromJsonAsync<T>(options, cancellationToken) ?? throw new JsonException("Empty answer.");
        }
    }
}
