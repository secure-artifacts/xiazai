namespace GreenAppleDownloader.Services;

public static class FileNameHelper
{
    public static string Sanitize(string? value, string fallback = "视频")
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            source = source.Replace(invalid, '_');
        }

        source = source.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(source) ? fallback : source;
    }

    public static string EnsureUniqueDirectory(string root, string preferredName)
    {
        Directory.CreateDirectory(root);
        var safeName = Sanitize(preferredName, "成品下载");
        var path = Path.Combine(root, safeName);
        var counter = 2;

        while (Directory.Exists(path) || File.Exists(path))
        {
            path = Path.Combine(root, $"{safeName}_{counter++}");
        }

        Directory.CreateDirectory(path);
        return path;
    }
}

