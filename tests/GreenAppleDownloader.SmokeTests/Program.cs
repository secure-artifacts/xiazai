using GreenAppleDownloader.Services;

var failures = new List<string>();

ExpectDriveId(
    "https://drive.google.com/file/d/1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV/view",
    "1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV");
ExpectDriveId(
    "https://drive.google.com/open?id=1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV&usp=drive_copy",
    "1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV");
ExpectDriveId(
    "[成品](https://drive.google.com/file/d/1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV/view)",
    "1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV");

var spreadsheet = DriveLinkParser.ExtractSpreadsheetId("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz123456/edit#gid=0");
Check(spreadsheet == "1AbCdEfGhIjKlMnOpQrStUvWxYz123456", "表格 ID 解析失败");
Check(!DriveLinkParser.TryExtractFileId("https://example.com/not-drive", out _), "无效网址不应被识别");

var clipboardLinks = ClipboardLinkExtractor.Extract(
    plainText: "成品图片\nhttps://drive.google.com/open?id=1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
    html: "<table><tr><td><a href=\"https://drive.google.com/file/d/1BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB/view\">成品视频</a></td></tr></table>",
    rtf: "{\\rtf1{\\field{\\*\\fldinst{HYPERLINK \"https://drive.google.com/file/d/1CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC/view\"}}{\\fldrslt 成品}}}");
Check(clipboardLinks.Count == 3, "剪贴板 HTML/RTF/纯文本链接未全部提取");
Check(clipboardLinks.All(link => link.StartsWith("https://drive.google.com/file/d/", StringComparison.Ordinal)), "剪贴板链接没有规范化");

var duplicateLinks = ClipboardLinkExtractor.Extract(
    "https://drive.google.com/file/d/1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/view",
    "<a href=\"https://drive.google.com/open?id=1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\">同一文件</a>",
    null);
Check(duplicateLinks.Count == 1, "剪贴板中的重复文件没有去重");

var sanitized = FileNameHelper.Sanitize("广告:成品?.mp4");
Check(!sanitized.Contains(':') && !sanitized.Contains('?'), "Windows 非法文件名字符未清理");

if (failures.Count > 0)
{
    Console.Error.WriteLine("Smoke tests failed:");
    failures.ForEach(failure => Console.Error.WriteLine(" - " + failure));
    return 1;
}

Console.WriteLine("All Green Apple smoke tests passed.");
return 0;

void ExpectDriveId(string input, string expected)
{
    var found = DriveLinkParser.TryExtractFileId(input, out var actual);
    Check(found && actual == expected, $"Drive ID 解析失败: {input}");
}

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}
