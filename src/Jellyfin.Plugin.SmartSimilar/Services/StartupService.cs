using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Jellyfin.Plugin.SmartSimilar.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Registers the index.html transformation with the File Transformation plugin on server startup.
    /// </summary>
    public class StartupService : IScheduledTask
    {
        public string Name => "Smart Similar Startup";

        public string Key => "Jellyfin.Plugin.SmartSimilar.Startup";

        public string Description => "Registers the Smart Similar web injection with the File Transformation plugin.";

        public string Category => "Startup Services";

        private readonly ILogger<StartupService> m_logger;
        private readonly CollectionLookupService m_collectionLookup;
        private readonly TmdbIdLookupService m_tmdbIdLookup;
        private readonly PeopleCacheService m_peopleCache;
        private readonly LocalScoringProvider m_localProvider;
        private readonly IUserManager m_userManager;

        public StartupService(
            ILogger<StartupService> logger,
            CollectionLookupService collectionLookup,
            TmdbIdLookupService tmdbIdLookup,
            PeopleCacheService peopleCache,
            LocalScoringProvider localProvider,
            IUserManager userManager)
        {
            m_logger = logger;
            m_collectionLookup = collectionLookup;
            m_tmdbIdLookup = tmdbIdLookup;
            m_peopleCache = peopleCache;
            m_localProvider = localProvider;
            m_userManager = userManager;
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            m_logger.LogInformation("Smart Similar startup. Registering file transformation.");

            RegisterTransformation();

            // Pre-warm every cache a request needs, so the first detail page
            // after a restart is as fast as any other. The people warm-up is
            // the long part (one DB query per movie/series, parallelized) and
            // reports task progress.
            m_collectionLookup.Warm();
            m_tmdbIdLookup.Warm();

            try
            {
                m_peopleCache.Warm(progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                m_logger.LogWarning(ex, "Failed to warm the people cache.");
            }

            foreach (Guid warmUserId in m_userManager.GetUsersIds())
            {
                m_localProvider.WarmCandidates(warmUserId);
            }

            m_logger.LogInformation("Smart Similar caches warmed.");

            return Task.CompletedTask;
        }

        private void RegisterTransformation()
        {
            Assembly? fileTransformationAssembly =
                AssemblyLoadContext.All.SelectMany(x => x.Assemblies).FirstOrDefault(x =>
                    x.FullName?.Contains(".FileTransformation") ?? false);

            if (fileTransformationAssembly == null)
            {
                m_logger.LogWarning(
                    "File Transformation plugin was not found. The Smart Similar plugin requires it to modify the web client. " +
                    "Install it from https://www.iamparadox.dev/jellyfin/plugins/manifest.json and restart Jellyfin.");
                return;
            }

            Type? pluginInterfaceType =
                fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            MethodInfo? registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation");

            if (registerMethod == null || registerMethod.GetParameters().Length != 1)
            {
                m_logger.LogWarning("File Transformation plugin was found but PluginInterface.RegisterTransformation was not. " +
                                    "The installed File Transformation version may be incompatible.");
                return;
            }

            string payloadJson = JsonSerializer.Serialize(new
            {
                id = "6da39760-3911-475d-a5a8-7164665512cf",
                fileNamePattern = "index.html",
                callbackAssembly = GetType().Assembly.FullName,
                callbackClass = typeof(TransformationPatches).FullName,
                callbackMethod = nameof(TransformationPatches.IndexHtml)
            });

            // RegisterTransformation takes a Newtonsoft JObject. Building the payload with the
            // JObject type from the File Transformation plugin's own load context (instead of
            // referencing Newtonsoft ourselves) avoids any cross-AssemblyLoadContext type mismatch.
            Type payloadType = registerMethod.GetParameters()[0].ParameterType;
            MethodInfo? parseMethod = payloadType.GetMethod("Parse", new[] { typeof(string) });

            if (parseMethod == null)
            {
                m_logger.LogWarning("Could not find a Parse method on the transformation payload type '{PayloadType}'.",
                    payloadType.FullName);
                return;
            }

            object? payload = parseMethod.Invoke(null, new object?[] { payloadJson });
            registerMethod.Invoke(null, new[] { payload });

            m_logger.LogInformation("Smart Similar transformation registered.");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger
            };
        }
    }
}
