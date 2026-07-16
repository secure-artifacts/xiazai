using System.Text.RegularExpressions;

namespace GreenAppleDownloader.Services;

public static partial class DriveLinkParser
{
    [GeneratedRegex(@"(?:/d/|[?&]id=)([A-Za-z0-9_-]{15,})", RegexOptions.IgnoreCase)]
    private static partial Regex DriveIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{20,}$")]
    private static partial Regex BareIdRegex();

    public static bool TryExtractFileId(string? input, out string fileId)
    {
        fileId = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var value = input.Trim();
        var match = DriveIdRegex().Match(value);
        if (match.Success)
        {
            fileId = match.Groups[1].Value;
            return true;
        }

        if (BareIdRegex().IsMatch(value))
        {
            fileId = value;
            return true;
        }

        return false;
    }

    public static string? ExtractSpreadsheetId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var value = input.Trim();
        var marker = "/spreadsheets/d/";
        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var start = markerIndex + marker.Length;
            var end = value.IndexOf('/', start);
            return end > start ? value[start..end] : value[start..].Split('?', '#')[0];
        }

        return BareIdRegex().IsMatch(value) ? value : null;
    }
}

