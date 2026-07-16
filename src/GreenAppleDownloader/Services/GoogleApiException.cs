using System.Net;

namespace GreenAppleDownloader.Services;

public sealed class GoogleApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
