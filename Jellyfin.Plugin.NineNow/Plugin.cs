using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.NineNow.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.NineNow;

/// <summary>Registers the 9Now channel and its dashboard settings.</summary>
public sealed class NineNowPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="NineNowPlugin"/> class.</summary>
    public NineNowPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "9Now";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a82ad2b4-48d8-4a74-8d92-4ba3f19e72d1");

    /// <summary>Gets the running plugin instance.</summary>
    public static NineNowPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        ];
    }
}
