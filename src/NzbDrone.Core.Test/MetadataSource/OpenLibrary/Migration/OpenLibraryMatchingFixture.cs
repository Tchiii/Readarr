using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.OpenLibrary.Migration;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary.Migration
{
    [TestFixture]
    public class OpenLibraryMatchingFixture
    {
        [TestCase("Frank Herbert", "Frank Herbert")]
        [TestCase("Frank Herbert", "Herbert, Frank")]
        [TestCase("J.R.R. Tolkien", "J. R. R. Tolkien")]
        [TestCase("J.R.R. Tolkien", "JRR Tolkien")]
        [TestCase("Leïla Slimani", "Leila Slimani")]
        [TestCase("ANDY WEIR", "andy weir")]
        public void names_should_match(string a, string b)
        {
            OpenLibraryMatching.NamesMatch(a, b).Should().BeTrue();
        }

        [TestCase("Frank Herbert", "Brian Herbert")]
        [TestCase("Andy Weir", "Andy Weir bush")]
        [TestCase("", "")]
        [TestCase(null, null)]
        public void names_should_not_match(string a, string b)
        {
            OpenLibraryMatching.NamesMatch(a, b).Should().BeFalse();
        }

        [TestCase("Dune", "Dune (Dune Chronicles, #1)")]
        [TestCase("Children of Dune", "Children of Dune (Dune Chronicles, Book 3)")]
        [TestCase("Dune", "Dune: Deluxe Edition")]
        [TestCase("La Horde du Contrevent", "La horde du contrevent")]
        [TestCase("L'Étranger", "L'etranger")]
        [TestCase("La Zone du Dehors", "La Zone Du Dehors (French Edition)")]
        [TestCase("Les Furtifs", "Les Furtifs (Folio SF) [Poche]")]
        public void titles_should_match(string a, string b)
        {
            OpenLibraryMatching.TitlesMatch(a, b).Should().BeTrue();
        }

        [TestCase("Dune", "Dune Messiah")]
        [TestCase("The Martian", "Martian")]
        [TestCase("Summary of Educated", "Educated")]
        [TestCase("", "")]
        public void titles_should_not_match(string a, string b)
        {
            OpenLibraryMatching.TitlesMatch(a, b).Should().BeFalse();
        }
    }
}
