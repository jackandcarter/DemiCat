using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DiscordHelper;

namespace DemiCatPlugin;

public static class BridgeMessageFormatter
{
    private const int DiscordContentLimit = 2000;
    private const int DiscordAttachmentLimit = 10;
    private const long DiscordAttachmentSizeLimit = 25 * 1024 * 1024;
    private const int DiscordEmbedDescriptionLimit = 4096;
    private const int DiscordEmbedTotalLimit = 6000;
    private const int DiscordEmbedCountLimit = 10;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    public sealed class BridgeFormatterOptions
    {
        public bool UseCharacterName { get; init; }
        public string? ChannelKind { get; init; }
        public IEnumerable<PresenceDto> Presences { get; init; } = Array.Empty<PresenceDto>();
        public IEnumerable<RoleDto> Roles { get; init; } = Array.Empty<RoleDto>();
        public IEnumerable<string>? AllowedRoleIds { get; init; }
        public string? AuthorName { get; init; }
        public string? CharacterName { get; init; }
        public string? WorldName { get; init; }
        public string? AuthorAvatarUrl { get; init; }
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    public sealed class BridgeFormattedAttachment
    {
        public string Path { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public bool IsImage { get; init; }
        public long? FileSize { get; init; }
    }

    public sealed class BridgeFormattedMessage
    {
        public static readonly BridgeFormattedMessage Empty = new();

        public string Content { get; init; } = string.Empty;
        public string DisplayContent { get; init; } = string.Empty;
        public IReadOnlyList<EmbedDto> Embeds { get; init; } = Array.Empty<EmbedDto>();
        public IReadOnlyList<BridgeFormattedAttachment> Attachments { get; init; } = Array.Empty<BridgeFormattedAttachment>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<DiscordMentionDto> Mentions { get; init; } = Array.Empty<DiscordMentionDto>();
        public string Nonce { get; init; } = string.Empty;
    }

    public static BridgeFormattedMessage Format(
        string? rawContent,
        IEnumerable<string> attachmentPaths,
        BridgeFormatterOptions options)
    {
        rawContent ??= string.Empty;
        var normalized = NormalizeLineEndings(rawContent);

        var resolution = MentionResolver.ResolveDetailed(
            normalized,
            options.Presences ?? Array.Empty<PresenceDto>(),
            options.Roles ?? Array.Empty<RoleDto>(),
            options.AllowedRoleIds);

        var content = resolution.Content;
        var displayContent = ChatWindow.ReplaceMentionTokens(content, resolution.Mentions);
        displayContent = displayContent.Replace("@\u200B", "@");

        var warnings = new List<string>();
        var errors = new List<string>();

        if (content.Length > DiscordContentLimit)
        {
            errors.Add($"Message exceeds Discord's {DiscordContentLimit} character limit.");
        }

        var embedChunks = SplitIntoEmbedChunks(displayContent, warnings, errors);
        var embeds = BuildEmbeds(embedChunks, options, warnings);

        var attachments = ProcessAttachments(attachmentPaths, warnings, errors);

        return new BridgeFormattedMessage
        {
            Content = content,
            DisplayContent = displayContent,
            Embeds = embeds,
            Attachments = attachments,
            Warnings = warnings,
            Errors = errors,
            Mentions = resolution.Mentions,
            Nonce = Guid.NewGuid().ToString("N")
        };
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static IReadOnlyList<EmbedDto> BuildEmbeds(
        IReadOnlyList<string> chunks,
        BridgeFormatterOptions options,
        List<string> warnings)
    {
        var list = new List<EmbedDto>();
        var chunkCount = chunks.Count > 0 ? chunks.Count : 1;
        var baseFooter = BuildFooterBase(options);

        if (chunkCount > DiscordEmbedCountLimit)
        {
            warnings.Add($"Embed count reduced to {DiscordEmbedCountLimit} to satisfy Discord limits.");
            chunkCount = DiscordEmbedCountLimit;
        }

        for (var i = 0; i < chunkCount; i++)
        {
            var description = chunks.Count > 0 ? chunks[Math.Min(i, chunks.Count - 1)] : string.Empty;
            var embed = new EmbedDto
            {
                Id = Guid.NewGuid().ToString("N"),
                Description = description,
                Timestamp = options.Timestamp,
                AuthorName = DetermineAuthor(options),
                AuthorIconUrl = options.AuthorAvatarUrl,
                FooterText = BuildFooter(baseFooter, chunkCount, i),
                Color = DetermineColor(options.ChannelKind)
            };
            list.Add(embed);
        }

        return list;
    }

    private static string BuildFooterBase(BridgeFormatterOptions options)
    {
        var channelLabel = options.ChannelKind switch
        {
            ChannelKind.OfficerChat => "Officer Chat",
            ChannelKind.FcChat => "FC Chat",
            ChannelKind.Chat => "Chat",
            _ => string.IsNullOrEmpty(options.ChannelKind) ? "Chat" : options.ChannelKind
        };

        var footer = new StringBuilder(channelLabel);
        if (!string.IsNullOrWhiteSpace(options.WorldName))
        {
            footer.Append(' ');
            footer.Append('•');
            footer.Append(' ');
            footer.Append(options.WorldName);
        }
        footer.Append(" • DemiCat");
        return footer.ToString();
    }

    private static string BuildFooter(string baseFooter, int totalChunks, int index)
    {
        if (totalChunks <= 1)
            return baseFooter;
        return $"{baseFooter} • Part {index + 1}/{totalChunks}";
    }

    private static string DetermineAuthor(BridgeFormatterOptions options)
    {
        if (options.UseCharacterName && !string.IsNullOrWhiteSpace(options.CharacterName))
            return options.CharacterName;
        if (!string.IsNullOrWhiteSpace(options.AuthorName))
            return options.AuthorName;
        return "You";
    }

    private static uint DetermineColor(string? channelKind)
    {
        return channelKind switch
        {
            ChannelKind.OfficerChat => 0xED4245, // red-ish for emphasis
            _ => 0x5865F2 // Discord blurple
        };
    }

    private static IReadOnlyList<string> SplitIntoEmbedChunks(
        string displayContent,
        List<string> warnings,
        List<string> errors)
    {
        if (string.IsNullOrEmpty(displayContent))
            return Array.Empty<string>();

        var chunks = new List<string>();
        var remaining = displayContent;
        var total = 0;
        var splitOccurred = false;

        while (!string.IsNullOrEmpty(remaining) && total < DiscordEmbedTotalLimit)
        {
            var take = Math.Min(DiscordEmbedDescriptionLimit, remaining.Length);
            var slice = remaining[..take];

            if (remaining.Length > take)
            {
                var breakPos = FindSplitPosition(slice);
                if (breakPos > 0)
                {
                    slice = slice[..breakPos];
                    take = breakPos;
                }
                splitOccurred = true;
            }

            chunks.Add(slice);
            remaining = remaining.Length > take ? remaining[take..] : string.Empty;
            total += slice.Length;
            if (chunks.Count >= DiscordEmbedCountLimit && !string.IsNullOrEmpty(remaining))
            {
                break;
            }
        }

        if (!string.IsNullOrEmpty(remaining))
        {
            errors.Add("Message exceeds Discord embed length limits and was truncated.");
        }
        else if (splitOccurred)
        {
            warnings.Add("Message split across multiple embeds to satisfy Discord limits.");
        }

        return chunks;
    }

    private static int FindSplitPosition(string text)
    {
        var newline = text.LastIndexOf('\n');
        if (newline >= 0 && newline >= text.Length / 2)
            return newline + 1;
        var space = text.LastIndexOf(' ');
        if (space >= 0 && space >= text.Length / 2)
            return space + 1;
        return text.Length;
    }

    private static IReadOnlyList<BridgeFormattedAttachment> ProcessAttachments(
        IEnumerable<string> attachmentPaths,
        List<string> warnings,
        List<string> errors)
    {
        var attachments = new List<BridgeFormattedAttachment>();
        foreach (var rawPath in attachmentPaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;
            var path = rawPath.Trim();
            var fileName = Path.GetFileName(path);
            long? size = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    size = info.Length;
                }
                else
                {
                    errors.Add($"Attachment '{fileName}' could not be read.");
                    continue;
                }
            }
            catch
            {
                errors.Add($"Attachment '{fileName}' could not be read.");
                continue;
            }

            if (size.HasValue && size.Value > DiscordAttachmentSizeLimit)
            {
                errors.Add($"Attachment '{fileName}' exceeds the 25 MB limit.");
                continue;
            }

            var ext = Path.GetExtension(path);
            attachments.Add(new BridgeFormattedAttachment
            {
                Path = path,
                FileName = fileName,
                IsImage = !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext),
                FileSize = size
            });
        }

        if (attachments.Count > DiscordAttachmentLimit)
        {
            errors.Add($"Too many attachments (max {DiscordAttachmentLimit}).");
        }

        return attachments;
    }
}
