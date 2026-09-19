using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.VidKing
{
    /// <summary>
    /// Wires the plugin's parts into the server's DI container.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<IItemResolver, VKingResolver>();

            // One instance, reached two ways: the controller asks it for an item's current
            // URL, and the host starts it so it can watch for configuration changes.
            serviceCollection.AddSingleton<VKingUrlSync>();
            serviceCollection.AddHostedService(provider => provider.GetRequiredService<VKingUrlSync>());

            // Playwright-based stream extractor (Option B). Called by the resolver at item
            // resolve time; result cached per id for the process lifetime.
            serviceCollection.AddSingleton<VKingStreamExtractor>();
        }
    }
}
