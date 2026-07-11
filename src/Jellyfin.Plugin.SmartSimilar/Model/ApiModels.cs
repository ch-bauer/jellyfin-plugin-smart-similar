namespace Jellyfin.Plugin.SmartSimilar.Model
{
    /// <summary>
    /// Response of GET SmartSimilar/Items. When <see cref="Active"/> is false the
    /// client leaves the native "More Like This" section untouched.
    /// </summary>
    public record SimilarItemsResponse(bool Active, IReadOnlyList<Guid> ItemIds);

    public record TmdbKeyTestRequest(string ApiKey);

    public record TmdbKeyTestResponse(bool Ok, string Message);
}
