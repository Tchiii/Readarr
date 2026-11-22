using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.MetadataSource.OpenLibrary.Migration;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary.Migration
{
    [TestFixture]
    public class OpenLibraryMigrationPlannerFixture : CoreTest<OpenLibraryMigrationPlanner>
    {
        private const string OlAuthor = "OL7234434A";

        private LocalAuthor _local;

        [SetUp]
        public void Setup()
        {
            _local = new LocalAuthor
            {
                Author = new Author
                {
                    Id = 1,
                    AuthorMetadataId = 10,
                    Metadata = new AuthorMetadata { ForeignAuthorId = "6540057", Name = "Andy Weir" }
                }
            };

            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.SearchWorks(It.IsAny<string>(), It.IsAny<int>()))
                .Returns(new List<OpenLibrarySearchDoc>());
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.SearchAuthors(It.IsAny<string>()))
                .Returns(new List<OpenLibraryAuthorSearchDoc>());
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.GetAuthorWorkDocs(It.IsAny<string>()))
                .Returns(new List<OpenLibrarySearchDoc>());
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.GetWorkEditions(It.IsAny<string>()))
                .Returns(new List<OpenLibraryEdition>());
        }

        private Book GivenBook(int id, string title, params Edition[] editions)
        {
            var book = new Book { Id = id, Title = title, ForeignBookId = (1000 + id).ToString() };
            _local.Books.Add(book);
            _local.EditionsByBook[id] = editions.ToList();
            editions.ToList().ForEach(e => e.BookId = id);
            return book;
        }

        private static Edition LocalEdition(int id, string isbn = null, bool monitored = true)
        {
            return new Edition { Id = id, Title = $"Edition {id}", Isbn13 = isbn, Monitored = monitored, ForeignEditionId = (5000 + id).ToString() };
        }

        private static OpenLibrarySearchDoc Work(string workId, string title, string authorId = OlAuthor, string authorName = "Andy Weir", int editions = 10)
        {
            return new OpenLibrarySearchDoc
            {
                Key = $"/works/{workId}",
                Title = title,
                AuthorKey = new List<string> { authorId },
                AuthorName = new List<string> { authorName },
                EditionCount = editions
            };
        }

        private static OpenLibraryEdition OlEdition(string editionId, string isbn = null, string language = "eng")
        {
            return new OpenLibraryEdition
            {
                Key = $"/books/{editionId}",
                Title = "Edition",
                Isbn13 = isbn == null ? null : new List<string> { isbn },
                Languages = new List<OpenLibraryKeyRef> { new OpenLibraryKeyRef { Key = $"/languages/{language}" } }
            };
        }

        private void GivenIsbnHit(string isbn, OpenLibrarySearchDoc doc)
        {
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.SearchWorks($"isbn:{isbn}", 1))
                .Returns(new List<OpenLibrarySearchDoc> { doc });
        }

        private void GivenAuthorWorks(params OpenLibrarySearchDoc[] works)
        {
            Mocker.GetMock<IOpenLibraryProxy>().Setup(x => x.GetAuthorWorkDocs(OlAuthor)).Returns(works.ToList());
        }

        private void GivenWorkEditions(string workId, params OpenLibraryEdition[] editions)
        {
            Mocker.GetMock<IOpenLibraryProxy>().Setup(x => x.GetWorkEditions(workId)).Returns(editions.ToList());
        }

        [Test]
        public void should_match_author_book_and_edition_by_isbn()
        {
            GivenBook(1, "The Martian", LocalEdition(11, "9780804139021"));
            var martian = Work("OL17091839W", "The Martian");
            GivenIsbnHit("9780804139021", martian);
            GivenAuthorWorks(martian);
            GivenWorkEditions("OL17091839W", OlEdition("OL1M", "9780000000001"), OlEdition("OL32815550M", "9780804139021"));

            var plan = Subject.PlanAuthor(_local);

            plan.NewId.Should().Be(OlAuthor);
            plan.Method.Should().Be(MatchMethod.IsbnVotes);

            var book = plan.Books.Single();
            book.NewId.Should().Be("OL17091839W");
            book.Method.Should().Be(MatchMethod.Isbn);
            book.Editions.Single().NewId.Should().Be("OL32815550M");
            book.Editions.Single().Method.Should().Be(MatchMethod.Isbn);
        }

        [Test]
        public void should_match_author_by_exact_name_and_books_by_title_without_isbn()
        {
            GivenBook(1, "Artemis: A Novel", LocalEdition(11));
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.SearchAuthors("Andy Weir"))
                .Returns(new List<OpenLibraryAuthorSearchDoc>
                {
                    new OpenLibraryAuthorSearchDoc { Key = "OL12970306A", Name = "Andy Weir bush", WorkCount = 1 },
                    new OpenLibraryAuthorSearchDoc { Key = "OL1A", Name = "Andy Weir", WorkCount = 2 },
                    new OpenLibraryAuthorSearchDoc { Key = OlAuthor, Name = "Weir, Andy", WorkCount = 13 }
                });
            GivenAuthorWorks(Work("OL17837968W", "Artemis", editions: 27), Work("OL9W", "Artemis", editions: 1));
            GivenWorkEditions("OL17837968W", OlEdition("OL26940537M", "9780525532101"));

            var plan = Subject.PlanAuthor(_local);

            plan.NewId.Should().Be(OlAuthor, "the exact name match with most works wins");
            plan.Method.Should().Be(MatchMethod.AuthorName);
            plan.Books.Single().NewId.Should().Be("OL17837968W", "the most published work with that title wins");
            plan.Books.Single().Method.Should().Be(MatchMethod.Title);
            plan.Books.Single().Editions.Single().Method.Should().Be(MatchMethod.DefaultEdition);
        }

        [Test]
        public void should_leave_everything_when_no_author_matches()
        {
            GivenBook(1, "The Martian", LocalEdition(11, "9780804139021"));

            var plan = Subject.PlanAuthor(_local);

            plan.NewId.Should().BeNull();
            plan.Problem.Should().NotBeNullOrWhiteSpace();
            plan.Books.Should().OnlyContain(b => b.NewId == null);
        }

        [Test]
        public void should_spread_same_title_books_over_same_title_works()
        {
            // Seen on real data: "La zone du dehors" and "La Zone Du Dehors (French Edition)" are two Open Library works.
            GivenBook(1, "La zone du dehors", LocalEdition(11));
            GivenBook(2, "La Zone Du Dehors (French Edition)", LocalEdition(21));
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.SearchAuthors("Andy Weir"))
                .Returns(new List<OpenLibraryAuthorSearchDoc> { new OpenLibraryAuthorSearchDoc { Key = OlAuthor, Name = "Andy Weir", WorkCount = 13 } });
            GivenAuthorWorks(Work("OL19960903W", "La Zone du Dehors", editions: 5), Work("OL19545733W", "La Zone Du Dehors (French Edition)", editions: 1));
            GivenWorkEditions("OL19960903W", OlEdition("OL1M"));
            GivenWorkEditions("OL19545733W", OlEdition("OL2M"));

            var books = Subject.PlanAuthor(_local).Books;

            books.Select(b => b.NewId).Should().BeEquivalentTo(new[] { "OL19960903W", "OL19545733W" });
        }

        [Test]
        public void should_ignore_isbn_hits_by_other_authors()
        {
            // A summary of the book shares nothing with the author but can share an ISBN-like hit.
            GivenBook(1, "The Martian", LocalEdition(11, "9780804139021"));
            GivenIsbnHit("9780804139021", Work("OL99W", "Summary of The Martian", "OL5A", "Someone Else"));

            var plan = Subject.PlanAuthor(_local);

            plan.NewId.Should().BeNull();
        }

        [Test]
        public void should_skip_author_already_in_library_under_open_library_id()
        {
            GivenBook(1, "The Martian", LocalEdition(11, "9780804139021"));
            GivenIsbnHit("9780804139021", Work("OL17091839W", "The Martian"));
            Mocker.GetMock<IAuthorService>().Setup(x => x.FindById(OlAuthor)).Returns(new Author());

            var plan = Subject.PlanAuthor(_local);

            plan.NewId.Should().BeNull();
            plan.Problem.Should().Contain(OlAuthor);
        }

        [Test]
        public void should_not_map_two_local_books_to_the_same_work_and_prefer_the_one_with_files()
        {
            GivenBook(1, "The Martian", LocalEdition(11));
            GivenBook(2, "The Martian (Classroom Edition)", LocalEdition(21));
            _local.EditionIdsWithFiles.Add(21);
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.SearchAuthors("Andy Weir"))
                .Returns(new List<OpenLibraryAuthorSearchDoc> { new OpenLibraryAuthorSearchDoc { Key = OlAuthor, Name = "Andy Weir", WorkCount = 13 } });
            GivenAuthorWorks(Work("OL17091839W", "The Martian"));
            GivenWorkEditions("OL17091839W", OlEdition("OL1M", "9780000000001"), OlEdition("OL2M", "9780000000002"));

            var plan = Subject.PlanAuthor(_local);

            plan.Books.Single(b => b.BookId == 2).NewId.Should().Be("OL17091839W");
            plan.Books.Single(b => b.BookId == 1).NewId.Should().BeNull();
            plan.Books.Single(b => b.BookId == 1).Problem.Should().Contain("duplicate");
        }

        [Test]
        public void should_only_map_editions_with_files_or_monitored_and_never_reuse_one()
        {
            GivenBook(1, "The Martian", LocalEdition(11, "9780804139021", monitored: true), LocalEdition(12, "9781111111111", monitored: false), LocalEdition(13, null, monitored: false));
            _local.EditionIdsWithFiles.Add(12);

            var martian = Work("OL17091839W", "The Martian");
            GivenIsbnHit("9781111111111", martian);
            GivenAuthorWorks(martian);
            GivenWorkEditions("OL17091839W", OlEdition("OL32815550M", "9780804139021"), OlEdition("OL7M", "9787777777777"));

            var book = Subject.PlanAuthor(_local).Books.Single();

            book.NewId.Should().Be("OL17091839W");
            book.Editions.Should().HaveCount(2);
            book.EditionsDropped.Should().Be(1, "edition 13 has no files and is not monitored");

            // Edition 12 (files) has no ISBN match, so it gets the best free edition, never the one taken by 11.
            book.Editions.Single(e => e.EditionId == 11).NewId.Should().Be("OL32815550M");
            book.Editions.Single(e => e.EditionId == 12).NewId.Should().Be("OL7M");
            book.Editions.Single(e => e.EditionId == 12).HasFiles.Should().BeTrue();
        }

        [Test]
        public void should_not_migrate_book_when_an_edition_with_files_cannot_be_mapped()
        {
            GivenBook(1, "The Martian", LocalEdition(11, "9780804139021"));
            _local.EditionIdsWithFiles.Add(11);
            var martian = Work("OL17091839W", "The Martian");
            GivenIsbnHit("9780804139021", martian);
            GivenAuthorWorks(martian);
            GivenWorkEditions("OL17091839W");

            var book = Subject.PlanAuthor(_local).Books.Single();

            book.NewId.Should().BeNull();
            book.Editions.Should().BeEmpty();
            book.Problem.Should().Contain("has files");
        }

        [Test]
        public void should_not_reuse_editions_already_in_database()
        {
            GivenBook(1, "The Martian", LocalEdition(11, "9780804139021"));
            var martian = Work("OL17091839W", "The Martian");
            GivenIsbnHit("9780804139021", martian);
            GivenAuthorWorks(martian);
            GivenWorkEditions("OL17091839W", OlEdition("OL32815550M", "9780804139021"), OlEdition("OL2M"));
            Mocker.GetMock<IEditionService>().Setup(x => x.GetEditionByForeignEditionId("OL32815550M")).Returns(new Edition());

            var edition = Subject.PlanAuthor(_local).Books.Single().Editions.Single();

            edition.NewId.Should().Be("OL2M");
            edition.Method.Should().Be(MatchMethod.DefaultEdition);
        }

        [Test]
        public void should_not_match_works_whose_primary_author_is_someone_else()
        {
            GivenBook(1, "Cheshire Crossing", LocalEdition(11));
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.SearchAuthors("Andy Weir"))
                .Returns(new List<OpenLibraryAuthorSearchDoc> { new OpenLibraryAuthorSearchDoc { Key = OlAuthor, Name = "Andy Weir", WorkCount = 13 } });

            // Co-written work where Andy Weir is listed second: refreshes would not list it under him.
            var coWritten = Work("OL17841386W", "Cheshire Crossing", "OL2A", "Sarah Andersen");
            coWritten.AuthorKey.Add(OlAuthor);
            GivenAuthorWorks(coWritten);

            Subject.PlanAuthor(_local).Books.Single().NewId.Should().BeNull();
        }
    }
}
