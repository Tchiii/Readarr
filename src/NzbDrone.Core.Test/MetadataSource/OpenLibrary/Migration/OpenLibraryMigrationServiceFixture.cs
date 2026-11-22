using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource.OpenLibrary.Migration;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary.Migration
{
    [TestFixture]
    public class OpenLibraryMigrationServiceFixture : CoreTest<OpenLibraryMigrationService>
    {
        private Author _goodreadsAuthor;
        private Book _book;
        private Edition _edition;

        [SetUp]
        public void Setup()
        {
            _goodreadsAuthor = new Author { Id = 1, AuthorMetadataId = 10, Metadata = new AuthorMetadata { Id = 10, ForeignAuthorId = "6540057", TitleSlug = "6540057", Name = "Andy Weir" } };
            var openLibraryAuthor = new Author { Id = 2, Metadata = new AuthorMetadata { ForeignAuthorId = "OL7421324A", Name = "Tara Westover" } };

            _book = new Book { Id = 5, Title = "The Martian", ForeignBookId = "18007564", TitleSlug = "18007564" };
            _edition = new Edition { Id = 7, BookId = 5, ForeignEditionId = "18007564", TitleSlug = "18007564", Monitored = true };

            Mocker.GetMock<IAuthorService>().Setup(x => x.GetAllAuthors()).Returns(new List<Author> { _goodreadsAuthor, openLibraryAuthor });
            Mocker.GetMock<IBookService>().Setup(x => x.GetBooksByAuthor(1)).Returns(new List<Book> { _book });
            Mocker.GetMock<IEditionService>().Setup(x => x.GetEditionsByBook(It.IsAny<IEnumerable<int>>())).Returns(new List<Edition> { _edition });
            Mocker.GetMock<IMediaFileService>().Setup(x => x.GetFilesByAuthor(1)).Returns(new List<BookFile>());

            Mocker.GetMock<IOpenLibraryMigrationPlanner>()
                .Setup(x => x.PlanAuthor(It.IsAny<LocalAuthor>()))
                .Returns(new AuthorMigration
                {
                    AuthorId = 1,
                    Name = "Andy Weir",
                    OldId = "6540057",
                    NewId = "OL7234434A",
                    Books = new List<BookMigration>
                    {
                        new BookMigration
                        {
                            BookId = 5,
                            OldId = "18007564",
                            NewId = "OL17091839W",
                            Editions = new List<EditionMigration> { new EditionMigration { EditionId = 7, OldId = "18007564", NewId = "OL32815550M" } }
                        }
                    }
                });
        }

        [Test]
        public void should_only_plan_goodreads_authors()
        {
            var plan = Subject.Run(false);

            plan.Authors.Should().ContainSingle().Which.AuthorId.Should().Be(1);
            Mocker.GetMock<IOpenLibraryMigrationPlanner>().Verify(x => x.PlanAuthor(It.Is<LocalAuthor>(l => l.Author.Id == 2)), Times.Never());
        }

        [Test]
        public void dry_run_should_not_change_anything()
        {
            Subject.Run(false);

            Mocker.GetMock<IEditionService>().Verify(x => x.UpdateMany(It.IsAny<List<Edition>>()), Times.Never());
            Mocker.GetMock<IBookService>().Verify(x => x.UpdateMany(It.IsAny<List<Book>>()), Times.Never());
            Mocker.GetMock<IAuthorMetadataRepository>().Verify(x => x.Update(It.IsAny<AuthorMetadata>()), Times.Never());
            Mocker.GetMock<IManageCommandQueue>().Verify(x => x.Push(It.IsAny<RefreshAuthorCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());
        }

        [Test]
        public void apply_should_rewrite_ids_in_place_and_queue_a_refresh()
        {
            var plan = Subject.Run(true);

            plan.Applied.Should().BeTrue();

            _edition.ForeignEditionId.Should().Be("OL32815550M");
            _edition.TitleSlug.Should().Be("OL32815550M");
            _book.ForeignBookId.Should().Be("OL17091839W");
            _book.TitleSlug.Should().Be("OL17091839W");
            _goodreadsAuthor.Metadata.Value.ForeignAuthorId.Should().Be("OL7234434A");
            _goodreadsAuthor.Metadata.Value.TitleSlug.Should().Be("OL7234434A");

            // Same rows (same Ids): files, history and settings stay attached.
            Mocker.GetMock<IEditionService>().Verify(x => x.UpdateMany(It.Is<List<Edition>>(l => l.Single().Id == 7)), Times.Once());
            Mocker.GetMock<IBookService>().Verify(x => x.UpdateMany(It.Is<List<Book>>(l => l.Single().Id == 5)), Times.Once());
            Mocker.GetMock<IAuthorMetadataRepository>().Verify(x => x.Update(It.Is<AuthorMetadata>(m => m.Id == 10)), Times.Once());
            Mocker.GetMock<IManageCommandQueue>().Verify(x => x.Push(It.Is<RefreshAuthorCommand>(c => c.AuthorId == 1), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());
        }

        [Test]
        public void apply_should_leave_unmatched_authors_alone()
        {
            Mocker.GetMock<IOpenLibraryMigrationPlanner>()
                .Setup(x => x.PlanAuthor(It.IsAny<LocalAuthor>()))
                .Returns(new AuthorMigration { AuthorId = 1, Problem = "not found" });

            Subject.Run(true);

            Mocker.GetMock<IAuthorMetadataRepository>().Verify(x => x.Update(It.IsAny<AuthorMetadata>()), Times.Never());
            _goodreadsAuthor.ForeignAuthorId.Should().Be("6540057");
        }

        [Test]
        public void open_library_errors_should_skip_the_author_not_abort_the_run()
        {
            Mocker.GetMock<IOpenLibraryMigrationPlanner>()
                .Setup(x => x.PlanAuthor(It.IsAny<LocalAuthor>()))
                .Throws(new InvalidOperationException("connection reset"));

            var plan = Subject.Run(true);

            plan.Authors.Single().Problem.Should().Contain("connection reset");
            Mocker.GetMock<IAuthorMetadataRepository>().Verify(x => x.Update(It.IsAny<AuthorMetadata>()), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_give_the_planner_the_editions_that_have_files()
        {
            Mocker.GetMock<IMediaFileService>().Setup(x => x.GetFilesByAuthor(1)).Returns(new List<BookFile> { new BookFile { EditionId = 7 } });

            Subject.Run(false);

            Mocker.GetMock<IOpenLibraryMigrationPlanner>().Verify(x => x.PlanAuthor(It.Is<LocalAuthor>(l => l.EditionIdsWithFiles.Contains(7) && l.EditionsOf(_book).Single().Id == 7)), Times.Once());
        }
    }
}
