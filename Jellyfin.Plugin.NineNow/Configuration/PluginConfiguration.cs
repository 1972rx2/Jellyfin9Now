using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NineNow.Configuration;

/// <summary>Stores the local Nine account session and 9Now region.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the access token issued by Nine.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the refresh token issued by Nine.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the access-token expiry as Unix time in seconds.</summary>
    public long TokenExpiresAtUnixSeconds { get; set; }

    /// <summary>Gets or sets the Australian region used for local live channels.</summary>
    public string Region { get; set; } = "nsw";
}
