using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using JsonExtensions.Reading;

namespace DiscordChatExporter.Core.Discord.Data;

// One entry of the 'refreshed_urls' response array returned by
// POST /api/v9/attachments/refresh-urls.
// https://discord.com/developers/docs/resources/channel#refresh-urls
public partial record RefreshedAttachmentUrl(string Original, string? Refreshed);

public partial record RefreshedAttachmentUrl
{
    public static RefreshedAttachmentUrl Parse(JsonElement json)
    {
        var original = json.GetProperty("original").GetNonWhiteSpaceString();

        // May be null if the URL could not be refreshed (e.g. deleted attachment or no access)
        var refreshed = json.GetPropertyOrNull("refreshed")?.GetStringOrNull();

        return new RefreshedAttachmentUrl(original, refreshed);
    }
}

// Request payload for POST /api/v9/attachments/refresh-urls.
// Serialized explicitly with a JSON property name attribute, so no custom naming policy is needed:
// { "attachment_urls": ["url1", "url2"] }
public partial record AttachmentRefreshRequestPayload(
    [property: JsonPropertyName("attachment_urls")] IReadOnlyList<string> AttachmentUrls
);

// Response envelope for POST /api/v9/attachments/refresh-urls:
// { "refreshed_urls": [{ "original": "...", "refreshed": "..." }] }
public partial record AttachmentRefreshResponsePayload(
    [property: JsonPropertyName("refreshed_urls")]
        IReadOnlyList<RefreshedAttachmentUrl> RefreshedUrls
);

public partial record AttachmentRefreshResponsePayload
{
    public static AttachmentRefreshResponsePayload Parse(JsonElement json) =>
        new(
            json.GetProperty("refreshed_urls")
                .EnumerateArray()
                .Select(RefreshedAttachmentUrl.Parse)
                .ToArray()
        );
}
