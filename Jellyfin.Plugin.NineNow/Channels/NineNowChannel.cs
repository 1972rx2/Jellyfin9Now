using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NineNow.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NineNow.Channels;

/// <summary>The browseable 9Now channel.</summary>
public sealed partial class NineNowChannel : IChannel, IRequiresMediaInfoCallback
{
    private const string ChannelLogoResource = "Jellyfin.Plugin.NineNow.Assets.NineNowLogo.png";
    private readonly ILogger<NineNowChannel> _logger;
    private readonly DrmThumbnailRenderer _drmThumbnailRenderer;

    /// <summary>Initializes a new instance of the <see cref="NineNowChannel"/> class.</summary>
    public NineNowChannel(ILogger<NineNowChannel> logger, DrmThumbnailRenderer drmThumbnailRenderer)
    {
        _logger = logger;
        _drmThumbnailRenderer = drmThumbnailRenderer;
    }

    /// <inheritdoc />
    public string Name => "9Now";

    /// <inheritdoc />
    public string Description => "Australian catch-up and live TV from 9Now. A Nine account is required.";

    /// <inheritdoc />
    public string HomePageUrl => "https://www.9now.com.au/";

    /// <inheritdoc />
    public string DataVersion => typeof(NineNowChannel).Assembly.GetName().Version?.ToString() ?? "1";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => !string.IsNullOrWhiteSpace(NineNowPlugin.Instance?.Configuration.AccessToken);

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = [ChannelMediaContentType.Episode, ChannelMediaContentType.Movie],
            MediaTypes = [ChannelMediaType.Video],
            DefaultSortFields = [ChannelItemSortField.Name],
        };
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<ChannelItemInfo> items;
        string folderId = query.FolderId ?? string.Empty;

        if (folderId.Length == 0)
        {
            items =
            [
                CreateFolder("featured", "Featured"),
                CreateFolder("shows", "Shows"),
                CreateFolder("categories", "Categories"),
                CreateFolder("live", "Live TV"),
            ];
            try
            {
                using JsonDocument document = await NineNowApiClient.GetFeaturedAsync(cancellationToken).ConfigureAwait(false);
                foreach (JsonElement row in GetArray(document.RootElement, "items").EnumerateArray())
                {
                    if (GetString(row, "type") == "featured-rail")
                    {
                        items.AddRange((await ParseContentRowsAsync(GetArray(row, "items"), cancellationToken).ConfigureAwait(false)).Take(8));
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                LogFeaturedHomeLoadFailed(_logger, ex);
            }
        }
        else if (string.Equals(folderId, "featured", StringComparison.Ordinal))
        {
            items = await GetFeaturedRailsAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (folderId.StartsWith("rail:", StringComparison.Ordinal))
        {
            items = await GetFeaturedItemsAsync(DecodeId(folderId["rail:".Length..]), cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(folderId, "shows", StringComparison.Ordinal))
        {
            using JsonDocument document = await NineNowApiClient.GetShowsAsync(cancellationToken).ConfigureAwait(false);
            items = ParseShows(GetArray(document.RootElement, "tvSeries"));
        }
        else if (string.Equals(folderId, "categories", StringComparison.Ordinal))
        {
            using JsonDocument document = await NineNowApiClient.GetGenresAsync(cancellationToken).ConfigureAwait(false);
            items = ParseGenres(GetArray(document.RootElement, "genres"));
        }
        else if (folderId.StartsWith("genre:", StringComparison.Ordinal))
        {
            using JsonDocument document = await NineNowApiClient.GetGenreAsync(DecodeId(folderId["genre:".Length..]), cancellationToken).ConfigureAwait(false);
            items = ParseShows(GetArray(document.RootElement, "tvSeries"));
        }
        else if (folderId.StartsWith("show:", StringComparison.Ordinal))
        {
            string showSlug = DecodeId(folderId["show:".Length..]);
            using JsonDocument document = await NineNowApiClient.GetShowAsync(showSlug, cancellationToken).ConfigureAwait(false);
            items = ParseSeasons(showSlug, document.RootElement);
        }
        else if (folderId.StartsWith("season:", StringComparison.Ordinal))
        {
            string[] parts = folderId.Split(':');
            if (parts.Length != 3)
            {
                throw new InvalidOperationException("This 9Now season link is invalid. Reopen the show to refresh it.");
            }

            string showSlug = DecodeId(parts[1]);
            string seasonSlug = DecodeId(parts[2]);
            using JsonDocument document = await NineNowApiClient.GetEpisodesAsync(showSlug, seasonSlug, cancellationToken).ConfigureAwait(false);
            JsonElement episodeRows = document.RootElement.TryGetProperty("episodes", out JsonElement episodeList)
                ? GetArray(episodeList, "items")
                : default;
            if (episodeRows.ValueKind == JsonValueKind.Array && episodeRows.GetArrayLength() > 0)
            {
                items = await ParseVideosAsync(episodeRows, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using JsonDocument clips = await NineNowApiClient.GetClipsAsync(showSlug, seasonSlug, cancellationToken).ConfigureAwait(false);
                items = await ParseVideosAsync(GetArray(GetArray(clips.RootElement, "clips"), "items"), cancellationToken).ConfigureAwait(false);
            }
        }
        else if (string.Equals(folderId, "live", StringComparison.Ordinal))
        {
            using JsonDocument document = await NineNowApiClient.GetLiveStreamsAsync(NineNowPlugin.Instance?.Configuration.Region ?? "nsw", cancellationToken).ConfigureAwait(false);
            items = ParseLiveChannels(GetLiveChannelRows(document.RootElement));
        }
        else if (folderId.StartsWith("video:", StringComparison.Ordinal) || folderId.StartsWith("liveitem:", StringComparison.Ordinal))
        {
            items = [];
        }
        else
        {
            items = [];
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        if (type != ImageType.Primary)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false, Stream = Stream.Null, Format = ImageFormat.Png });
        }

        Stream? logo = typeof(NineNowChannel).Assembly.GetManifestResourceStream(ChannelLogoResource);
        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = logo is not null,
            Stream = logo ?? Stream.Null,
            Format = ImageFormat.Png,
        });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string manifest;
        if (id.StartsWith("video:", StringComparison.Ordinal))
        {
            manifest = await NineNowApiClient.GetPlaybackAsync(DecodeId(id["video:".Length..]), cancellationToken).ConfigureAwait(false);
        }
        else if (id.StartsWith("drmvideo:", StringComparison.Ordinal))
        {
            manifest = await NineNowApiClient.GetPlaybackAsync(DecodeId(id["drmvideo:".Length..]), cancellationToken).ConfigureAwait(false);
        }
        else if (id.StartsWith("liveitem:", StringComparison.Ordinal))
        {
            string region = NineNowPlugin.Instance?.Configuration.Region ?? "nsw";
            manifest = await NineNowApiClient.GetLivePlaybackAsync(DecodeId(id["liveitem:".Length..]), region, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException("Unsupported 9Now item.", nameof(id));
        }

        return
        [
            new MediaSourceInfo
            {
                Id = id,
                Path = manifest,
                Protocol = MediaProtocol.Http,
                Container = "m3u8",
                IsRemote = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = NineNowApiClient.StreamUserAgent },
            },
        ];
    }

    private static async Task<List<ChannelItemInfo>> GetFeaturedRailsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await NineNowApiClient.GetFeaturedAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<ChannelItemInfo>();
        foreach (JsonElement row in GetArray(document.RootElement, "items").EnumerateArray())
        {
            if (GetArray(row, "items").ValueKind == JsonValueKind.Array
                && GetArray(row, "items").GetArrayLength() > 0
                && GetString(row, "id") is string id
                && GetString(row, "title") is string title)
            {
                items.Add(CreateFolder("rail:" + EncodeId(id), title, GetImage(row, "w768"), GetString(row, "description")));
            }
        }

        return items;
    }

    private async Task<List<ChannelItemInfo>> GetFeaturedItemsAsync(string railId, CancellationToken cancellationToken)
    {
        using JsonDocument document = await NineNowApiClient.GetFeaturedAsync(cancellationToken).ConfigureAwait(false);
        foreach (JsonElement row in GetArray(document.RootElement, "items").EnumerateArray())
        {
            if (string.Equals(GetString(row, "id"), railId, StringComparison.Ordinal))
            {
                return await ParseContentRowsAsync(GetArray(row, "items"), cancellationToken).ConfigureAwait(false);
            }
        }

        return [];
    }

    private async Task<List<ChannelItemInfo>> ParseContentRowsAsync(JsonElement rows, CancellationToken cancellationToken)
    {
        var items = new List<ChannelItemInfo>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string? linkType = row.TryGetProperty("link", out JsonElement link) ? GetString(link, "type") : null;
            if (linkType == "tv-series" || GetString(row, "type") == "tv-series")
            {
                if (ParseShow(row) is ChannelItemInfo show)
                {
                    items.Add(show);
                }
            }
            else if (linkType is "episode" or "clip" || row.TryGetProperty("video", out _))
            {
                if (await ParseVideoAsync(row, cancellationToken).ConfigureAwait(false) is ChannelItemInfo video)
                {
                    items.Add(video);
                }
            }
        }

        return items;
    }

    private static List<ChannelItemInfo> ParseShows(JsonElement rows)
    {
        var items = new List<ChannelItemInfo>();
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (ParseShow(row) is ChannelItemInfo show)
            {
                items.Add(show);
            }
        }

        return items;
    }

    private static ChannelItemInfo? ParseShow(JsonElement row)
    {
        string? slug = GetString(row, "slug");
        string? name = GetString(row, "name");
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return CreateFolder("show:" + EncodeId(slug), name, GetImage(row, "w768"), GetString(row, "description"));
    }

    private static List<ChannelItemInfo> ParseGenres(JsonElement rows)
    {
        var items = new List<ChannelItemInfo>();
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement row in rows.EnumerateArray())
        {
            string? slug = GetString(row, "slug");
            string? name = GetString(row, "name");
            if (!string.IsNullOrWhiteSpace(slug) && !string.IsNullOrWhiteSpace(name))
            {
                items.Add(CreateFolder("genre:" + EncodeId(slug), name, GetImage(row, "w768"), GetString(row, "description")));
            }
        }

        return items;
    }

    private static List<ChannelItemInfo> ParseSeasons(string showSlug, JsonElement page)
    {
        var items = new List<ChannelItemInfo>();
        JsonElement show = page.TryGetProperty("tvSeries", out JsonElement tvSeries) ? tvSeries : page;
        JsonElement seasons = page.TryGetProperty("seasons", out JsonElement pageSeasons)
            ? pageSeasons
            : GetArray(show, "seasons");
        if (seasons.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement season in seasons.EnumerateArray().OrderBy(item => GetInt32(item, "seasonNumber")))
        {
            string? slug = GetString(season, "slug");
            string? name = GetString(season, "name");
            if (!string.IsNullOrWhiteSpace(slug) && !string.IsNullOrWhiteSpace(name))
            {
                items.Add(CreateFolder(
                    "season:" + EncodeId(showSlug) + ":" + EncodeId(slug),
                    name,
                    GetImage(show, "w768"),
                    GetString(show, "description")));
            }
        }

        return items;
    }

    private async Task<List<ChannelItemInfo>> ParseVideosAsync(JsonElement rows, CancellationToken cancellationToken)
    {
        var items = new List<ChannelItemInfo>();
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (await ParseVideoAsync(row, cancellationToken).ConfigureAwait(false) is ChannelItemInfo item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private async Task<ChannelItemInfo?> ParseVideoAsync(JsonElement row, CancellationToken cancellationToken)
    {
        if (!row.TryGetProperty("video", out JsonElement video))
        {
            return null;
        }

        string? reference = GetString(video, "brightcoveId") ?? GetString(video, "referenceId");
        string? name = GetString(row, "name");
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string? overview = GetString(row, "description");
        bool isDrm = GetBoolean(video, "drm") || GetBoolean(row, "drm");
        string? imageUrl = GetImage(row, "w768");
        if (isDrm)
        {
            overview = string.IsNullOrWhiteSpace(overview)
                ? "This episode is marked as DRM-protected by 9Now and may not play in Jellyfin."
                : overview + Environment.NewLine + Environment.NewLine + "This episode is marked as DRM-protected by 9Now and may not play in Jellyfin.";
            if (Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri))
            {
                imageUrl = await _drmThumbnailRenderer.RenderAsync(imageUri, cancellationToken).ConfigureAwait(false) ?? imageUrl;
            }
        }

        return new ChannelItemInfo
        {
            Id = (isDrm ? "drmvideo:" : "video:") + EncodeId(reference),
            Name = isDrm ? "[DRM] " + name : name,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Episode,
            ImageUrl = imageUrl,
            Overview = overview,
            PremiereDate = GetDate(row, "airDate"),
        };
    }

    private static List<ChannelItemInfo> ParseLiveChannels(JsonElement rows)
    {
        var items = new List<ChannelItemInfo>();
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement row in rows.EnumerateArray())
        {
            string? reference = GetString(row, "referenceId");
            string? name = GetString(row, "name");
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string? overview = null;
            if (row.TryGetProperty("listings", out JsonElement listings) && listings.ValueKind == JsonValueKind.Array && listings.GetArrayLength() > 0)
            {
                overview = GetString(listings[0], "name");
                string? episode = GetString(listings[0], "episodeTitle");
                if (!string.IsNullOrWhiteSpace(episode))
                {
                    overview = string.IsNullOrWhiteSpace(overview) ? episode : overview + " — " + episode;
                }
            }

            items.Add(new ChannelItemInfo
            {
                Id = "liveitem:" + EncodeId(reference),
                Name = name,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Movie,
                ImageUrl = GetImage(row, "w768"),
                Overview = overview,
            });
        }

        return items;
    }

    private static ChannelItemInfo CreateFolder(string id, string name, string? image = null, string? overview = null)
    {
        return new ChannelItemInfo
        {
            Id = id,
            Name = name,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
            ImageUrl = image,
            Overview = overview,
        };
    }

    private static JsonElement GetArray(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value : default;
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
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBoolean(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static int GetInt32(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
                ? result
                : 0;
    }

    private static DateTime? GetDate(JsonElement element, string property)
    {
        return DateTime.TryParse(GetString(element, property), out DateTime date) ? date : null;
    }

    private static string? GetImage(JsonElement row, string size)
    {
        return row.TryGetProperty("image", out JsonElement image)
            && image.TryGetProperty("sizes", out JsonElement sizes)
                ? GetString(sizes, size)
                : null;
    }

    private static string EncodeId(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeId(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not load 9Now featured items for the channel home page.")]
    private static partial void LogFeaturedHomeLoadFailed(ILogger logger, Exception exception);
}
