using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSimilar.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSimilar.Tests
{
    public class PeopleCacheServiceTests
    {
        [Fact]
        public void GetOrLoad_SplitsPeopleByRole()
        {
            Movie movie = TestData.Movie("M");
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(l => l.GetPeople(It.IsAny<BaseItem>())).Returns(new List<PersonInfo>
            {
                new PersonInfo { Name = "Dir", Type = PersonKind.Director },
                new PersonInfo { Name = "Wri", Type = PersonKind.Writer },
                new PersonInfo { Name = "Act1", Type = PersonKind.Actor },
                new PersonInfo { Name = "Act2", Type = PersonKind.Actor }
            });

            using var cache = new PeopleCacheService(libraryManager.Object, NullLogger<PeopleCacheService>.Instance);
            PeopleCacheService.PeopleEntry entry = cache.GetOrLoad(movie);

            Assert.Equal(new[] { "Dir" }, entry.Directors);
            Assert.Equal(new[] { "Wri" }, entry.Writers);
            Assert.Equal(new[] { "Act1", "Act2" }, entry.TopActors);
        }

        [Fact]
        public void GetOrLoad_SecondCall_DoesNotQueryAgain()
        {
            Movie movie = TestData.Movie("M");
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(l => l.GetPeople(It.IsAny<BaseItem>())).Returns(new List<PersonInfo>());

            using var cache = new PeopleCacheService(libraryManager.Object, NullLogger<PeopleCacheService>.Instance);
            cache.GetOrLoad(movie);
            cache.GetOrLoad(movie);

            libraryManager.Verify(l => l.GetPeople(It.IsAny<BaseItem>()), Times.Once);
        }

        [Fact]
        public void ItemUpdated_DropsTheEntry()
        {
            Movie movie = TestData.Movie("M");
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(l => l.GetPeople(It.IsAny<BaseItem>())).Returns(new List<PersonInfo>());

            using var cache = new PeopleCacheService(libraryManager.Object, NullLogger<PeopleCacheService>.Instance);
            cache.GetOrLoad(movie);

            libraryManager.Raise(l => l.ItemUpdated += null!,
                libraryManager.Object, new ItemChangeEventArgs { Item = movie });

            cache.GetOrLoad(movie);

            libraryManager.Verify(l => l.GetPeople(It.IsAny<BaseItem>()), Times.Exactly(2));
        }

        [Fact]
        public void GetOrLoad_CapsTopActorsAtFive()
        {
            Movie movie = TestData.Movie("M");
            var people = Enumerable.Range(1, 9)
                .Select(i => new PersonInfo { Name = "Act" + i, Type = PersonKind.Actor })
                .ToList();
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(l => l.GetPeople(It.IsAny<BaseItem>())).Returns(people);

            using var cache = new PeopleCacheService(libraryManager.Object, NullLogger<PeopleCacheService>.Instance);
            PeopleCacheService.PeopleEntry entry = cache.GetOrLoad(movie);

            Assert.Equal(5, entry.TopActors.Count);
            Assert.Equal("Act1", entry.TopActors[0]);
        }
    }
}
