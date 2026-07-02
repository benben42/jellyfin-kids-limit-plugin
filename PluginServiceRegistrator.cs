using Jellyfin.Plugin.KidsLimit.Services;
using Jellyfin.Plugin.KidsLimit.State;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.KidsLimit;

/// <summary>
/// Registers the plugin's services (the state store and the hosted tracker). Uses the
/// modern <see cref="IPluginServiceRegistrator"/> rather than the deprecated entry point.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StateStore>();
        serviceCollection.AddHostedService<WatchTimeTracker>();
    }
}
