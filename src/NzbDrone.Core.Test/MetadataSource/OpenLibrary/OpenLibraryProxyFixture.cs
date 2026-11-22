using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    /// <summary>
    /// Talks to the real openlibrary.org, so it only runs with the IntegrationTest category.
    /// Assertions stick to stable facts (IDs, names, titles) since Open Library is community edited.
    /// </summary>
    [TestFixture]
    [IntegrationTest]
    public class OpenLibraryProxyFixture : CoreTest<OpenLibraryProxy>
    {
        [SetUp]
        public void Setup()
        {
            UseRealHttp();
        }

        [Test]
        public void should_get_book_with_all_editions()
        {
            var result = Subject.GetBookInfo("OL893414W");
            var authorId = result.Item1;
            var book = result.Item2;
            var authors = result.Item3;

            authorId.Should().Be("OL79034A");
            authors.Should().ContainSingle(x => x.ForeignAuthorId == "OL79034A" && x.Name == "Frank Herbert");

            book.ForeignBookId.Should().Be("OL893414W");
            book.Title.Should().Be("Dune");
            book.ReleaseDate.Value.Year.Should().Be(1965);
            book.Ratings.Votes.Should().BeGreaterThan(0);

            // Dune has well over 100 editions: we must get them all, not just the first page.
            book.Editions.Value.Should().HaveCountGreaterThan(100);
            book.Editions.Value.Should().ContainSingle(x => x.Monitored);
            book.Editions.Value.Should().OnlyContain(x => OpenLibraryIds.IsEditionId(x.ForeignEditionId));
        }

        [Test]
        public void should_get_author_with_books()
        {
            var author = Subject.GetAuthorInfo("OL7421324A");

            author.Metadata.Value.Name.Should().Be("Tara Westover");
            author.Books.Value.Should().Contain(x => x.Title.StartsWith("Educated"));
            author.Books.Value.Should().OnlyContain(x => x.Editions.Value.Count(e => e.Monitored) == 1);
        }

        [Test]
        public void should_throw_for_unknown_ids()
        {
            Assert.Throws<AuthorNotFoundException>(() => Subject.GetAuthorInfo("OL999999999999A"));
            Assert.Throws<BookNotFoundException>(() => Subject.GetBookInfo("OL999999999999W"));
        }

        [TestCase("dune", null, "OL893414W")]
        [TestCase("project hail mary", "andy weir", null)]
        [TestCase("isbn:9780425038918", null, "OL893414W")]
        [TestCase("work:OL893414W", null, "OL893414W")]
        [TestCase("OL893414W", null, "OL893414W")]
        [TestCase("edition:OL7500941M", null, "OL893414W")]
        public void should_search(string query, string author, string expectedWork)
        {
            var result = Subject.SearchForNewBook(query, author);

            result.Should().NotBeEmpty();
            result.Should().OnlyContain(x => x.Editions.Value.Count(e => e.Monitored) == 1);
            result.Should().OnlyContain(x => x.Author.Value != null && x.AuthorMetadata.Value != null);

            if (expectedWork != null)
            {
                result.Select(x => x.ForeignBookId).Should().Contain(expectedWork);
            }
        }

        [Test]
        public void should_monitor_the_requested_edition()
        {
            var book = Subject.SearchForNewBook("edition:OL7500941M", null, false).Single();

            book.Editions.Value.Should().ContainSingle().Which.ForeignEditionId.Should().Be("OL7500941M");
        }

        [Test]
        public void should_find_old_editions_by_goodreads_id()
        {
            // 234225 is a Goodreads edition of Dune that Open Library records in id_goodreads.
            Subject.SearchByGoodreadsBookId(234225, true).Select(x => x.ForeignBookId).Should().Contain("OL893414W");
        }

        [Test]
        public void should_return_nothing_for_unmappable_goodreads_ids()
        {
            Subject.SearchForNewBook("author:58", null).Should().BeEmpty();
        }
    }
}
