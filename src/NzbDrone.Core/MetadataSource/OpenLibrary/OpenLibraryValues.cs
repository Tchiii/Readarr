using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.MetadataSource.OpenLibrary
{
    /// <summary>
    /// Open Library identifiers. Readarr stores them as-is in its Foreign*Id columns, so an Open Library
    /// library uses e.g. <c>OL79034A</c> (author), <c>OL893414W</c> (work = Readarr book) and
    /// <c>OL7500941M</c> (edition). Goodreads IDs, by contrast, are purely numeric, which is what lets
    /// <see cref="MetadataSourceRouter"/> tell the two apart.
    /// </summary>
    public static class OpenLibraryIds
    {
        private static readonly Regex AuthorRegex = new Regex(@"^OL\d+A$", RegexOptions.Compiled);
        private static readonly Regex WorkRegex = new Regex(@"^OL\d+W$", RegexOptions.Compiled);
        private static readonly Regex EditionRegex = new Regex(@"^OL\d+M$", RegexOptions.Compiled);

        public static bool IsAuthorId(string id) => id != null && AuthorRegex.IsMatch(id);

        public static bool IsWorkId(string id) => id != null && WorkRegex.IsMatch(id);

        public static bool IsEditionId(string id) => id != null && EditionRegex.IsMatch(id);

        public static bool IsOpenLibraryId(string id) => IsAuthorId(id) || IsWorkId(id) || IsEditionId(id);

        /// <summary>
        /// Turns an Open Library key (<c>/works/OL893414W</c>) or a bare ID into the bare, upper-case ID
        /// (<c>OL893414W</c>). Returns null for anything that is not an Open Library ID.
        /// </summary>
        public static string FromKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var id = key.Trim().TrimEnd('/').Split('/').Last().ToUpperInvariant();

            return IsOpenLibraryId(id) ? id : null;
        }
    }

    /// <summary>
    /// Parsing helpers for the loosely formatted values Open Library stores
    /// (free-text dates, ISBN-10, language keys, subjects).
    /// </summary>
    public static class OpenLibraryValues
    {
        // Formats seen in Open Library's publish_date field, most specific first.
        private static readonly string[] DateFormats =
        {
            "yyyy-MM-dd",
            "yyyy-MM",
            "MMMM d, yyyy",
            "MMMM d yyyy",
            "MMM d, yyyy",
            "MMM d yyyy",
            "MMM. d, yyyy",
            "d MMMM yyyy",
            "d MMM yyyy",
            "MMMM yyyy",
            "MMM yyyy",
            "MMM. yyyy",
            "yyyy"
        };

        // Digit lookarounds instead of \b, so that "c1978" (copyright 1978, very common) still matches.
        private static readonly Regex YearRegex = new Regex(@"(?<!\d)(1[0-9]{3}|20[0-9]{2})(?!\d)", RegexOptions.Compiled);

        /// <summary>
        /// Parses Open Library's free-text <c>publish_date</c> ("2021", "August 1, 1978", "1978-08-01",
        /// "c1978", "[1978?]"...). When only a year can be found the date is 1 January of that year, which
        /// is also what Goodreads did, so Readarr's existing "1 January = imprecise" heuristics still apply.
        /// Returns null when no plausible year is present.
        /// </summary>
        public static DateTime? ParseDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var cleaned = raw.Trim().Trim('[', ']', '.', '?').Trim();

            if (DateTime.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            {
                return DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            }

            var year = YearRegex.Match(cleaned);
            if (year.Success)
            {
                return new DateTime(int.Parse(year.Value, CultureInfo.InvariantCulture), 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }

            return null;
        }

        public static DateTime? FromYear(int? year)
        {
            if (year is null or < 1 or > 9999)
            {
                return null;
            }

            return new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        /// <summary>Strips hyphens/spaces and returns a 13 digit ISBN, converting ISBN-10 if needed.</summary>
        public static string NormalizeIsbn(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var isbn = new string(raw.Where(c => char.IsDigit(c) || c == 'X' || c == 'x').ToArray()).ToUpperInvariant();

            if (isbn.Length == 13 && isbn.All(char.IsDigit))
            {
                return isbn;
            }

            if (isbn.Length == 10)
            {
                return Isbn10To13(isbn);
            }

            return null;
        }

        /// <summary>ISBN-10 → ISBN-13: prefix with 978, drop the old check digit, compute the EAN-13 one.</summary>
        public static string Isbn10To13(string isbn10)
        {
            if (isbn10 == null || isbn10.Length != 10 || !isbn10.Take(9).All(char.IsDigit))
            {
                return null;
            }

            var core = "978" + isbn10.Substring(0, 9);
            var sum = 0;

            for (var i = 0; i < 12; i++)
            {
                var digit = core[i] - '0';
                sum += i % 2 == 0 ? digit : digit * 3;
            }

            var check = (10 - (sum % 10)) % 10;

            return core + check.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary><c>/languages/eng</c> → <c>eng</c> (ISO 639-2, which Readarr's metadata profiles understand).</summary>
        public static string LanguageFromKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var code = key.Trim().TrimEnd('/').Split('/').Last().ToLowerInvariant();

            return code.Length is 2 or 3 ? code : null;
        }

        /// <summary>
        /// Open Library subjects mix genres with machine tags (<c>nyt:...</c>, <c>award:...</c>) and very
        /// specific headings. Keep the first few human-readable ones, lower-cased like Goodreads genres were.
        /// </summary>
        public static List<string> CleanSubjects(IEnumerable<string> subjects, int max = 10)
        {
            if (subjects == null)
            {
                return new List<string>();
            }

            return subjects
                .Where(s => !string.IsNullOrWhiteSpace(s) && !s.Contains(':') && s.Length <= 50)
                .Select(s => s.Trim().ToLowerInvariant())
                .Distinct()
                .Take(max)
                .ToList();
        }
    }
}
