using System.Reflection;
using Jellyfin.Plugin.SmartSimilar.Model;

namespace Jellyfin.Plugin.SmartSimilar.Helpers
{
    /// <summary>
    /// Callbacks invoked by the File Transformation plugin. Method signatures
    /// (static, PatchRequestPayload in, string out) must not change.
    /// </summary>
    public static class TransformationPatches
    {
        public static string IndexHtml(PatchRequestPayload payload)
        {
            string contents = payload.Contents ?? string.Empty;

            if (!contents.Contains("</head>", StringComparison.OrdinalIgnoreCase))
            {
                return contents;
            }

            string js = ReadEmbeddedResource("Web.smartsimilar.js");
            string css = ReadEmbeddedResource("Web.smartsimilar.css");

            string injection =
                "\n<!-- SmartSimilar plugin -->\n" +
                "<style>\n" + css + "\n</style>\n" +
                "<script>\n" + js + "\n</script>\n";

            int index = contents.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            return contents.Insert(index, injection);
        }

        private static string ReadEmbeddedResource(string relativeName)
        {
            string resourceName = "Jellyfin.Plugin.SmartSimilar." + relativeName;
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
            }

            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
