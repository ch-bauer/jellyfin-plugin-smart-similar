using Jellyfin.Plugin.SmartSimilar.Model;

namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Merges the per-anchor scores of a multi-anchor request into one ranking.
    /// Pure: it is handed the scores and knows nothing about the library.
    /// </summary>
    public static class ScoreAggregator
    {
        /// <summary>The scores one seed produced, and the kind of item it can be compared with.</summary>
        public sealed record SeedScores(int Index, string Kind, IReadOnlyList<ScoredCandidate> Scores);

        /// <summary>
        /// Ranks candidates by the <b>mean</b> of their scores over the seeds they can be
        /// compared with. The mean is the point of the endpoint: a title that matches every
        /// pick moderately beats one that matches a single pick well, which is what a caller
        /// building a channel out of several titles wants. Seeds of the other kind are not
        /// counted against a candidate - a film is never penalised for a series being picked.
        /// </summary>
        /// <param name="seeds">Per-seed scores, scored with no floor of their own.</param>
        /// <param name="seedCount">Total number of seeds in the request, including inactive ones.</param>
        /// <param name="excluded">Ids never to return - the seeds themselves.</param>
        /// <param name="minScore">Floor applied to the mean, not to the individual scores.</param>
        /// <param name="limit">Maximum number of results.</param>
        /// <returns>The ranking, best first.</returns>
        public static IReadOnlyList<ScoredItemDto> Merge(
            IReadOnlyList<SeedScores> seeds,
            int seedCount,
            IReadOnlySet<Guid> excluded,
            double minScore,
            int limit)
        {
            Dictionary<Guid, Accumulator> byCandidate = new Dictionary<Guid, Accumulator>();

            foreach (SeedScores seed in seeds)
            {
                foreach (ScoredCandidate candidate in seed.Scores)
                {
                    if (excluded.Contains(candidate.Id))
                    {
                        continue;
                    }

                    if (!byCandidate.TryGetValue(candidate.Id, out Accumulator? accumulator))
                    {
                        accumulator = new Accumulator(seedCount, seed.Kind);
                        byCandidate[candidate.Id] = accumulator;
                    }

                    accumulator.Add(seed.Index, candidate);
                }
            }

            List<ScoredItemDto> results = new List<ScoredItemDto>(byCandidate.Count);
            foreach (KeyValuePair<Guid, Accumulator> entry in byCandidate)
            {
                double mean = entry.Value.Mean();
                if (mean < minScore)
                {
                    continue;
                }

                results.Add(new ScoredItemDto(
                    entry.Key,
                    entry.Value.Kind,
                    Math.Round(mean, 2),
                    entry.Value.PerSeed,
                    entry.Value.Shared()));
            }

            // Ties are broken by id so the same library always ranks the same way.
            results.Sort(static (a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.Id.CompareTo(b.Id);
            });

            return limit > 0 && results.Count > limit ? results.GetRange(0, limit) : results;
        }

        private sealed class Accumulator
        {
            private readonly List<string> m_genres = new();
            private readonly List<string> m_tags = new();
            private readonly List<string> m_people = new();
            private readonly List<string> m_studios = new();
            private int? m_yearGap;
            private bool m_officialRating;
            private int m_scored;

            public Accumulator(int seedCount, string kind)
            {
                PerSeed = new double?[seedCount];
                Kind = kind;
            }

            public double?[] PerSeed { get; }

            public string Kind { get; }

            public void Add(int seedIndex, ScoredCandidate candidate)
            {
                PerSeed[seedIndex] = Math.Round(candidate.Score, 2);
                m_scored++;

                Union(m_genres, candidate.Shared.Genres);
                Union(m_tags, candidate.Shared.Tags);
                Union(m_people, candidate.Shared.People);
                Union(m_studios, candidate.Shared.Studios);

                if (candidate.Shared.YearGap.HasValue
                    && (!m_yearGap.HasValue || candidate.Shared.YearGap.Value < m_yearGap.Value))
                {
                    m_yearGap = candidate.Shared.YearGap;
                }

                m_officialRating |= candidate.Shared.OfficialRating;
            }

            public double Mean()
            {
                if (m_scored == 0)
                {
                    return 0;
                }

                double total = 0;
                foreach (double? score in PerSeed)
                {
                    total += score ?? 0;
                }

                return total / m_scored;
            }

            public SharedSignalsDto Shared()
            {
                return new SharedSignalsDto(m_genres, m_tags, m_people, m_studios, m_yearGap, m_officialRating);
            }

            private static void Union(List<string> into, IReadOnlyList<string> values)
            {
                foreach (string value in values)
                {
                    if (!into.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        into.Add(value);
                    }
                }
            }
        }
    }
}
