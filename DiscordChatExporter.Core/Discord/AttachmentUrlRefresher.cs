using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils;
using JsonExtensions.Http;

namespace DiscordChatExporter.Core.Discord;

// Refreshes expired Discord CDN attachment URLs by calling
// POST https://discord.com/api/v9/attachments/refresh-urls.
// Attachment URLs are signed with 'ex'/'is'/'hm' query parameters that expire after ~24 hours,
// which makes previously-generated links fail with 403/404 when assets are downloaded later.
public partial class AttachmentUrlRefresher(
    DiscordClient discord,
    Snowflake guildId,
    Snowflake channelId
)
{
    // Discord accepts up to 50 URLs per request, but smaller batches keep payloads well below
    // any size limits and make partial failures cheaper. Must be between 1 and 50.
    public const int MaxUrlsPerBatch = 25;

    // Polite delay between batches, mirroring the rate-limit-friendly pacing of the API
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(400);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, string?> _refreshedUrlByOriginalUrl = new(
        StringComparer.Ordinal
    );

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<string> RefreshAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        var map = await RefreshAsync([url], cancellationToken);
        return map.TryGetValue(url, out var refreshed) && !string.IsNullOrWhiteSpace(refreshed)
            ? refreshed
            : url;
    }

    // Returns a map of original URL -> refreshed URL. URLs that could not be refreshed are
    // mapped to null so that callers can fall back to the original value.
    public async ValueTask<IReadOnlyDictionary<string, string?>> RefreshAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken = default
    )
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Only signed Discord CDN attachment URLs can be refreshed; everything else passes through
        var candidates = urls.Where(Cdn.IsExpiringAttachmentUrl)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length <= 0)
            return result;

        // Serve from cache first, then refresh only what's still missing
        var pending = new List<string>();

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            foreach (var candidate in candidates)
            {
                if (_refreshedUrlByOriginalUrl.TryGetValue(candidate, out var cached))
                    result[candidate] = cached;
                else
                    pending.Add(candidate);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        // Split into batches of at most MaxUrlsPerBatch URLs to avoid payload-size rejections
        for (var i = 0; i < pending.Count; i += MaxUrlsPerBatch)
        {
            var batch = pending.Skip(i).Take(MaxUrlsPerBatch).ToArray();

            IReadOnlyDictionary<string, string?> batchResult;
            try
            {
                batchResult = await RefreshBatchAsync(batch, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // Never let a failed refresh crash the export - the caller will just retry
            // the original (possibly dead) URL against the CDN
            catch (Exception)
            {
                batchResult = batch.ToDictionary(
                    url => url,
                    _ => (string?)null,
                    StringComparer.Ordinal
                );
            }

            foreach (var (original, refreshed) in batchResult)
            {
                result[original] = refreshed;

                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    _refreshedUrlByOriginalUrl[original] = refreshed;
                }
                finally
                {
                    _semaphore.Release();
                }
            }

            // Pace out the requests so we don't hammer the endpoint
            if (i + MaxUrlsPerBatch < pending.Count)
                await Task.Delay(BatchDelay, cancellationToken);
        }

        return result;
    }

    private async ValueTask<IReadOnlyDictionary<string, string?>> RefreshBatchAsync(
        IReadOnlyList<string> batch,
        CancellationToken cancellationToken
    )
    {
        // Discord requires this header on bot-authenticated requests; the token kind is resolved
        // (and cached) by the client itself as part of its normal request pipeline.
        var tokenKind = await discord.ResolveTokenKindInternalAsync(cancellationToken);

        // { "attachment_urls": ["url1", "url2"] }
        var payload = new AttachmentRefreshRequestPayload(batch);
        var content = new StringContent(
            JsonSerializer.Serialize(payload, SerializerOptions),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await discord.ExecutePostAsync(
            "attachments/refresh-urls",
            content,
            BuildContextPropertiesHeaderValue(tokenKind),
            cancellationToken
        );

        // 429s and transient server errors are already retried by the resilience pipeline;
        // anything else (401/403/404, etc.) should not kill the export, so bail out gracefully
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Failed to refresh attachment URLs ({response.StatusCode})"
            );

        var json = await response.Content.ReadAsJsonAsync(cancellationToken);

        var parsed = AttachmentRefreshResponsePayload.Parse(json);

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in parsed.RefreshedUrls)
        {
            if (!string.IsNullOrWhiteSpace(entry.Original))
                map[entry.Original] = entry.Refreshed;
        }

        // Any URL the API silently dropped from the response is treated as un-refreshable
        foreach (var url in batch)
            map.TryAdd(url, null);

        return map;
    }

    // Base64-encoded JSON metadata that Discord requires on bot-authenticated requests
    private string? BuildContextPropertiesHeaderValue(TokenKind tokenKind)
    {
        if (tokenKind != TokenKind.Bot)
            return null;

        var context = new Dictionary<string, string> { ["location"] = "DiscordChatExporter" };

        if (guildId != Snowflake.Zero)
            context["guild_id"] = guildId.ToString();

        if (channelId != Snowflake.Zero)
            context["channel_id"] = channelId.ToString();

        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(context));
    }
}
