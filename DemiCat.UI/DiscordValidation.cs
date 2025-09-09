using System;
using System.Text.RegularExpressions;

namespace DemiCat.UI;

public static class DiscordValidation
{
    private static readonly Regex HttpOrHttps = new(@"^https?://", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttachmentScheme = new(@"^attachment://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsImageUrlAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true; // empty is allowed in many fields
        return HttpOrHttps.IsMatch(url) || AttachmentScheme.IsMatch(url);
    }
}
