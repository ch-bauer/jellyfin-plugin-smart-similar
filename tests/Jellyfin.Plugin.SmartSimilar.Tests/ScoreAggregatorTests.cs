using Jellyfin.Plugin.SmartSimilar.Model;
using Jellyfin.Plugin.SmartSimilar.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSimilar.Tests
{
    public class ScoreAggregatorTests
    {
        [Fact]
        public void Merge_RewardsMatchingEverySeedOverMatchingOneWell()
        {
            Guid everySeed = Guid.NewGuid();
            Guid oneSeedOnly = Guid.NewGuid();

            var seeds = new[]
            {
                Seed(0, (everySeed, 60), (oneSeedOnly, 95)),
                Seed(1, (everySeed, 60), (oneSeedOnly, 5))
            };

            IReadOnlyList<ScoredItemDto> merged = ScoreAggregator.Merge(
                seeds, seedCount: 2, excluded: new HashSet<Guid>(), minScore: 0, limit: 10);

            Assert.Equal(everySeed, merged[0].Id);
            Assert.Equal(60, merged[0].Score);
            Assert.Equal(50, merged[1].Score);
        }

        [Fact]
        public void Merge_OtherKindSeed_IsNullRatherThanZero()
        {
            // A film must not be dragged down because a series was also picked.
            Guid film = Guid.NewGuid();

            var seeds = new[]
            {
                Seed(0, (film, 40)),
                new ScoreAggregator.SeedScores(1, "Series", Array.Empty<ScoredCandidate>())
            };

            IReadOnlyList<ScoredItemDto> merged = ScoreAggregator.Merge(
                seeds, seedCount: 2, excluded: new HashSet<Guid>(), minScore: 0, limit: 10);

            Assert.Equal(40, merged[0].Score);
            Assert.Equal(new double?[] { 40, null }, merged[0].PerSeed);
            Assert.Equal("Movie", merged[0].Kind);
        }

        [Fact]
        public void Merge_FloorAppliesToTheMeanNotTheParts()
        {
            Guid steady = Guid.NewGuid();
            var seeds = new[] { Seed(0, (steady, 30)), Seed(1, (steady, 10)) };

            Assert.Empty(ScoreAggregator.Merge(seeds, 2, new HashSet<Guid>(), minScore: 25, limit: 10));
            Assert.Single(ScoreAggregator.Merge(seeds, 2, new HashSet<Guid>(), minScore: 20, limit: 10));
        }

        [Fact]
        public void Merge_SeedsAreNeverReturnedAsResults()
        {
            Guid seedId = Guid.NewGuid();
            Guid other = Guid.NewGuid();

            var seeds = new[] { Seed(0, (seedId, 90), (other, 20)) };

            IReadOnlyList<ScoredItemDto> merged = ScoreAggregator.Merge(
                seeds, 1, new HashSet<Guid> { seedId }, minScore: 0, limit: 10);

            Assert.Single(merged);
            Assert.Equal(other, merged[0].Id);
        }

        [Fact]
        public void Merge_SharedSignals_AreUnionedAndTheYearGapIsTheClosest()
        {
            Guid candidate = Guid.NewGuid();

            var seeds = new[]
            {
                new ScoreAggregator.SeedScores(0, "Movie", new[]
                {
                    new ScoredCandidate(candidate, 50, new SharedSignals(
                        new[] { "Crime" }, Array.Empty<string>(), new[] { "Michael Mann" },
                        Array.Empty<string>(), 12, false))
                }),
                new ScoreAggregator.SeedScores(1, "Movie", new[]
                {
                    new ScoredCandidate(candidate, 50, new SharedSignals(
                        new[] { "crime", "Thriller" }, Array.Empty<string>(), Array.Empty<string>(),
                        Array.Empty<string>(), 3, true))
                })
            };

            SharedSignalsDto shared = ScoreAggregator.Merge(seeds, 2, new HashSet<Guid>(), 0, 10)[0].Shared;

            Assert.Equal(new[] { "Crime", "Thriller" }, shared.Genres);
            Assert.Equal(new[] { "Michael Mann" }, shared.People);
            Assert.Equal(3, shared.YearGap);
            Assert.True(shared.OfficialRating);
        }

        [Fact]
        public void Merge_Limit_KeepsTheBest()
        {
            var seeds = new[]
            {
                Seed(0, (Guid.NewGuid(), 10), (Guid.NewGuid(), 90), (Guid.NewGuid(), 50))
            };

            IReadOnlyList<ScoredItemDto> merged = ScoreAggregator.Merge(
                seeds, 1, new HashSet<Guid>(), 0, limit: 2);

            Assert.Equal(2, merged.Count);
            Assert.Equal(90, merged[0].Score);
            Assert.Equal(50, merged[1].Score);
        }

        [Fact]
        public void GetScoredDetailed_NamesWhatTheTwoShare()
        {
            Movie anchor = TestData.Movie("Heat",
                genres: new[] { "Crime", "Thriller" }, studios: new[] { "Warner" },
                year: 1995, officialRating: "FSK-16");
            Movie other = TestData.Movie("Collateral",
                genres: new[] { "Crime" }, studios: new[] { "Warner" },
                year: 2004, officialRating: "FSK-16");

            ScoredCandidate scored = CreateProvider(anchor, other)
                .GetScoredDetailed(anchor, Guid.Empty, 0).Single();

            Assert.Equal(other.Id, scored.Id);
            Assert.Equal(new[] { "Crime" }, scored.Shared.Genres);
            Assert.Equal(new[] { "Warner" }, scored.Shared.Studios);
            Assert.Equal(9, scored.Shared.YearGap);
            Assert.True(scored.Shared.OfficialRating);
        }

        [Fact]
        public void GetScoredDetailed_HonoursItsOwnFloorAndRanksBestFirst()
        {
            Movie anchor = TestData.Movie("Anchor", genres: new[] { "Horror" });
            Movie close = TestData.Movie("Close", genres: new[] { "Horror" });
            Movie far = TestData.Movie("Far", genres: new[] { "Comedy" });

            LocalScoringProvider provider = CreateProvider(anchor, close, far);

            Assert.Equal(close.Id, provider.GetScoredDetailed(anchor, Guid.Empty, 0)[0].Id);
            Assert.Equal(close.Id, provider.GetScoredDetailed(anchor, Guid.Empty, 30).Single().Id);
            Assert.Empty(provider.GetScoredDetailed(anchor, Guid.Empty, 99));
        }

        private static LocalScoringProvider CreateProvider(params Movie[] movies)
        {
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(movies.Cast<BaseItem>().ToList());
            libraryManager.Setup(l => l.GetPeople(It.IsAny<BaseItem>())).Returns(new List<PersonInfo>());

            return new LocalScoringProvider(
                libraryManager.Object,
                Mock.Of<IUserManager>(),
                new PeopleCacheService(libraryManager.Object, NullLogger<PeopleCacheService>.Instance),
                NullLogger<LocalScoringProvider>.Instance);
        }

        private static ScoreAggregator.SeedScores Seed(int index, params (Guid Id, double Score)[] scores)
        {
            return new ScoreAggregator.SeedScores(index, "Movie", scores
                .Select(s => new ScoredCandidate(s.Id, s.Score, SharedSignals.None))
                .ToList());
        }
    }
}
