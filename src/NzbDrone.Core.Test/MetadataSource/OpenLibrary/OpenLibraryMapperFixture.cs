using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    // The JSON below is trimmed from real Open Library responses (Frank Herbert / Dune).
    [TestFixture]
    public class OpenLibraryMapperFixture
    {
        private const string AuthorJson = @"{
            ""key"": ""/authors/OL79034A"",
            ""type"": {""key"": ""/type/author""},
            ""name"": ""Frank  Herbert"",
            ""bio"": {""type"": ""/type/text"", ""value"": ""American science fiction author.""},
            ""photos"": [-1, 6257553],
            ""links"": [{""title"": ""Official Website"", ""url"": ""https://dunenovels.com"", ""type"": {""key"": ""/type/link""}}],
            ""death_date"": ""11 February 1986""
        }";

        private const string EditionJson = @"{
            ""key"": ""/books/OL7500941M"",
            ""type"": {""key"": ""/type/edition""},
            ""title"": ""Dune"",
            ""works"": [{""key"": ""/works/OL893414W""}],
            ""isbn_10"": [""0425038912""],
            ""identifiers"": {""amazon"": [""B00B7NPRY8""]},
            ""languages"": [{""key"": ""/languages/eng""}],
            ""publishers"": [""Berkley""],
            ""publish_date"": ""August 1, 1978"",
            ""number_of_pages"": 489,
            ""physical_format"": ""Mass Market Paperback"",
            ""edition_name"": ""Berkley Medallion edition"",
            ""description"": ""Set on the desert planet Arrakis..."",
            ""covers"": [9705237]
        }";

        private const string SearchDocJson = @"{
            ""key"": ""/works/OL893414W"",
            ""title"": ""Dune"",
            ""author_key"": [""OL79034A""],
            ""author_name"": [""Frank Herbert""],
            ""first_publish_year"": 1965,
            ""edition_count"": 155,
            ""ratings_average"": 4.30819,
            ""ratings_count"": 464,
            ""subject"": [""Science fiction"", ""award:hugo_award=1966"", ""Ecology""],
            ""editions"": {""numFound"": 155, ""docs"": [{
                ""key"": ""/books/OL7500941M"",
                ""title"": ""Dune"",
                ""language"": [""eng""],
                ""isbn"": [""9780425038918"", ""0425038912""],
                ""cover_i"": 9705237,
                ""publisher"": [""Berkley""],
                ""publish_date"": [""August 1, 1978""]
            }]}
        }";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

        private static Edition MakeEdition(string id, string isbn = null, string language = null, bool cover = false, int pages = 0, DateTime? date = null)
        {
            var edition = new Edition { ForeignEditionId = id, Isbn13 = isbn, Language = language, PageCount = pages, ReleaseDate = date };
            if (cover)
            {
                edition.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Cover, "https://example.org/c.jpg"));
            }

            return edition;
        }

        [Test]
        public void should_map_author()
        {
            var metadata = OpenLibraryMapper.MapAuthorMetadata("OL79034A", Parse<OpenLibraryAuthor>(AuthorJson));

            metadata.ForeignAuthorId.Should().Be("OL79034A");
            metadata.TitleSlug.Should().Be("OL79034A");
            metadata.Name.Should().Be("Frank Herbert");
            metadata.NameLastFirst.Should().Be("Herbert, Frank");
            metadata.SortName.Should().Be("frank herbert");
            metadata.Overview.Should().Be("American science fiction author.");
            metadata.Status.Should().Be(AuthorStatusType.Ended);

            // -1 is a removed-photo placeholder and must be skipped.
            metadata.Images.Should().ContainSingle(x => x.Url == "https://covers.openlibrary.org/a/id/6257553-L.jpg" && x.CoverType == MediaCoverTypes.Poster);
            metadata.Links.Should().Contain(x => x.Url == "https://dunenovels.com" && x.Name == "Official Website");
            metadata.Links.Should().Contain(x => x.Url == "https://openlibrary.org/authors/OL79034A");
        }

        [Test]
        public void should_read_bio_given_as_plain_string()
        {
            var author = Parse<OpenLibraryAuthor>(@"{""key"": ""/authors/OL1A"", ""name"": ""X"", ""bio"": ""Plain bio""}");

            author.Bio.Should().Be("Plain bio");
        }

        [Test]
        public void should_detect_merged_and_deleted_records()
        {
            Parse<OpenLibraryAuthor>(@"{""key"": ""/authors/OL1A"", ""type"": {""key"": ""/type/redirect""}, ""location"": ""/authors/OL2A""}").IsRedirect.Should().BeTrue();
            Parse<OpenLibraryAuthor>(@"{""key"": ""/authors/OL1A"", ""type"": {""key"": ""/type/delete""}}").IsDeleted.Should().BeTrue();
            Parse<OpenLibraryAuthor>(AuthorJson).IsRedirect.Should().BeFalse();
        }

        [Test]
        public void should_map_edition()
        {
            var edition = OpenLibraryMapper.MapEdition(Parse<OpenLibraryEdition>(EditionJson), "Work title");

            edition.ForeignEditionId.Should().Be("OL7500941M");
            edition.TitleSlug.Should().Be("OL7500941M");
            edition.Title.Should().Be("Dune");
            edition.Isbn13.Should().Be("9780425038918");
            edition.Asin.Should().Be("B00B7NPRY8");
            edition.Language.Should().Be("eng");
            edition.Publisher.Should().Be("Berkley");
            edition.PageCount.Should().Be(489);
            edition.ReleaseDate.Should().Be(new DateTime(1978, 8, 1, 0, 0, 0, DateTimeKind.Utc));
            edition.Format.Should().Be("Mass Market Paperback");
            edition.IsEbook.Should().BeFalse();
            edition.Disambiguation.Should().Be("Berkley Medallion edition");
            edition.Overview.Should().Be("Set on the desert planet Arrakis...");
            edition.Images.Should().ContainSingle(x => x.Url == "https://covers.openlibrary.org/b/id/9705237-L.jpg" && x.CoverType == MediaCoverTypes.Cover);
            edition.Links.Should().ContainSingle(x => x.Url == "https://openlibrary.org/books/OL7500941M");
        }

        [Test]
        public void should_fall_back_to_work_title_and_empty_overview()
        {
            var edition = OpenLibraryMapper.MapEdition(Parse<OpenLibraryEdition>(@"{""key"": ""/books/OL1M""}"), "Work title");

            edition.Title.Should().Be("Work title");
            edition.Overview.Should().BeEmpty();
            edition.PageCount.Should().Be(0);
            edition.Images.Should().BeEmpty();
        }

        [TestCase("Kindle Edition", true)]
        [TestCase("ebook", true)]
        [TestCase("Electronic resource", true)]
        [TestCase("Paperback", false)]
        [TestCase(null, false)]
        public void should_detect_ebook_formats(string format, bool expected)
        {
            OpenLibraryMapper.IsEbookFormat(format).Should().Be(expected);
        }

        [Test]
        public void should_monitor_exactly_one_edition_preferring_isbn_then_english()
        {
            var editions = new List<Edition>
            {
                MakeEdition("OL1M", language: "fre", cover: true, pages: 300, date: DateTime.UtcNow),
                MakeEdition("OL2M", isbn: "9780000000002", language: "fre"),
                MakeEdition("OL3M", isbn: "9780000000003", language: "eng"),
                MakeEdition("OL4M", isbn: "9780000000004", language: "eng")
            };

            OpenLibraryMapper.ChooseMonitoredEdition(editions);

            editions.Should().ContainSingle(x => x.Monitored);

            // OL3M and OL4M tie: Open Library's order wins.
            editions.Single(x => x.Monitored).ForeignEditionId.Should().Be("OL3M");
        }

        [Test]
        public void should_map_book_from_editions()
        {
            var editions = new List<Edition>
            {
                MakeEdition("OL1M", isbn: "9780000000001", language: "eng", date: new DateTime(1990, 5, 1)),
                MakeEdition("OL2M", date: new DateTime(1965, 8, 1)),
                MakeEdition("OL2M")
            };

            var book = OpenLibraryMapper.MapBook("OL893414W", "Dune", new[] { "Science fiction" }, null, 4.3, 464, editions);

            book.ForeignBookId.Should().Be("OL893414W");
            book.TitleSlug.Should().Be("OL893414W");
            book.Title.Should().Be("Dune");
            book.CleanTitle.Should().NotBeNullOrWhiteSpace();
            book.Genres.Should().Equal("science fiction");
            book.Ratings.Votes.Should().Be(464);
            book.Ratings.Value.Should().Be(4.3m);
            book.AnyEditionOk.Should().BeTrue();
            book.SeriesLinks.Value.Should().BeEmpty();
            book.Links.Should().ContainSingle(x => x.Url == "https://openlibrary.org/works/OL893414W");

            // Duplicated edition IDs are collapsed, and the earliest edition date is used as a fallback.
            book.Editions.Value.Should().HaveCount(2);
            book.Editions.Value.Should().ContainSingle(x => x.Monitored);
            book.ReleaseDate.Should().Be(new DateTime(1965, 8, 1));
        }

        [Test]
        public void should_prefer_work_publication_date_over_editions()
        {
            var editions = new List<Edition> { MakeEdition("OL1M", date: new DateTime(1990, 5, 1)) };

            var book = OpenLibraryMapper.MapBook("OL1W", "T", null, new DateTime(1965, 1, 1), null, null, editions);

            book.ReleaseDate.Should().Be(new DateTime(1965, 1, 1));
            book.Ratings.Votes.Should().Be(0);
            book.Genres.Should().BeEmpty();
        }

        [Test]
        public void should_use_monitored_edition_title_when_work_has_none()
        {
            var editions = new List<Edition> { new Edition { ForeignEditionId = "OL1M", Title = "Edition title" } };

            OpenLibraryMapper.MapBook("OL1W", null, null, null, null, null, editions).Title.Should().Be("Edition title");
        }

        [Test]
        public void should_map_search_doc_to_light_book()
        {
            var book = OpenLibraryMapper.MapSearchDoc(Parse<OpenLibrarySearchDoc>(SearchDocJson));

            book.ForeignBookId.Should().Be("OL893414W");
            book.Title.Should().Be("Dune");
            book.ReleaseDate.Should().Be(new DateTime(1965, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            book.Ratings.Votes.Should().Be(464);
            book.Genres.Should().Equal("science fiction", "ecology");

            book.Editions.Value.Should().ContainSingle();
            var edition = book.Editions.Value.Single();
            edition.ForeignEditionId.Should().Be("OL7500941M");
            edition.Monitored.Should().BeTrue();
            edition.Isbn13.Should().Be("9780425038918");
            edition.Language.Should().Be("eng");

            book.AuthorMetadata.Value.ForeignAuthorId.Should().Be("OL79034A");
            book.AuthorMetadata.Value.Name.Should().Be("Frank Herbert");
            book.AuthorMetadata.Value.Images.Single().Url.Should().Be("https://covers.openlibrary.org/a/olid/OL79034A-L.jpg?default=false");
        }

        [TestCase(@"{""key"": ""/works/OL1W"", ""title"": ""No author"", ""editions"": {""docs"": [{""key"": ""/books/OL1M""}]}}")]
        [TestCase(@"{""key"": ""/works/OL1W"", ""title"": ""No edition"", ""author_key"": [""OL1A""]}")]
        public void should_skip_unusable_search_docs(string json)
        {
            OpenLibraryMapper.MapSearchDoc(Parse<OpenLibrarySearchDoc>(json)).Should().BeNull();
        }
    }
}
