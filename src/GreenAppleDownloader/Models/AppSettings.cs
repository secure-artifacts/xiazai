namespace GreenAppleDownloader.Models;

public sealed class AppSettings
{
    public string ClientSecretsPath { get; set; } = string.Empty;
    public string SpreadsheetId { get; set; } = string.Empty;
    public string DownloadRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "青苹果下载");

    public int Concurrency { get; set; } = 2;
    public int MaxRetries { get; set; } = 5;
    public int PollSeconds { get; set; } = 5;
    public bool AutoSync { get; set; } = true;
    public bool OpenFolderWhenComplete { get; set; } = true;
    public bool ZipAfterDownload { get; set; }
}

