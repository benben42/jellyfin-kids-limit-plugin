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
        serviceCollection.AddSingleton<WalletStore>();
        serviceCollection.AddSingleton<LegacyBlockRestorer>();
        serviceCollection.AddSingleton<PlaybackTerminator>();
        serviceCollection.AddSingleton<NotificationService>();
        serviceCollection.AddSingleton<RewardsService>();
        serviceCollection.AddSingleton<StopMethodTester>();
        // Registered as a singleton *and* as the hosted service resolving to that same
        // instance: the parent "Stop now" endpoint calls into the tracker directly, and
        // AddHostedService<T> alone would only register it as IHostedService.
        serviceCollection.AddSingleton<WatchTimeTracker>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WatchTimeTracker>());
    }
}
