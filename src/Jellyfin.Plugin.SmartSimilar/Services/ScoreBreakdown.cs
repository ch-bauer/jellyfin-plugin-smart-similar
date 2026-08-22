namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// The metadata a candidate actually has in common with one anchor. This is
    /// what a caller needs to say <em>why</em> something was suggested; the score
    /// alone cannot tell "same director" from "same decade".
    /// </summary>
    public sealed record SharedSignals(
        IReadOnlyList<string> Genres,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> People,
        IReadOnlyList<string> Studios,
        int? YearGap,
        bool OfficialRating)
    {
        public static SharedSignals None { get; } = new SharedSignals(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, false);
    }

    /// <summary>One candidate, its score against a single anchor, and what they share.</summary>
    public sealed record ScoredCandidate(Guid Id, double Score, SharedSignals Shared);
}
