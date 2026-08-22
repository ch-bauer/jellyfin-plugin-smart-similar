namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Turns TMDb's <b>order</b> into a number so it can share a scale with local
    /// scoring. TMDb's recommendations endpoint returns a ranking and no scores at
    /// all, so the position in the list is the only signal there is - these are the
    /// two rules that make it comparable with the local 0-100 budget.
    /// </summary>
    public static class TmdbBlend
    {
        /// <summary>Score given to TMDb's first recommendation.</summary>
        public const double TopScore = 100;

        /// <summary>Score given to its last one - still a recommendation, so well clear of zero.</summary>
        public const double BottomScore = 50;

        /// <summary>How far up the local ranking a TMDb recommendation is lifted in Hybrid.</summary>
        public const double HybridBonus = 60;

        /// <summary>Rank not present in TMDb's answer.</summary>
        public const int NotRecommended = -1;

        /// <summary>
        /// Maps a position in TMDb's list onto <see cref="TopScore"/>..<see cref="BottomScore"/>.
        /// Everything TMDb named is a recommendation, so the band is deliberately narrow:
        /// the twentieth suggestion is not four times worse than the first.
        /// </summary>
        /// <param name="rank">Zero-based position, or <see cref="NotRecommended"/>.</param>
        /// <param name="count">Length of TMDb's list.</param>
        /// <returns>The score, or 0 when not recommended.</returns>
        public static double RankScore(int rank, int count)
        {
            if (rank < 0 || count <= 0)
            {
                return 0;
            }

            if (count == 1)
            {
                return TopScore;
            }

            double position = Math.Clamp(rank, 0, count - 1) / (double)(count - 1);
            return TopScore - ((TopScore - BottomScore) * position);
        }

        /// <summary>
        /// Hybrid keeps the local score as the base and <b>lifts</b> what TMDb also names,
        /// rather than replacing one ranking with the other. A title both agree on ends up
        /// on top; a title only TMDb knows still beats a weak local match; and a title TMDb
        /// never saw keeps its local score instead of being pushed out of the answer.
        /// </summary>
        /// <param name="localScore">The local 0-100 score.</param>
        /// <param name="rank">Zero-based TMDb position, or <see cref="NotRecommended"/>.</param>
        /// <param name="count">Length of TMDb's list.</param>
        /// <returns>The blended score, capped at 100.</returns>
        public static double Hybrid(double localScore, int rank, int count)
        {
            if (rank < 0 || count <= 0)
            {
                return localScore;
            }

            double position = count == 1 ? 0 : Math.Clamp(rank, 0, count - 1) / (double)(count - 1);
            return Math.Min(100, localScore + (HybridBonus * (1 - position)));
        }
    }
}
