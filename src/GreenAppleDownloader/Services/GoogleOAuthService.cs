using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreenAppleDownloader.Models;

namespace GreenAppleDownloader.Services;

public sealed class GoogleOAuthService
{
    private const string Scope = "https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/spreadsheets";
    private readonly HttpClient _httpClient;
    private readonly SecureTokenStore _tokenStore;
    private GoogleToken? _token;
    private GoogleClientSecrets? _secrets;

    public GoogleOAuthService(HttpClient httpClient, SecureTokenStore tokenStore)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _token = tokenStore.Load();
    }

    public bool HasStoredToken => _token is not null;

    public async Task ConnectAsync(string secretsPath, CancellationToken cancellationToken = default)
    {
        _secrets = LoadSecrets(secretsPath);
        var listener = new HttpListener();
        var port = GetFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var authUrl = BuildAuthorizationUrl(_secrets, redirectUri, challenge, state);

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        try
        {
            using var registration = cancellationToken.Register(listener.Stop);
            var context = await listener.GetContextAsync();
            var query = context.Request.QueryString;
            var responseText = query["error"] is { Length: > 0 } error
                ? $"授权没有完成：{WebUtility.HtmlEncode(error)}。您可以关闭此页面。"
                : "授权成功！现在可以关闭此页面，返回青苹果下载器。";

            var responseBytes = Encoding.UTF8.GetBytes($"<html><head><meta charset=\"utf-8\"><title>青苹果下载器</title></head><body style=\"font-family:Segoe UI,Microsoft YaHei;padding:48px;background:#f0faf3;color:#173d29\"><h2>🍏 {responseText}</h2></body></html>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, cancellationToken);
            context.Response.Close();

            if (!string.Equals(query["state"], state, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(query["code"]))
            {
                throw new InvalidOperationException("Google 授权未完成或安全校验失败。");
            }

            var form = new Dictionary<string, string>
            {
                ["code"] = query["code"]!,
                ["client_id"] = _secrets.ClientId,
                ["client_secret"] = _secrets.ClientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier
            };

            using var response = await _httpClient.PostAsync(_secrets.TokenUri, new FormUrlEncodedContent(form), cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new GoogleApiException(response.StatusCode, $"Google 授权失败：{json}");
            }

            _token = JsonSerializer.Deserialize<GoogleToken>(json) ?? throw new InvalidOperationException("Google 没有返回有效令牌。");
            _token.ReceivedAtUtc = DateTimeOffset.UtcNow;
            _tokenStore.Save(_token);
        }
        finally
        {
            listener.Close();
        }
    }

    public async Task<string> GetAccessTokenAsync(string secretsPath, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        _secrets ??= LoadSecrets(secretsPath);
        _token ??= _tokenStore.Load();
        if (_token is null)
        {
            throw new InvalidOperationException("请先在设置页连接 Google 账号。");
        }

        if (!forceRefresh && !_token.IsExpiring)
        {
            return _token.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(_token.RefreshToken))
        {
            throw new InvalidOperationException("Google 登录已过期，请重新连接账号。");
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _secrets.ClientId,
            ["client_secret"] = _secrets.ClientSecret,
            ["refresh_token"] = _token.RefreshToken,
            ["grant_type"] = "refresh_token"
        };
        using var response = await _httpClient.PostAsync(_secrets.TokenUri, new FormUrlEncodedContent(form), cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new GoogleApiException(response.StatusCode, $"刷新 Google 登录失败：{json}");
        }

        var refreshed = JsonSerializer.Deserialize<GoogleToken>(json) ?? throw new InvalidOperationException("Google 没有返回有效令牌。");
        refreshed.RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken) ? _token.RefreshToken : refreshed.RefreshToken;
        refreshed.ReceivedAtUtc = DateTimeOffset.UtcNow;
        _token = refreshed;
        _tokenStore.Save(_token);
        return _token.AccessToken;
    }

    public void Disconnect()
    {
        _token = null;
        _tokenStore.Clear();
    }

    private static GoogleClientSecrets LoadSecrets(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("请选择从 Google Cloud 下载的 OAuth 桌面客户端 JSON 文件。", path);
        }

        var document = JsonSerializer.Deserialize<GoogleClientSecretsDocument>(File.ReadAllText(path));
        var secrets = document?.Installed ?? document?.Web;
        if (secrets is null || string.IsNullOrWhiteSpace(secrets.ClientId))
        {
            throw new InvalidDataException("OAuth JSON 文件格式不正确，请使用“桌面应用”类型的客户端凭据。");
        }

        return secrets;
    }

    private static string BuildAuthorizationUrl(GoogleClientSecrets secrets, string redirectUri, string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = secrets.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        };
        return secrets.AuthUri + "?" + string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
