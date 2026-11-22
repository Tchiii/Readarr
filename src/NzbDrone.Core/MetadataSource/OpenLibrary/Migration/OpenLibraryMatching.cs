using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Migration
{
    /// <summary>
    /// Deliberately strict comparisons for the Goodreads → Open Library migration. A missed match only leaves
    /// an entry on its Goodreads ID, while a wrong match would attach the wrong metadata to someone's files.
    /// </summary>
    public static class OpenLibraryMatching
    {
        // "Dune (Dune Chronicles, #1)", "Children of Dune (Dune Chronicles, Book 3)": Goodreads and Open Library
        // both append series information in trailing parentheses.
        private static readonly Regex TrailingParentheses = new Regex(@"\s*[\(\[][^\)\]]*[\)\]]\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Name key: accents removed, lower case, letters/digits only, tokens sorted. "Herbert, Frank" and
        /// "Frank Herbert" give the same key, and so do "J.R.R. Tolkien" and "J. R. R. Tolkien".
        /// </summary>
        public static string NameKey(string name)
        {
            var tokens = Tokens(name).OrderBy(x => x, System.StringComparer.Ordinal);

            return string.Concat(tokens);
        }

        public static bool NamesMatch(string a, string b)
        {
            var keyA = NameKey(a);

            return keyA.Length > 0 && keyA == NameKey(b);
        }

        /// <summary>
        /// Title key: trailing series parentheses and subtitle (after ':') removed, accents removed, lower case,
        /// letters/digits only. "Dune: Deluxe Edition" and "Dune (Dune Chronicles, #1)" both give "dune".
        /// </summary>
        public static string TitleKey(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var t = title;

            // Strip repeatedly: "Title (Series, #1) (French Edition)".
            string previous;
            do
            {
                previous = t;
                t = TrailingParentheses.Replace(t, string.Empty);
            }
            while (t != previous && t.Length > 0);

            var colon = t.IndexOf(':');
            if (colon > 0)
            {
                t = t.Substring(0, colon);
            }

            return string.Join(" ", Tokens(t));
        }

        public static bool TitlesMatch(string a, string b)
        {
            var keyA = TitleKey(a);

            return keyA.Length > 0 && keyA == TitleKey(b);
        }

        private static string[] Tokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return System.Array.Empty<string>();
            }

            var decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);

            foreach (var c in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);

                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            return sb.ToString().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
