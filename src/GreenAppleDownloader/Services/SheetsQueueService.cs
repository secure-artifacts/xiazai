using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GreenAppleDownloader.Models;

namespace GreenAppleDownloader.Services;

public sealed class SheetsQueueService(GoogleApiClient api)
{
    public const string QueueSheetName = "_青苹果下载队列";
    private static readonly string[] Headers =
    [
        "task_id", "batch_id", "created_at", "source_sheet", "source_cell", "display_text", "drive_url",
        "file_id", "status", "progress", "local_path", "error", "requested_by", "app_instance"
    ];

    public async Task EnsureQueueSheetAsync(string spreadsheetId, CancellationToken cancellationToken = default)
    {
        ValidateSpreadsheetId(spreadsheetId);
        var metadataUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}?fields=sheets(properties(sheetId,title,hidden))";
        using var metadataResponse = await api.SendAsync(new HttpRequestMessage(HttpMethod.Get, metadataUrl), cancellationToken: cancellationToken);
        await GoogleApiClient.EnsureSuccessAsync(metadataResponse, cancellationToken);
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(cancellationToken));
        var exists = metadata.RootElement.GetProperty("sheets").EnumerateArray().Any(sheet =>
            string.Equals(sheet.GetProperty("properties").GetProperty("title").GetString(), QueueSheetName, StringComparison.Ordinal));

        if (!exists)
        {
            var createUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}:batchUpdate";
            var createBody = new
            {
                requests = new[]
                {
                    new { addSheet = new { properties = new { title = QueueSheetName, hidden = true } } }
                }
            };
            using var createRequest = new HttpRequestMessage(HttpMethod.Post, createUrl)
            {
                Content = JsonContent.Create(createBody)
            };
            using var createResponse = await api.SendAsync(createRequest, cancellationToken: cancellationToken);
            await GoogleApiClient.EnsureSuccessAsync(createResponse, cancellationToken);
        }

        var range = EncodeRange($"'{QueueSheetName}'!A1:N1");
        var valuesUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}/values/{range}?valueInputOption=RAW";
        using var headerRequest = new HttpRequestMessage(HttpMethod.Put, valuesUrl)
        {
            Content = JsonContent.Create(new { range = $"'{QueueSheetName}'!A1:N1", majorDimension = "ROWS", values = new[] { Headers } })
        };
        using var headerResponse = await api.SendAsync(headerRequest, cancellationToken: cancellationToken);
        await GoogleApiClient.EnsureSuccessAsync(headerResponse, cancellationToken);
    }

    public async Task<IReadOnlyList<DownloadTaskItem>> GetPendingAsync(string spreadsheetId, CancellationToken cancellationToken = default)
    {
        ValidateSpreadsheetId(spreadsheetId);
        var range = EncodeRange($"'{QueueSheetName}'!A2:N");
        var url = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}/values/{range}?majorDimension=ROWS&valueRenderOption=UNFORMATTED_VALUE";
        using var response = await api.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken: cancellationToken);
        await GoogleApiClient.EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("values", out var values))
        {
            return [];
        }

        var pending = new List<DownloadTaskItem>();
        var rowNumber = 2;
        foreach (var row in values.EnumerateArray())
        {
            var fields = row.EnumerateArray().Select(ValueText).ToList();
            while (fields.Count < Headers.Length)
            {
                fields.Add(string.Empty);
            }

            var status = fields[8].Trim();
            if ((string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "RETRY", StringComparison.OrdinalIgnoreCase)) &&
                DriveLinkParser.TryExtractFileId(fields[7].Length > 0 ? fields[7] : fields[6], out var fileId))
            {
                pending.Add(new DownloadTaskItem
                {
                    QueueRow = rowNumber,
                    TaskId = fields[0],
                    BatchId = fields[1],
                    CreatedAt = fields[2],
                    SourceSheet = fields[3],
                    SourceCell = fields[4],
                    DisplayText = fields[5],
                    DriveUrl = fields[6],
                    FileId = fileId,
                    RequestedBy = fields[12],
                    Status = "等待下载"
                });
            }

            rowNumber++;
        }

        if (pending.Count == 0)
        {
            return pending;
        }

        var firstBatch = pending[0].BatchId;
        return pending.Where(item => string.Equals(item.BatchId, firstBatch, StringComparison.Ordinal)).ToList();
    }

    public async Task UpdateTaskAsync(string spreadsheetId, DownloadTaskItem task, string appInstance, CancellationToken cancellationToken = default)
    {
        if (task.QueueRow < 2)
        {
            return;
        }

        var url = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}/values:batchUpdate";
        var status = task.Status switch
        {
            "已完成" => "DONE",
            "下载失败" => "FAILED",
            "正在下载" => "DOWNLOADING",
            _ => "PENDING"
        };
        var body = new
        {
            valueInputOption = "RAW",
            data = new object[]
            {
                new
                {
                    range = $"'{QueueSheetName}'!I{task.QueueRow}:L{task.QueueRow}",
                    majorDimension = "ROWS",
                    values = new object[][] { [status, Math.Round(task.Progress, 1), task.LocalPath, task.Error] }
                },
                new
                {
                    range = $"'{QueueSheetName}'!N{task.QueueRow}",
                    majorDimension = "ROWS",
                    values = new object[][] { [appInstance] }
                }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        using var response = await api.SendAsync(request, cancellationToken: cancellationToken);
        await GoogleApiClient.EnsureSuccessAsync(response, cancellationToken);
    }

    private static string EncodeRange(string range) => Uri.EscapeDataString(range);

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        _ => string.Empty
    };

    private static void ValidateSpreadsheetId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("请先在设置页填写 Google 表格链接或 ID。");
        }
    }
}
