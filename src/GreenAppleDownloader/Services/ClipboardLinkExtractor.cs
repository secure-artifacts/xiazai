using System.Net;
using System.Text.RegularExpressions;

namespace GreenAppleDownloader.Services;

/// <summary>
/// Extracts Google Drive links from the three clipboard representations most
/// commonly produced by browsers and spreadsheets: plain text, HTML and RTF.
/// </summary>
public static partial class ClipboardLinkExtractor
{
    [GeneratedRegex("href\\s*=\\s*(?:\"(?<url>[^\"]+)\"|'(?<url>[^']+)')", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHrefRegex();

    [GeneratedRegex("HYPERLINK\\s+(?:\\\\?\")(?<url>https?://[^\"\\\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RtfHyperlinkRegex();

    [GeneratedRegex("https?://(?:drive|docs)\\.google\\.com/[^\\s<>\"']+", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleDriveUrlRegex();

    public static IReadOnlyList<string> Extract(string? plainText, string? html, string? rtf)
    {
        var links = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(html))
        {
            var decodedHtml = WebUtility.HtmlDecode(html);
            foreach (Match match in HtmlHrefRegex().Matches(decodedHtml))
            {
                AddCandidate(match.Groups["url"].Value, links, seenIds);
            }
            AddUrls(decodedHtml, links, seenIds);
        }

        if (!string.IsNullOrWhiteSpace(rtf))
        {
            foreach (Match match in RtfHyperlinkRegex().Matches(rtf))
            {
                AddCandidate(match.Groups["url"].Value, links, seenIds);
            }
            AddUrls(rtf, links, seenIds);
        }

        if (!string.IsNullOrWhiteSpace(plainText))
        {
            AddUrls(plainText, links, seenIds);
            foreach (var line in plainText.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddCandidate(line, links, seenIds);
            }
        }

        return links;
    }

    private static void AddUrls(string source, List<string> links, HashSet<string> seenIds)
    {
        foreach (Match match in GoogleDriveUrlRegex().Matches(source))
        {
            AddCandidate(match.Value, links, seenIds);
        }
    }

    private static void AddCandidate(string? rawValue, List<string> links, HashSet<string> seenIds)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        var value = WebUtility.HtmlDecode(rawValue).Trim().Trim('"', '\'', '(', ')', '[', ']', '<', '>', ',', ';');
        value = UnwrapGoogleRedirect(value);
        if (!DriveLinkParser.TryExtractFileId(value, out var fileId) || !seenIds.Add(fileId))
        {
            return;
        }

        // A canonical URL is clearer in the text box and avoids carrying
        // browser redirect/tracking parameters into the task list.
        links.Add($"https://drive.google.com/file/d/{fileId}/view");
    }

    private static string UnwrapGoogleRedirect(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals("/url", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && (pair[0] == "q" || pair[0] == "url"))
            {
                return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
            }
        }

        return value;
    }
}
