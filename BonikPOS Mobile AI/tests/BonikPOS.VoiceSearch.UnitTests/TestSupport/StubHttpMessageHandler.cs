using System.Net;
using System.Text;

namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>Answers HTTP requests from a queue of scripted responders and records every request.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responders = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Used when the queue is empty.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Fallback { get; set; }

    public StubHttpMessageHandler Respond(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public StubHttpMessageHandler RespondJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond((_, _) => Task.FromResult(Json(json, status)));

    public StubHttpMessageHandler RespondStatus(HttpStatusCode status, Action<HttpResponseMessage>? configure = null) =>
        Respond((_, _) =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent("error", Encoding.UTF8, "text/plain") };
            configure?.Invoke(r);
            return Task.FromResult(r);
        });

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_responders.TryDequeue(out var responder))
        {
            return responder(request, cancellationToken);
        }

        return Fallback is not null
            ? Fallback(request, cancellationToken)
            : throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
    }
}
