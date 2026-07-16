using System.Net;
using System.Text.Json;
using GreenAppleDownloader.Models;

namespace GreenAppleDownloader.Services;

public sealed class DriveDownloadService(GoogleApiClient api)
{
    public async Task<DriveFileMetadata> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var fields = Uri.EscapeDataString("id,name,size,mimeType,capabilities(canDownload)");
        var url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?fields={fields}&supportsAllDrives=true";
        using var response = await api.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken: cancellationToken);
        await GoogleApiClient.EnsureSuccessAsync(response, cancellationToken);
        var metadata = JsonSerializer.Deserialize<DriveFileMetadata>(await response.Content.ReadAsStringAsync(cancellationToken));
        return metadata ?? throw new InvalidDataException("无法读取 Google Drive 文件信息。");
    }

    public async Task<string> DownloadAsync(
        DownloadTaskItem task,
        string targetDirectory,
        int maxRetries,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(task.FileId, cancellationToken);
        if (metadata.Capabilities?.CanDownload == false)
        {
            throw new InvalidOperationException("此文件的拥有者禁止下载。");
        }

        if (metadata.MimeType.StartsWith("application/vnd.google-apps", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("该链接不是可直接下载的视频文件。");
        }

        Directory.CreateDirectory(targetDirectory);
        var safeName = FileNameHelper.Sanitize(metadata.Name, $"视频_{task.FileId}");
        var finalPath = UniqueFilePath(targetDirectory, safeName);
        var partPath = finalPath + ".part";
        task.FileName = safeName;
        progress?.Report(new DownloadProgress(task, "正在下载", 0, safeName));

        Exception? lastError = null;
        for (var attempt = 0; attempt <= Math.Max(0, maxRetries); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
                if (metadata.Size is { } total && existing == total)
                {
                    File.Move(partPath, finalPath, overwrite: true);
                    return finalPath;
                }

                var url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(task.FileId)}?alt=media&supportsAllDrives=true";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                }

                using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && metadata.Size == existing)
                {
                    File.Move(partPath, finalPath, overwrite: true);
                    return finalPath;
                }

                await GoogleApiClient.EnsureSuccessAsync(response, cancellationToken);
                var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
                if (!append)
                {
                    existing = 0;
                }

                var totalLength = metadata.Size ?? (response.Content.Headers.ContentLength is { } contentLength ? existing + contentLength : null);
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, true);
                var buffer = new byte[1024 * 1024];
                long received = existing;
                var lastReport = DateTime.UtcNow.AddSeconds(-1);
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 200)
                    {
                        var percent = totalLength is > 0 ? received * 100d / totalLength.Value : 0;
                        progress?.Report(new DownloadProgress(task, "正在下载", percent, safeName));
                        lastReport = DateTime.UtcNow;
                    }
                }

                await output.FlushAsync(cancellationToken);
                if (metadata.Size is { } expectedSize && new FileInfo(partPath).Length != expectedSize)
                {
                    throw new IOException($"文件传输提前结束，已收到 {new FileInfo(partPath).Length} 字节，应为 {expectedSize} 字节。");
                }
                File.Move(partPath, finalPath, overwrite: true);
                progress?.Report(new DownloadProgress(task, "已完成", 100, safeName, finalPath));
                return finalPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < Math.Max(0, maxRetries) && IsRetryable(ex))
            {
                lastError = ex;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1)) + Random.Shared.NextDouble());
                progress?.Report(new DownloadProgress(task, $"网络波动，{delay.TotalSeconds:0} 秒后重试", task.Progress, task.FileName));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastError ?? new IOException("下载失败。");
    }

    private static bool IsRetryable(Exception error) => error switch
    {
        HttpRequestException => true,
        IOException => true,
        GoogleApiException apiError => apiError.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout,
        _ => false
    };

    private static string UniqueFilePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 2;
        // An existing .part belongs to this exact filename and is deliberately
        // reused so interrupted downloads can continue after the app restarts.
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{stem}_{counter++}{extension}");
        }

        return path;
    }
}
