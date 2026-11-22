using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MetadataSource
{
    [TestFixture]
    public class MetadataSourceRouterFixture : CoreTest<MetadataSourceRouter>
    {
        private void GivenCustomMetadataSource(string url)
        {
            Mocker.GetMock<IConfigService>().SetupGet(x => x.MetadataSource).Returns(url);
        }

        private static Book BookBy(string authorId)
        {
            var author = new Author { Metadata = new AuthorMetadata { ForeignAuthorId = authorId } };

            return new Book { Author = author };
        }

        // --- Lookups by ID follow the ID, whatever the configuration ---
        [TestCase(null)]
        [TestCase("https://custom.example/v1/")]
        public void should_send_open_library_author_ids_to_open_library(string source)
        {
            GivenCustomMetadataSource(source);

            Subject.GetAuthorInfo("OL79034A");

            Mocker.GetMock<IOpenLibraryProxy>().Verify(x => x.GetAuthorInfo("OL79034A", true), Times.Once());
            Mocker.GetMock<IBookInfoProxy>().Verify(x => x.GetAuthorInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase(null)]
        [TestCase("https://custom.example/v1/")]
        public void should_send_goodreads_author_ids_to_bookinfo(string source)
        {
            GivenCustomMetadataSource(source);

            Subject.GetAuthorInfo("58", false);

            Mocker.GetMock<IBookInfoProxy>().Verify(x => x.GetAuthorInfo("58", false), Times.Once());
            Mocker.GetMock<IOpenLibraryProxy>().Verify(x => x.GetAuthorInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_route_book_lookups_on_id_format()
        {
            Subject.GetBookInfo("OL893414W");
            Subject.GetBookInfo("3634639");

            Mocker.GetMock<IOpenLibraryProxy>().Verify(x => x.GetBookInfo("OL893414W"), Times.Once());
            Mocker.GetMock<IBookInfoProxy>().Verify(x => x.GetBookInfo("3634639"), Times.Once());
        }

        // --- New lookups follow the configuration ---
        [Test]
        public void should_search_open_library_by_default()
        {
            GivenCustomMetadataSource(string.Empty);

            Subject.SearchForNewBook("dune", null);
            Subject.SearchByIsbn("9780425038918");
            Subject.SearchByAsin("B00B7NPRY8");
            Subject.SearchByGoodreadsBookId(234225, true);

            var openLibrary = Mocker.GetMock<IOpenLibraryProxy>();
            openLibrary.Verify(x => x.SearchForNewBook("dune", null, true), Times.Once());
            openLibrary.Verify(x => x.SearchByIsbn("9780425038918"), Times.Once());
            openLibrary.Verify(x => x.SearchByAsin("B00B7NPRY8"), Times.Once());
            openLibrary.Verify(x => x.SearchByGoodreadsBookId(234225, true), Times.Once());

            Mocker.GetMock<IBookInfoProxy>().VerifyNoOtherCalls();
        }

        [Test]
        public void should_search_bookinfo_when_a_custom_source_is_configured()
        {
            GivenCustomMetadataSource("https://custom.example/v1/");

            Subject.SearchForNewBook("dune", "herbert", false);
            Subject.SearchByIsbn("9780425038918");
            Subject.SearchForNewAuthor("herbert");
            Subject.SearchForNewEntity("dune");

            var bookInfo = Mocker.GetMock<IBookInfoProxy>();
            bookInfo.Verify(x => x.SearchForNewBook("dune", "herbert", false), Times.Once());
            bookInfo.Verify(x => x.SearchByIsbn("9780425038918"), Times.Once());
            bookInfo.Verify(x => x.SearchForNewAuthor("herbert"), Times.Once());
            bookInfo.Verify(x => x.SearchForNewEntity("dune"), Times.Once());

            Mocker.GetMock<IOpenLibraryProxy>().VerifyNoOtherCalls();
        }

        [Test]
        public void should_only_ask_bookinfo_for_changed_authors_when_a_custom_source_is_configured()
        {
            Subject.GetChangedAuthors(DateTime.UtcNow).Should().BeNull();
            Mocker.GetMock<IBookInfoProxy>().Verify(x => x.GetChangedAuthors(It.IsAny<DateTime>()), Times.Never());

            GivenCustomMetadataSource("https://custom.example/v1/");
            Mocker.GetMock<IBookInfoProxy>().Setup(x => x.GetChangedAuthors(It.IsAny<DateTime>())).Returns(new HashSet<string> { "58" });

            Subject.GetChangedAuthors(DateTime.UtcNow).Should().BeEquivalentTo(new[] { "58" });
        }

        [Test]
        public void should_list_each_open_library_author_once_before_their_books()
        {
            var books = new List<Book> { BookBy("OL1A"), BookBy("OL2A"), BookBy("OL1A") };
            Mocker.GetMock<IOpenLibraryProxy>().Setup(x => x.SearchForNewBook("q", null, false)).Returns(books);

            var result = Subject.SearchForNewEntity("q");

            result.Should().HaveCount(5);
            result[0].Should().BeOfType<Author>().Which.ForeignAuthorId.Should().Be("OL1A");
            result[1].Should().BeSameAs(books[0]);
            result[2].Should().BeOfType<Author>().Which.ForeignAuthorId.Should().Be("OL2A");
            result[3].Should().BeSameAs(books[1]);
            result[4].Should().BeSameAs(books[2]);
        }

        [Test]
        public void should_deduplicate_open_library_authors()
        {
            Mocker.GetMock<IOpenLibraryProxy>()
                .Setup(x => x.SearchForNewBook("q", null, true))
                .Returns(new List<Book> { BookBy("OL1A"), BookBy("OL1A"), BookBy("OL2A") });

            Subject.SearchForNewAuthor("q").Should().HaveCount(2);
        }
    }
}
