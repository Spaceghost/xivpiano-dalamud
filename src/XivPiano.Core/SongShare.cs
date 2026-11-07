using System.Globalization;
using System.Text;

namespace XivPiano.Core;

/// <summary>Clipboard-only public song text; never includes stream URLs or account tokens.</summary>
public static class SongShare
{
    public const int MaxBytes = 480;

    public static string Format(string title, string artist, string? detailUrl)
    {
        var text = "Listening to " + Clean(title, 120) + " — " + Clean(artist, 100);
        if (Uri.TryCreate(detailUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.IsDefaultPort
            && (uri.Host.Equals("pandora.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.pandora.com", StringComparison.OrdinalIgnoreCase)))
        {
            // Only the public song path is shared; query/fragment can contain tracking or credentials.
            var link = uri.GetLeftPart(UriPartial.Path);
            if (Encoding.UTF8.GetByteCount(text + " " + link) <= MaxBytes) text += " " + link;
        }
        return text;
    }

    private static string Clean(string value, int maxBytes)
    {
        var result = new StringBuilder();
        int bytes = 0;
        bool space = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            { space = result.Length > 0; continue; }
            int next = rune.Utf8SequenceLength + (space ? 1 : 0);
            if (bytes + next > maxBytes) break;
            if (space) result.Append(' ');
            result.Append(rune); bytes += next; space = false;
        }
        return result.Length == 0 ? "unknown" : result.ToString();
    }
}
