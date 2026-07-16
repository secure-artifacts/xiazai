using System.Text.Json.Serialization;

namespace GreenAppleDownloader.Models;

public sealed class GoogleClientSecretsDocument
{
    [JsonPropertyName("installed")]
    public GoogleClientSecrets? Installed { get; set; }

    [JsonPropertyName("web")]
    public GoogleClientSecrets? Web { get; set; }
}

public sealed class GoogleClientSecrets
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("auth_uri")]
    public string AuthUri { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    [JsonPropertyName("token_uri")]
    public string TokenUri { get; set; } = "https://oauth2.googleapis.com/token";
}

public sealed class GoogleToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; } = 3600;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsExpiring => DateTimeOffset.UtcNow >= ReceivedAtUtc.AddSeconds(ExpiresIn - 120);
}

public sealed class DriveFileMetadata
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public string? SizeText { get; set; }

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public DriveCapabilities? Capabilities { get; set; }

    [JsonIgnore]
    public long? Size => long.TryParse(SizeText, out var value) ? value : null;
}

public sealed class DriveCapabilities
{
    [JsonPropertyName("canDownload")]
    public bool CanDownload { get; set; } = true;
}

public sealed record DownloadProgress(
    DownloadTaskItem Task,
    string Status,
    double Percent,
    string? FileName = null,
    string? LocalPath = null,
    string? Error = null);

