using System.Net;

namespace CMBuyerStudio.Tests.Integration.Testing;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return await _handler(request, cancellationToken);
    }

    public static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        byte[]? body = null,
        string? contentType = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(body ?? [])
        };

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        return response;
    }
}
