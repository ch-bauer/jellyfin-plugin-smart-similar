using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SmartSimilar.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Similarity provider: "Local", "Tmdb" or "Hybrid".</summary>
        public string Provider { get; set; } = "Local";

        /// <summary>TMDb API key (v3) or read access token (v4). Only needed for Tmdb/Hybrid.</summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>Maximum number of similar items to show.</summary>
        public int ResultLimit { get; set; } = 16;

        /// <summary>Hide items that share a collection with the current item.</summary>
        public bool ExcludeCollectionItems { get; set; } = true;

        /// <summary>Hide items already marked as played.</summary>
        public bool ExcludeWatched { get; set; }

        /// <summary>Minimum match score (0-100) for the local scoring provider.</summary>
        public int MinScore { get; set; } = 15;
    }
}
