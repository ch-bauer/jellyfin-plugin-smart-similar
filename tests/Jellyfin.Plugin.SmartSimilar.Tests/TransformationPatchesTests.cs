using Jellyfin.Plugin.SmartSimilar.Helpers;
using Jellyfin.Plugin.SmartSimilar.Model;
using Xunit;

namespace Jellyfin.Plugin.SmartSimilar.Tests
{
    public class TransformationPatchesTests
    {
        [Fact]
        public void IndexHtml_InjectsScriptAndStyleBeforeHead()
        {
            string html = "<html><head><title>t</title></head><body></body></html>";

            string result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

            Assert.Contains("<style>", result, StringComparison.Ordinal);
            Assert.Contains("<script>", result, StringComparison.Ordinal);
            Assert.Contains("SmartSimilar plugin", result, StringComparison.Ordinal);
            // The injection must land inside the head.
            Assert.True(result.IndexOf("<script>", StringComparison.Ordinal)
                        < result.IndexOf("</head>", StringComparison.Ordinal));
        }

        [Fact]
        public void IndexHtml_WithoutHead_ReturnsUnchanged()
        {
            string html = "<html><body>no head here</body></html>";

            string result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

            Assert.Equal(html, result);
        }

        [Fact]
        public void IndexHtml_NullContents_ReturnsEmpty()
        {
            string result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = null });

            Assert.Equal(string.Empty, result);
        }
    }
}
