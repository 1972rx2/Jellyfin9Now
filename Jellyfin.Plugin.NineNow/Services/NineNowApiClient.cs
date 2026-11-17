using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NineNow.Configuration;

namespace Jellyfin.Plugin.NineNow.Services;

/// <summary>Minimal client for Nine's current 9Now web and device-login APIs.</summary>
public static class NineNowApiClient
{
    private const string ApiBaseUrl = "https://tv-api.9now.com.au/v2/pages";
    private const string LiveStreamsUrl = "https://api.9now.com.au/ctv/livestreams";
    private const string AuthBaseUrl = "https://login.nine.com.au/api/device";
    private const string ActivationUrl = "https://9now.com.au/activate";
    private const string BrightcoveBaseUrl = "https://edge.api.brightcove.com/playback/v1/accounts/4460760524001/videos/";
    private const string BrightcovePolicyKey = "BCpkADawqM17uNt4BA9TibECIvv8vBVypgHHIgThenKM55b88yzwUAmQ5hHbEfpsaQCimxMfcJglqzWqPTc21Mbnt4H-49t8_htP91BPml8bDw7AjWou9m_avlno4V7DBRsuLWdpLOoUMziK";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";

    /// <summary>The user agent attached to provider HLS requests.</summary>
    public const string StreamUserAgent = UserAgent;

    private static readonly HttpClientHandler SharedHandler = CreateHandler();
    private static readonly HttpClient Client = CreateClient(SharedHandler);
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    /// <summary>Gets the provider's featured rails.</summary>
    public static async Task<JsonDocument> GetFeaturedAsync(CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GetJsonAsync(BuildUrl(ApiBaseUrl + "/home", ("device", "web")), true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the all-shows catalogue.</summary>
    public static async Task<JsonDocument> GetShowsAsync(CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GetJsonAsync(BuildUrl(ApiBaseUrl + "/tv-series", ("device", "web")), true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the show's seasons and related metadata.</summary>
    public static async Task<JsonDocument> GetShowAsync(string slug, CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GetJsonAsync(BuildUrl(ApiBaseUrl + "/tv-series/" + Uri.EscapeDataString(slug), ("device", "web")), true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the episodes for one season.</summary>
    public static async Task<JsonDocument> GetEpisodesAsync(string showSlug, string seasonSlug, CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);
        string path = ApiBaseUrl + "/tv-series/" + Uri.EscapeDataString(showSlug) + "/seasons/" + Uri.EscapeDataString(seasonSlug) + "/episodes";
        return await GetJsonAsync(BuildUrl(path, ("device", "web"), ("sort", string.Empty)), true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets 9Now genres.</summary>
    public static async Task<JsonDocument> GetGenresAsync(CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GetJsonAsync(BuildUrl(ApiBaseUrl + "/genres", ("device", "web")), true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets shows in one genre.</summary>
    public static async Task<JsonDocument> GetGenreAsync(string slug, CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GetJsonAsync(BuildUrl(ApiBaseUrl + "/genres/" + Uri.EscapeDataString(slug), ("device", "web")), true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets live channels and events for the selected Australian region.</summary>
    public static async Task<JsonDocument> GetLiveStreamsAsync(string region, CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);
        string url = BuildUrl(
            LiveStreamsUrl,
            ("device", "web"),
            ("streamParams", "web,chrome,windows"),
            ("region", region),
            ("offset", "0"));
        return await GetJsonAsync(url, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves an on-demand video to a clear HLS manifest returned by Brightcove.</summary>
    public static async Task<string> GetPlaybackAsync(string reference, CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);
        string videoReference = reference.All(char.IsDigit) ? reference : "ref:" + reference;
        string url = BrightcoveBaseUrl + Uri.EscapeDataString(videoReference);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("BCOV-POLICY", BrightcovePolicyKey);
        AddAuthorization(request);

        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        ThrowIfProviderError(document.RootElement);

        if (!document.RootElement.TryGetProperty("sources", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("9Now did not return any playback sources.");
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            string? type = GetString(source, "type");
            string? container = GetString(source, "container");
            string? manifest = GetString(source, "src");
            bool isHls = string.Equals(type, "application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "application/x-mpegURL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(container, "M2TS", StringComparison.OrdinalIgnoreCase)
                || (manifest?.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ?? false);
            bool hasDrm = source.TryGetProperty("key_systems", out JsonElement systems)
                && systems.ValueKind == JsonValueKind.Object
                && systems.EnumerateObject().Any();

            if (isHls && !hasDrm && Uri.TryCreate(manifest, UriKind.Absolute, out Uri? manifestUri) && manifestUri.Scheme == Uri.UriSchemeHttps)
            {
                return manifestUri.ToString();
            }
        }

        bool drmOnly = sources.EnumerateArray().Any(source => source.TryGetProperty("key_systems", out JsonElement systems)
            && systems.ValueKind == JsonValueKind.Object && systems.EnumerateObject().Any());
        if (drmOnly)
        {
            throw new InvalidOperationException("9Now marks this video as DRM-protected. This plugin only plays clear streams supported by Jellyfin.");
        }

        throw new InvalidOperationException("9Now did not return a clear HLS stream for this video.");
    }

    /// <summary>Resolves a live channel reference to its currently advertised stream URL.</summary>
    public static async Task<string> GetLivePlaybackAsync(string reference, string region, CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetLiveStreamsAsync(region, cancellationToken).ConfigureAwait(false);
        JsonElement rows = GetLiveChannelRows(document.RootElement);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (!string.Equals(GetString(row, "referenceId"), reference, StringComparison.Ordinal))
            {
                continue;
            }

            if (row.TryGetProperty("stream", out JsonElement stream) && GetString(stream, "url") is string streamUrl
                && Uri.TryCreate(streamUrl, UriKind.Absolute, out Uri? streamUri) && streamUri.Scheme == Uri.UriSchemeHttps)
            {
                return streamUri.ToString();
            }

            throw new InvalidOperationException("9Now did not provide a live stream URL for this channel.");
        }

        throw new InvalidOperationException("This 9Now live channel is no longer available in the selected region.");
    }

    /// <summary>Starts the provider's device-code sign-in flow.</summary>
    public static async Task<DeviceLoginStart> StartDeviceLoginAsync(CancellationToken cancellationToken)
    {
        string url = BuildUrl(AuthBaseUrl + "/code", ("client_id", "9nowdevice"));
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>()),
        };
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string authCode = GetString(root, "auth_code") ?? throw new InvalidOperationException("Nine did not return an activation code.");
        string deviceCode = GetString(root, "device_code") ?? throw new InvalidOperationException("Nine did not return a device code.");
        int expiresIn = GetInt32(root, "expires_in", 600);
        int pollInterval = Math.Clamp(GetInt32(root, "interval", 5), 2, 30);
        if (expiresIn is < 30 or > 3600)
        {
            throw new InvalidOperationException("Nine returned an invalid activation-code lifetime.");
        }

        return new DeviceLoginStart(authCode, deviceCode, new Uri(ActivationUrl), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn, pollInterval);
    }

    /// <summary>Checks whether a device code has been approved and saves the resulting account tokens.</summary>
    public static async Task<DeviceLoginCompletion> CompleteDeviceLoginAsync(
        PluginConfiguration configuration,
        string authCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(authCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);

        string url = BuildUrl(
            AuthBaseUrl + "/token",
            ("auth_code", authCode),
            ("device_code", deviceCode),
            ("client_id", "9nowdevice"),
            ("response_types", "id_token"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("accessToken", out JsonElement accessTokenElement) || string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
        {
            string? detail = GetString(root, "error_description") ?? GetString(root, "error") ?? GetString(root, "message");
            return new DeviceLoginCompletion(false, detail);
        }

        if (!response.IsSuccessStatusCode)
        {
            string status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            return new DeviceLoginCompletion(false, "Nine returned HTTP " + status + " while checking sign-in.");
        }

        SaveTokens(configuration, root);
        return new DeviceLoginCompletion(true, null);
    }

    private static async Task EnsureFreshTokenAsync(CancellationToken cancellationToken)
    {
        PluginConfiguration configuration = NineNowPlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("The 9Now plugin is not initialized.");
        if (string.IsNullOrWhiteSpace(configuration.AccessToken))
        {
            throw new InvalidOperationException("Sign in to 9Now from Plugins → 9Now in the Jellyfin dashboard first.");
        }

        if (configuration.TokenExpiresAtUnixSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return;
        }

        await TokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (configuration.TokenExpiresAtUnixSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(configuration.RefreshToken))
            {
                throw new InvalidOperationException("The saved 9Now session has expired. Sign in again from the Jellyfin dashboard.");
            }

            string url = BuildUrl(
                AuthBaseUrl + "/refresh-token",
                ("refresh_token", configuration.RefreshToken),
                ("client_id", "9nowdevice"),
                ("response_types", "id_token"));
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument tokens = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (tokens.RootElement.TryGetProperty("error", out JsonElement error))
            {
                throw new InvalidOperationException("9Now session refresh failed: " + (error.GetString() ?? "sign in again"));
            }

            SaveTokens(configuration, tokens.RootElement);
            NineNowPlugin.Instance!.SaveConfiguration(configuration);
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static void SaveTokens(PluginConfiguration configuration, JsonElement token)
    {
        string accessToken = GetString(token, "accessToken") ?? string.Empty;
        string? refreshToken = GetString(token, "refresh_token") ?? GetString(token, "refreshToken");
        decimal seconds;
        JsonElement expiry = token.TryGetProperty("expiresIn", out JsonElement camelExpiry) ? camelExpiry : default;
        bool validExpiry = expiry.ValueKind == JsonValueKind.Number
            ? expiry.TryGetDecimal(out seconds)
            : decimal.TryParse(GetString(token, "expiresIn"), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!validExpiry || seconds <= 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds() - now
            || string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Nine returned an invalid account session or token expiry.");
        }

        configuration.AccessToken = accessToken;
        configuration.RefreshToken = refreshToken;
        configuration.TokenExpiresAtUnixSeconds = now + (long)decimal.Floor(seconds) - 30;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, bool authenticated, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (authenticated)
        {
            AddAuthorization(request);
        }

        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        JsonDocument document = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        ThrowIfProviderError(document.RootElement);
        return document;
    }

    private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void AddAuthorization(HttpRequestMessage request)
    {
        string? token = NineNowPlugin.Instance?.Configuration.AccessToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static string BuildUrl(string baseUrl, params (string Name, string Value)[] query)
    {
        string encoded = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Name) + "=" + Uri.EscapeDataString(pair.Value)));
        return baseUrl + (baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?") + encoded;
    }

    private static HttpClientHandler CreateHandler()
    {
        return new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            CheckCertificateRevocationList = true,
        };
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static void ThrowIfProviderError(JsonElement root)
    {
        if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            string? message = GetString(errors[0], "message");
            throw new InvalidOperationException(message ?? "9Now returned an API error.");
        }
    }

    private static JsonElement GetLiveChannelRows(JsonElement root)
    {
        if (root.TryGetProperty("data", out JsonElement data)
            && data.TryGetProperty("getLivestream", out JsonElement live)
            && live.TryGetProperty("channels", out JsonElement channels)
            && channels.ValueKind == JsonValueKind.Array)
        {
            return channels;
        }

        throw new InvalidOperationException("9Now did not return a live-channel list.");
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static int GetInt32(JsonElement element, string property, int fallback)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : fallback;
    }
}

/// <summary>A device activation request returned to the Jellyfin dashboard.</summary>
public sealed record DeviceLoginStart(string AuthCode, string DeviceCode, Uri ActivationUrl, long ExpiresAtUnixSeconds, int PollIntervalSeconds);

/// <summary>The outcome of a device-code sign-in poll.</summary>
public sealed record DeviceLoginCompletion(bool Complete, string? Detail);
