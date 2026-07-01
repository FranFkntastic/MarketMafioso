using System.Net;

namespace MarketMafioso.Tests.MarketAcquisition;

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode statusCode;
    private readonly string responseBody;

    public RecordingHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
    {
        this.statusCode = statusCode;
        this.responseBody = responseBody;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public Uri? RequestUri => LastRequest?.RequestUri;
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content == null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody),
        };
    }
}
