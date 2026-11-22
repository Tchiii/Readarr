using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.OpenLibrary;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    [TestFixture]
    public class OpenLibraryValuesFixture
    {
        [TestCase("OL79034A", true, false, false)]
        [TestCase("OL893414W", false, true, false)]
        [TestCase("OL7500941M", false, false, true)]
        [TestCase("58", false, false, false)]
        [TestCase("ol79034a", false, false, false)]
        [TestCase("OL79034", false, false, false)]
        [TestCase(null, false, false, false)]
        public void should_recognise_id_kinds(string id, bool author, bool work, bool edition)
        {
            OpenLibraryIds.IsAuthorId(id).Should().Be(author);
            OpenLibraryIds.IsWorkId(id).Should().Be(work);
            OpenLibraryIds.IsEditionId(id).Should().Be(edition);
        }

        [TestCase("/works/OL893414W", "OL893414W")]
        [TestCase("/authors/OL79034A/", "OL79034A")]
        [TestCase("ol7500941m", "OL7500941M")]
        [TestCase("OL893414W", "OL893414W")]
        [TestCase("/languages/eng", null)]
        [TestCase("44767458", null)]
        [TestCase("", null)]
        public void should_extract_id_from_key(string key, string expected)
        {
            OpenLibraryIds.FromKey(key).Should().Be(expected);
        }

        [TestCase("1978-08-01", 1978, 8, 1)]
        [TestCase("August 1, 1978", 1978, 8, 1)]
        [TestCase("Aug 1, 1978", 1978, 8, 1)]
        [TestCase("1 August 1978", 1978, 8, 1)]
        [TestCase("August 1978", 1978, 8, 1)]
        [TestCase("1978-08", 1978, 8, 1)]
        [TestCase("1978", 1978, 1, 1)]
        [TestCase("c1978", 1978, 1, 1)]
        [TestCase("[1978?]", 1978, 1, 1)]
        [TestCase("printed in 2021 by", 2021, 1, 1)]
        public void should_parse_publish_dates(string raw, int year, int month, int day)
        {
            var date = OpenLibraryValues.ParseDate(raw);

            date.Should().Be(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));
            date.Value.Kind.Should().Be(DateTimeKind.Utc);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("unknown")]
        [TestCase("19th century")]
        public void should_return_null_for_unparseable_dates(string raw)
        {
            OpenLibraryValues.ParseDate(raw).Should().BeNull();
        }

        [TestCase("0425038912", "9780425038918")]
        [TestCase("0-425-03891-2", "9780425038918")]
        [TestCase("9780425038918", "9780425038918")]
        [TestCase("978-0-425-03891-8", "9780425038918")]
        [TestCase("080442957X", "9780804429573")]
        [TestCase("12345", null)]
        [TestCase(null, null)]
        public void should_normalise_isbns_to_13_digits(string raw, string expected)
        {
            OpenLibraryValues.NormalizeIsbn(raw).Should().Be(expected);
        }

        [TestCase("/languages/eng", "eng")]
        [TestCase("/languages/FRE", "fre")]
        [TestCase("/languages/", null)]
        [TestCase(null, null)]
        public void should_extract_language_code(string key, string expected)
        {
            OpenLibraryValues.LanguageFromKey(key).Should().Be(expected);
        }

        [Test]
        public void should_drop_machine_tags_and_duplicates_from_subjects()
        {
            var subjects = new[] { "Science fiction", "nyt:mass-market-monthly=2021-11-07", "award:hugo_award=1966", "science fiction", "Ecology" };

            OpenLibraryValues.CleanSubjects(subjects).Should().Equal("science fiction", "ecology");
        }

        [Test]
        public void should_limit_subjects()
        {
            var subjects = new[] { "a", "b", "c", "d" };

            OpenLibraryValues.CleanSubjects(subjects, 2).Should().Equal("a", "b");
        }
    }
}
