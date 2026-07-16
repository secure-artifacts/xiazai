using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GreenAppleDownloader.Models;

public sealed class DownloadTaskItem : INotifyPropertyChanged
{
    private string _status = "等待下载";
    private string _fileName = "正在获取文件信息…";
    private double _progress;
    private string _error = string.Empty;
    private string _localPath = string.Empty;

    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
    public int QueueRow { get; set; }
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O");
    public string SourceSheet { get; set; } = "手动添加";
    public string SourceCell { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public string DriveUrl { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;

    public string FileName
    {
        get => _fileName;
        set => SetField(ref _fileName, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, Math.Clamp(value, 0, 100));
    }

    public string Error
    {
        get => _error;
        set => SetField(ref _error, value);
    }

    public string LocalPath
    {
        get => _localPath;
        set => SetField(ref _localPath, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

