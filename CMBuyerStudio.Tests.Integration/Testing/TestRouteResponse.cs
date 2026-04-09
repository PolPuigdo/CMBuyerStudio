namespace CMBuyerStudio.Tests.Integration.Testing;

public sealed record TestRouteResponse(string Body, string ContentType = "text/html", int StatusCode = 200)
{
    public static TestRouteResponse Html(string body) => new(body, "text/html", 200);
}
