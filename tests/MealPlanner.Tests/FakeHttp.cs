using System.Net;
using System.Text;

namespace MealPlanner.Tests;

/// <summary>
/// Answers every request with one canned response and remembers what was sent,
/// so an API client can be run end to end with no network. The suite never
/// reaches a real provider, for the same reason it never spawns the CLI.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public FakeHttp(string body, HttpStatusCode status = HttpStatusCode.OK)
        : this((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }))
    {
    }

    /// <param name="respond">
    /// Handed the request's own token. A handler that waits on anything else
    /// never sees the client's timeout fire, and a timeout test hangs the run
    /// rather than failing it.
    /// </param>
    public FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public HttpRequestMessage? Request { get; private set; }

    /// <summary>The body of the last request, read before the client disposed it.</summary>
    public string? RequestBody { get; private set; }

    public string? ClientName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        ClientName = name;
        return new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Request = request;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await _respond(request, cancellationToken);
    }
}
