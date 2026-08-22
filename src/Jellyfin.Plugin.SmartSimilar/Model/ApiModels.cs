namespace Jellyfin.Plugin.SmartSimilar.Model
{
    /// <summary>
    /// Response of GET SmartSimilar/Items. When <see cref="Active"/> is false the
    /// client leaves the native "More Like This" section untouched.
    /// </summary>
    public record SimilarItemsResponse(bool Active, IReadOnlyList<Guid> ItemIds);

    public record TmdbKeyTestRequest(string ApiKey);

    public record TmdbKeyTestResponse(bool Ok, string Message);

    /// <summary>One of the anchors a scoring request was given.</summary>
    /// <param name="Id">The anchor's item id.</param>
    /// <param name="Name">Its name, so a caller need not look it up again.</param>
    /// <param name="Kind">"Movie" or "Series" - candidates are only comparable within a kind.</param>
    /// <param name="Active">False when the id is unknown or its type is not handled.</param>
    /// <param name="Source">
    /// What actually answered for this seed: "Local", "Tmdb" or "Hybrid". It can differ
    /// from the configured provider - a seed with no TMDb id, or one TMDb knows nothing
    /// about in this library, falls back to local scoring and says so here.
    /// </param>
    /// <param name="TmdbMatches">How many of TMDb's recommendations exist in the library.</param>
    public record ScoreSeedDto(Guid Id, string Name, string Kind, bool Active, string Source, int TmdbMatches);

    /// <summary>What a candidate has in common with the anchors, merged across them.</summary>
    public record SharedSignalsDto(
        IReadOnlyList<string> Genres,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> People,
        IReadOnlyList<string> Studios,
        int? YearGap,
        bool OfficialRating);

    /// <summary>
    /// One scored candidate. <paramref name="Score"/> is the mean over the comparable
    /// seeds, which is what rewards a title matching every pick over one matching a
    /// single pick well; <paramref name="PerSeed"/> is aligned with the request's seed
    /// order, null where the seed is of the other kind and the two cannot be compared.
    /// </summary>
    public record ScoredItemDto(
        Guid Id,
        string Kind,
        double Score,
        IReadOnlyList<double?> PerSeed,
        SharedSignalsDto Shared);

    /// <summary>
    /// Response of GET SmartSimilar/Score. Active is false only when no seed could
    /// be scored at all; individual seeds carry their own Active flag.
    /// </summary>
    public record ScoreResponse(
        bool Active,
        IReadOnlyList<ScoreSeedDto> Seeds,
        IReadOnlyList<ScoredItemDto> Results);
}
