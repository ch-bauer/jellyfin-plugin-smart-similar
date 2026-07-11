using Jellyfin.Plugin.SmartSimilar.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SmartSimilar
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public override string Name => "Smart Similar";

        public override Guid Id => Guid.Parse("61b616fa-7ba8-4262-b2a9-fae29b015930");

        public override string Description =>
            "Replaces the native \"More Like This\" section with better similarity results " +
            "from a selectable provider, excluding items of the same collection.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "SmartSimilar",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
                }
            };
        }
    }
}
