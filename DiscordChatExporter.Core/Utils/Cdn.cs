using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace DiscordChatExporter.Core.Utils;

// Helpers for working with Discord CDN/media URLs.
// https://discord.com/developers/docs/reference#image-cdn
public static class Cdn
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.discordapp.com",
        "media.discordapp.net",
        // Legacy hosts, still present in older messages
        "cdn.discordapp.media",
        "media.discordapp.com",
    };

    // Attachment URLs on the Discord CDN are signed using 'ex' (expiry), 'is' (ip signature),
    // and 'hm' (hash message) query parameters. The signatures expire after a while (~24h for
    // links produced by the API), which causes previously-exported URLs to fail with 403/404.
    private static readonly string[] SignatureQueryParameters = ["ex", "is", "hm"];

    public static bool IsDiscordCdnUrl(string url) => TryGetHostIfDiscordCdn(url, out _);

    // Whether the URL is a Discord attachment URL that carries expiring signature parameters
    public static bool IsExpiringAttachmentUrl(string url)
    {
        if (!TryGetHostIfDiscordCdn(url, out var uri) || uri is null)
            return false;

        return uri.AbsolutePath.StartsWith("/attachments/", StringComparison.OrdinalIgnoreCase)
            && SignatureQueryParameters.Any(p => HasQueryParameter(uri, p));
    }

    private static bool TryGetHostIfDiscordCdn(string url, out Uri? uri)
    {
        uri = null;

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed is null)
                return false;

            if (
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                || !AllowedHosts.Contains(parsed.Host)
            )
                return false;

            uri = parsed;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool HasQueryParameter(Uri uri, string name) =>
        HttpUtility.ParseQueryString(uri.Query).Get(name) is not null;

    // Normalizes a CDN URL by stripping the expiring signature parameters, so that the same
    // asset resolves to the same cache key regardless of when its URL was signed
    public static string StripSignatureParameters(string url)
    {
        if (!TryGetHostIfDiscordCdn(url, out var uri) || uri is null)
            return url;

        var query = HttpUtility.ParseQueryString(uri.Query);

        foreach (var parameter in SignatureQueryParameters)
            query.Remove(parameter);

        return uri.GetLeftPart(UriPartial.Path) + query;
    }
}
