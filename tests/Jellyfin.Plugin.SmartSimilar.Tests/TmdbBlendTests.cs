using Jellyfin.Plugin.SmartSimilar.Services;
using Xunit;

namespace Jellyfin.Plugin.SmartSimilar.Tests
{
    public class TmdbBlendTests
    {
        [Fact]
        public void RankScore_SpansTheBandFromFirstToLast()
        {
            Assert.Equal(TmdbBlend.TopScore, TmdbBlend.RankScore(0, 21));
            Assert.Equal(TmdbBlend.BottomScore, TmdbBlend.RankScore(20, 21));
            Assert.Equal(75, TmdbBlend.RankScore(10, 21));
        }

        [Fact]
        public void RankScore_SingleRecommendation_IsTheTop()
        {
            Assert.Equal(TmdbBlend.TopScore, TmdbBlend.RankScore(0, 1));
        }

        [Fact]
        public void RankScore_NotRecommended_IsZero()
        {
            Assert.Equal(0, TmdbBlend.RankScore(TmdbBlend.NotRecommended, 10));
            Assert.Equal(0, TmdbBlend.RankScore(0, 0));
        }

        [Fact]
        public void Hybrid_LiftsWhatTmdbNamesAndLeavesTheRestAlone()
        {
            // Both agree: local score plus the full bonus, capped at 100.
            Assert.Equal(100, TmdbBlend.Hybrid(70, rank: 0, count: 11));
            Assert.Equal(90, TmdbBlend.Hybrid(30, rank: 0, count: 11));

            // TMDb's last pick gets no lift at all, so it cannot outrank a strong local match.
            Assert.Equal(30, TmdbBlend.Hybrid(30, rank: 10, count: 11));

            // A title TMDb never saw keeps its local score rather than dropping out.
            Assert.Equal(42, TmdbBlend.Hybrid(42, TmdbBlend.NotRecommended, 11));
        }

        [Fact]
        public void Hybrid_AWeakLocalMatchTmdbNamesStillBeatsAMiddlingOneItDoesNot()
        {
            double namedByTmdb = TmdbBlend.Hybrid(10, rank: 1, count: 11);
            double localOnly = TmdbBlend.Hybrid(45, TmdbBlend.NotRecommended, 11);

            Assert.True(namedByTmdb > localOnly);
        }
    }
}
