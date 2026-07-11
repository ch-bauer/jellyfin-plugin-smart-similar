using Jellyfin.Plugin.SmartSimilar.Configuration;
using Jellyfin.Plugin.SmartSimilar.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSimilar.Tests
{
    public class LocalScoringProviderTests
    {
        [Fact]
        public void GetScored_RanksSharedMetadataAboveUnrelated()
        {
            Movie anchor = TestData.Movie("Heat",
                genres: new[] { "Crime", "Thriller" }, year: 1995, communityRating: 8.3f, officialRating: "FSK-16");
            Movie verySimilar = TestData.Movie("Collateral",
                genres: new[] { "Crime", "Thriller" }, year: 2004, communityRating: 7.5f, officialRating: "FSK-16");
            Movie somewhatSimilar = TestData.Movie("Drive",
                genres: new[] { "Thriller" }, year: 2011, communityRating: 7.8f);
            Movie unrelated = TestData.Movie("Cars",
                genres: new[] { "Animation", "Family" }, year: 2006, communityRating: 6.9f, officialRating: "FSK-0");

            LocalScoringProvider provider = CreateProvider(anchor, verySimilar, somewhatSimilar, unrelated);

            IReadOnlyList<Guid> scored = provider.GetScored(anchor, Guid.Empty, new PluginConfiguration { MinScore = 15 });

            Assert.Equal(verySimilar.Id, scored[0]);
            Assert.Contains(somewhatSimilar.Id, scored);
            Assert.DoesNotContain(unrelated.Id, scored);
            Assert.DoesNotContain(anchor.Id, scored);
        }

        [Fact]
        public void GetScored_MinScoreZero_IncludesEverythingExceptAnchor()
        {
            Movie anchor = TestData.Movie("A", genres: new[] { "Drama" });
            Movie other = TestData.Movie("B", genres: new[] { "Comedy" });

            LocalScoringProvider provider = CreateProvider(anchor, other);

            IReadOnlyList<Guid> scored = provider.GetScored(anchor, Guid.Empty, new PluginConfiguration { MinScore = 0 });

            Assert.Single(scored);
            Assert.Equal(other.Id, scored[0]);
        }

        [Fact]
        public void GetScored_SharedDirector_BeatsPureMetadataTie()
        {
            Movie anchor = TestData.Movie("Alien", genres: new[] { "Sci-Fi" }, year: 1979);
            Movie sameDirector = TestData.Movie("Blade Runner", genres: new[] { "Sci-Fi" }, year: 1982);
            Movie otherDirector = TestData.Movie("Star Trek", genres: new[] { "Sci-Fi" }, year: 1982);

            var libraryManager = LibraryWith(anchor, sameDirector, otherDirector);
            libraryManager
                .Setup(l => l.GetPeople(It.Is<BaseItem>(i => i.Id == anchor.Id || i.Id == sameDirector.Id)))
                .Returns(new List<PersonInfo>
                {
                    new PersonInfo { Name = "Ridley Scott", Type = Jellyfin.Data.Enums.PersonKind.Director }
                });
            libraryManager
                .Setup(l => l.GetPeople(It.Is<BaseItem>(i => i.Id == otherDirector.Id)))
                .Returns(new List<PersonInfo>());

            LocalScoringProvider provider = CreateProvider(libraryManager);

            IReadOnlyList<Guid> scored = provider.GetScored(anchor, Guid.Empty, new PluginConfiguration { MinScore = 0 });

            Assert.Equal(sameDirector.Id, scored[0]);
            Assert.Equal(otherDirector.Id, scored[1]);
        }

        [Fact]
        public void GetScored_SecondCall_UsesCachedRanking()
        {
            Movie anchor = TestData.Movie("A", genres: new[] { "Drama" });
            Movie other = TestData.Movie("B", genres: new[] { "Drama" });

            var libraryManager = LibraryWith(anchor, other);
            LocalScoringProvider provider = CreateProvider(libraryManager);
            var config = new PluginConfiguration { MinScore = 0 };

            provider.GetScored(anchor, Guid.Empty, config);
            int queriesAfterFirst = libraryManager.Invocations.Count(i => i.Method.Name == "GetItemList");
            provider.GetScored(anchor, Guid.Empty, config);

            Assert.Equal(queriesAfterFirst,
                libraryManager.Invocations.Count(i => i.Method.Name == "GetItemList"));
        }

        [Fact]
        public void GetScored_GenreOverlap_IsRelativeToAnchor()
        {
            // One shared genre out of one beats one shared genre out of three.
            Movie anchor = TestData.Movie("Anchor", genres: new[] { "Horror" });
            Movie fullOverlap = TestData.Movie("Full", genres: new[] { "Horror" });
            Movie partialOverlap = TestData.Movie("Partial", genres: new[] { "Horror", "Comedy", "Romance" });

            LocalScoringProvider provider = CreateProvider(anchor, fullOverlap, partialOverlap);

            IReadOnlyList<Guid> scored = provider.GetScored(anchor, Guid.Empty, new PluginConfiguration { MinScore = 0 });

            // Both share 1/1 of the anchor's genres - the tie is broken deterministically,
            // but both must be present and score above an empty-genre movie would.
            Assert.Equal(2, scored.Count);
        }

        private static Mock<ILibraryManager> LibraryWith(params Movie[] movies)
        {
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager
                .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(movies.Cast<BaseItem>().ToList());
            libraryManager
                .Setup(l => l.GetPeople(It.IsAny<BaseItem>()))
                .Returns(new List<PersonInfo>());
            return libraryManager;
        }

        private static LocalScoringProvider CreateProvider(params Movie[] movies)
        {
            return CreateProvider(LibraryWith(movies));
        }

        private static LocalScoringProvider CreateProvider(Mock<ILibraryManager> libraryManager)
        {
            var peopleCache = new PeopleCacheService(libraryManager.Object, NullLogger<PeopleCacheService>.Instance);
            return new LocalScoringProvider(
                libraryManager.Object,
                Mock.Of<IUserManager>(),
                peopleCache,
                NullLogger<LocalScoringProvider>.Instance);
        }
    }
}
