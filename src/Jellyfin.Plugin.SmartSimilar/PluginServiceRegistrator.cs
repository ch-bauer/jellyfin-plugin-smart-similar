using Jellyfin.Plugin.SmartSimilar.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SmartSimilar
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<CollectionLookupService>();
            serviceCollection.AddSingleton<TmdbIdLookupService>();
            serviceCollection.AddSingleton<PeopleCacheService>();
            serviceCollection.AddSingleton<LocalScoringProvider>();
            serviceCollection.AddSingleton<TmdbRecommendationsProvider>();
            serviceCollection.AddSingleton<SimilarItemsService>();
        }
    }
}
