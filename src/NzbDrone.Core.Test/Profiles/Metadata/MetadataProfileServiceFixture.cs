using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles.Metadata
{
    [TestFixture]

    public class MetadataProfileServiceFixture : CoreTest<MetadataProfileService>
    {
        [Test]
        public void init_should_add_default_profiles()
        {
            Subject.Handle(new ApplicationStartedEvent());

            Mocker.GetMock<IMetadataProfileRepository>()
                  .Verify(v => v.Insert(It.IsAny<MetadataProfile>()), Times.Exactly(2));
        }

        [Test]

        //This confirms that new profiles are added only if no other profiles exists.
        //We don't want to keep adding them back if a user deleted them on purpose.
        public void Init_should_skip_if_any_profiles_already_exist()
        {
            Mocker.GetMock<IMetadataProfileRepository>()
                  .Setup(s => s.All())
                  .Returns(Builder<MetadataProfile>.CreateListOfSize(2).Build().ToList());

            Subject.Handle(new ApplicationStartedEvent());

            Mocker.GetMock<IMetadataProfileRepository>()
                  .Verify(v => v.Insert(It.Is<MetadataProfile>(x => x.Name == "Standard")), Times.Never());
        }

        [Test]
        public void init_should_add_none_profile_if_it_doesnt_exist()
        {
            Mocker.GetMock<IMetadataProfileRepository>()
                  .Setup(s => s.All())
                  .Returns(Builder<MetadataProfile>.CreateListOfSize(2).Build().ToList());

            Subject.Handle(new ApplicationStartedEvent());

            Mocker.GetMock<IMetadataProfileRepository>()
                  .Verify(v => v.Insert(It.Is<MetadataProfile>(x => x.Name == "None")), Times.Once());
        }

        [Test]
        public void init_should_move_existing_none_profile()
        {
            var profiles = Builder<MetadataProfile>.CreateListOfSize(2)
                .TheFirst(1)
                .With(x => x.Name = MetadataProfileService.NONE_PROFILE_NAME)
                .BuildList();

            Mocker.GetMock<IMetadataProfileRepository>()
                .Setup(s => s.All())
                .Returns(profiles);

            Subject.Handle(new ApplicationStartedEvent());

            Mocker.GetMock<IMetadataProfileRepository>()
                .Verify(v => v.Update(It.Is<MetadataProfile>(x => x.Id == profiles.First().Id && x.Name == "None.1")), Times.Once());

            Mocker.GetMock<IMetadataProfileRepository>()
                  .Verify(v => v.Insert(It.Is<MetadataProfile>(x => x.Name == "None")), Times.Once());
        }

        [Test]
        public void should_not_be_able_to_edit_none_profile()
        {
            var profile = Builder<MetadataProfile>.CreateNew()
                .With(p => p.Name = MetadataProfileService.NONE_PROFILE_NAME)
                .Build();

            Assert.Throws<InvalidOperationException>(() => Subject.Update(profile));
        }

        [Test]
        public void should_not_be_able_to_delete_none_profile()
        {
            var profile = Builder<MetadataProfile>.CreateNew()
                .With(p => p.Name = MetadataProfileService.NONE_PROFILE_NAME)
                .Build();

            Mocker.GetMock<IMetadataProfileRepository>().Setup(c => c.Get(profile.Id)).Returns(profile);

            Assert.Throws<MetadataProfileInUseException>(() => Subject.Delete(profile.Id));
        }

        [Test]
        public void should_not_be_able_to_delete_profile_if_assigned_to_author()
        {
            var profile = Builder<MetadataProfile>.CreateNew()
                .With(p => p.Id = 2)
                .Build();

            var authorList = Builder<Author>.CreateListOfSize(3)
                                            .Random(1)
                                            .With(c => c.MetadataProfileId = profile.Id)
                                            .Build().ToList();

            var importLists = Builder<ImportListDefinition>.CreateListOfSize(2)
                .All()
                .With(c => c.MetadataProfileId = 1)
                .Build().ToList();

            var rootFolders = Builder<RootFolder>.CreateListOfSize(2)
                .All()
                .With(f => f.DefaultMetadataProfileId = 1)
                .BuildList();

            Mocker.GetMock<IAuthorService>().Setup(c => c.GetAllAuthors()).Returns(authorList);
            Mocker.GetMock<IImportListFactory>().Setup(c => c.All()).Returns(importLists);
            Mocker.GetMock<IRootFolderService>().Setup(c => c.All()).Returns(rootFolders);
            Mocker.GetMock<IMetadataProfileRepository>().Setup(c => c.Get(profile.Id)).Returns(profile);

            Assert.Throws<MetadataProfileInUseException>(() => Subject.Delete(profile.Id));

            Mocker.GetMock<IMetadataProfileRepository>().Verify(c => c.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_not_be_able_to_delete_profile_if_assigned_to_import_list()
        {
            var profile = Builder<MetadataProfile>.CreateNew()
                .With(p => p.Id = 2)
                .Build();

            var authorList = Builder<Author>.CreateListOfSize(3)
                .All()
                .With(c => c.MetadataProfileId = 1)
                .Build().ToList();

            var importLists = Builder<ImportListDefinition>.CreateListOfSize(2)
                .Random(1)
                .With(c => c.MetadataProfileId = profile.Id)
                .Build().ToList();

            var rootFolders = Builder<RootFolder>.CreateListOfSize(2)
                .All()
                .With(f => f.DefaultMetadataProfileId = 1)
                .BuildList();

            Mocker.GetMock<IAuthorService>().Setup(c => c.GetAllAuthors()).Returns(authorList);
            Mocker.GetMock<IImportListFactory>().Setup(c => c.All()).Returns(importLists);
            Mocker.GetMock<IRootFolderService>().Setup(c => c.All()).Returns(rootFolders);
            Mocker.GetMock<IMetadataProfileRepository>().Setup(c => c.Get(profile.Id)).Returns(profile);

            Assert.Throws<MetadataProfileInUseException>(() => Subject.Delete(profile.Id));

            Mocker.GetMock<IMetadataProfileRepository>().Verify(c => c.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_not_be_able_to_delete_profile_if_assigned_to_root_folder()
        {
            var profile = Builder<MetadataProfile>.CreateNew()
                .With(p => p.Id = 2)
                .Build();

            var authorList = Builder<Author>.CreateListOfSize(3)
                .All()
                .With(c => c.MetadataProfileId = 1)
                .Build().ToList();

            var importLists = Builder<ImportListDefinition>.CreateListOfSize(2)
                .All()
                .With(c => c.MetadataProfileId = 1)
                .Build().ToList();

            var rootFolders = Builder<RootFolder>.CreateListOfSize(2)
                .Random(1)
                .With(f => f.DefaultMetadataProfileId = profile.Id)
                .BuildList();

            Mocker.GetMock<IAuthorService>().Setup(c => c.GetAllAuthors()).Returns(authorList);
            Mocker.GetMock<IImportListFactory>().Setup(c => c.All()).Returns(importLists);
            Mocker.GetMock<IRootFolderService>().Setup(c => c.All()).Returns(rootFolders);
            Mocker.GetMock<IMetadataProfileRepository>().Setup(c => c.Get(profile.Id)).Returns(profile);

            Assert.Throws<MetadataProfileInUseException>(() => Subject.Delete(profile.Id));

            Mocker.GetMock<IMetadataProfileRepository>().Verify(c => c.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_delete_profile_if_not_assigned_to_author_import_list_or_root_folder()
        {
            var profile = Builder<MetadataProfile>.CreateNew()
                .With(p => p.Id = 1)
                .Build();

            var authorList = Builder<Author>.CreateListOfSize(3)
                                            .All()
                                            .With(c => c.MetadataProfileId = 2)
                                            .Build().ToList();

            var importLists = Builder<ImportListDefinition>.CreateListOfSize(2)
                .All()
                .With(c => c.MetadataProfileId = 2)
                .Build().ToList();

            var rootFolders = Builder<RootFolder>.CreateListOfSize(2)
                .All()
                .With(f => f.DefaultMetadataProfileId = 2)
                .BuildList();

            Mocker.GetMock<IAuthorService>().Setup(c => c.GetAllAuthors()).Returns(authorList);
            Mocker.GetMock<IImportListFactory>().Setup(c => c.All()).Returns(importLists);
            Mocker.GetMock<IRootFolderService>().Setup(c => c.All()).Returns(rootFolders);
            Mocker.GetMock<IMetadataProfileRepository>().Setup(c => c.Get(profile.Id)).Returns(profile);

            Subject.Delete(1);

            Mocker.GetMock<IMetadataProfileRepository>().Verify(c => c.Delete(1), Times.Once());
        }

        private List<Book> FilterWithMinPopularity(double minPopularity, params Book[] books)
        {
            var profile = new MetadataProfile { Id = 1, MinPopularity = minPopularity };

            Mocker.GetMock<IMetadataProfileRepository>().Setup(c => c.Get(1)).Returns(profile);
            Mocker.GetMock<IMediaFileService>().Setup(c => c.GetFilesByAuthor(It.IsAny<int>())).Returns(new List<BookFile>());

            var author = new Author
            {
                Metadata = new AuthorMetadata { ForeignAuthorId = "OL1A" },
                Books = books.ToList(),
                Series = new List<Series>()
            };

            return Subject.FilterBooks(author, 1);
        }

        private static Book BookWith(string foreignId, int votes, decimal rating, DateTime? releaseDate = null)
        {
            return new Book
            {
                ForeignBookId = foreignId,
                Title = foreignId,
                ReleaseDate = releaseDate ?? new DateTime(2000, 1, 1),
                Ratings = new Ratings { Votes = votes, Value = rating },
                Editions = new List<Edition> { new Edition { ForeignEditionId = foreignId + "-e", Monitored = true } }
            };
        }

        [Test]
        public void open_library_books_only_need_one_rating_to_pass_a_positive_threshold()
        {
            // 55 votes × 4.0 = 220 popularity: below the Goodreads-calibrated default of 350.
            var rated = BookWith("OL893516W", 55, 4.0m);
            var unrated = BookWith("OL2W", 0, 0m);

            FilterWithMinPopularity(350, rated, unrated).Should().BeEquivalentTo(new[] { rated });
        }

        [Test]
        public void goodreads_books_still_use_the_popularity_threshold()
        {
            var popular = BookWith("3634639", 1000, 4.0m);
            var niche = BookWith("123", 55, 4.0m);

            FilterWithMinPopularity(350, popular, niche).Should().BeEquivalentTo(new[] { popular });
        }

        [Test]
        public void zero_threshold_keeps_unrated_open_library_books()
        {
            var unrated = BookWith("OL2W", 0, 0m);

            FilterWithMinPopularity(0, unrated).Should().BeEquivalentTo(new[] { unrated });
        }

        [Test]
        public void unreleased_open_library_books_are_kept_without_ratings()
        {
            var upcoming = BookWith("OL3W", 0, 0m, DateTime.UtcNow.AddMonths(6));

            FilterWithMinPopularity(350, upcoming).Should().BeEquivalentTo(new[] { upcoming });
        }
    }
}
