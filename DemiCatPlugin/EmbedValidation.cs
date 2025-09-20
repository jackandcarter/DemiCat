using System;
using System.Collections.Generic;
using DiscordHelper;

namespace DemiCatPlugin;

internal static class EmbedValidation
{
    private const int TitleLimit = 256;
    private const int DescriptionLimit = 4096;
    private const int FieldNameLimit = 256;
    private const int FieldValueLimit = 1024;
    private const int FieldCountLimit = 25;
    private const int FooterTextLimit = 2048;
    private const int AuthorNameLimit = 256;
    private const int TotalCharLimit = 6000;
    private const int ButtonLabelLimit = 80;
    private const int ButtonCountLimit = 25;
    private const int ButtonWidthLimit = 200;

    internal static List<string> Validate(EmbedDto embed, IReadOnlyList<EmbedButtonDto> buttons)
    {
        var warnings = new List<string>();
        var total = 0;

        if (!string.IsNullOrEmpty(embed.Title))
        {
            if (embed.Title.Length > TitleLimit)
            {
                warnings.Add("Title too long");
            }
            total += embed.Title.Length;
        }

        if (!string.IsNullOrEmpty(embed.Description))
        {
            if (embed.Description.Length > DescriptionLimit)
            {
                warnings.Add("Description too long");
            }
            total += embed.Description.Length;
        }

        if (!string.IsNullOrEmpty(embed.FooterText))
        {
            if (embed.FooterText.Length > FooterTextLimit)
            {
                warnings.Add("Footer too long");
            }
            total += embed.FooterText.Length;
        }

        if (!string.IsNullOrEmpty(embed.AuthorName))
        {
            if (embed.AuthorName.Length > AuthorNameLimit)
            {
                warnings.Add("Author name too long");
            }
            total += embed.AuthorName.Length;
        }

        if (embed.Fields != null)
        {
            if (embed.Fields.Count > FieldCountLimit)
            {
                warnings.Add("Too many fields");
            }

            foreach (var field in embed.Fields)
            {
                if (field.Name.Length > FieldNameLimit)
                {
                    warnings.Add("Field name too long");
                }

                if (field.Value.Length > FieldValueLimit)
                {
                    warnings.Add("Field value too long");
                }

                total += field.Name.Length + field.Value.Length;
            }
        }

        if (total > TotalCharLimit)
        {
            warnings.Add("Embed too large");
        }

        CheckUrl("url", embed.Url, warnings);
        CheckUrl("thumbnail url", embed.ThumbnailUrl, warnings);
        CheckUrl("image url", embed.ImageUrl, warnings);
        CheckUrl("provider url", embed.ProviderUrl, warnings);
        CheckUrl("footer icon url", embed.FooterIconUrl, warnings);
        CheckUrl("author icon url", embed.AuthorIconUrl, warnings);
        CheckUrl("video url", embed.VideoUrl, warnings);

        if (embed.Authors != null)
        {
            foreach (var author in embed.Authors)
            {
                if (!string.IsNullOrEmpty(author.Name) && author.Name.Length > AuthorNameLimit)
                {
                    warnings.Add("Author name too long");
                }

                CheckUrl("author url", author.Url, warnings);
                CheckUrl("author icon url", author.IconUrl, warnings);
            }
        }

        if (buttons.Count > 0)
        {
            if (buttons.Count > ButtonCountLimit)
            {
                warnings.Add("Too many buttons");
            }

            foreach (var button in buttons)
            {
                if (!string.IsNullOrEmpty(button.Label) && button.Label.Length > ButtonLabelLimit)
                {
                    warnings.Add("Button label too long");
                }

                CheckUrl("button url", button.Url, warnings);

                var width = button.Width ?? 1;
                if (width < 1 || width > ButtonWidthLimit)
                {
                    warnings.Add("Invalid button width");
                }
            }
        }

        return warnings;
    }

    private static void CheckUrl(string name, string? url, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!IsHttpUrl(url))
        {
            warnings.Add($"Invalid {name}");
        }
    }

    internal static bool IsHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
