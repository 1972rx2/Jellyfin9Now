using Jellyfin.Plugin.NineNow.Channels;
using Jellyfin.Plugin.NineNow.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.NineNow;

/// <summary>Registers the channel provider with Jellyfin's service container.</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton<DrmThumbnailRenderer>();
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<IChannel, NineNowChannel>());
    }
}
