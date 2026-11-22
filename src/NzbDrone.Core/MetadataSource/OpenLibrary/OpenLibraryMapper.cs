using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.MetadataSource.OpenLibrary
{
    /// <summary>
    /// Pure mapping from Open Library DTOs to Readarr's model. No I/O happens here so that every rule can
    /// be unit tested; <see cref="OpenLibraryProxy"/> does the fetching.
    ///
    /// Model correspondence:
    /// <list type="bullet">
    /// <item>Open Library author  → Readarr <see cref="Author"/> / <see cref="AuthorMetadata"/></item>
    /// <item>Open Library work    → Readarr <see cref="Book"/></item>
    /// <item>Open Library edition → Readarr <see cref="Edition"/></item>
    /// </list>
    /// Series are not mapped yet: Open Library has no series entity, only free text on some editions.
    /// </summary>
    public static class OpenLibraryMapper
    {
        public const string SiteUrl = "https://openlibrary.org";
        private const string CoversUrl = "https://covers.openlibrary.org";
        private const string LinkName = "Open Library";

        // --- Authors -------------------------------------------------------------------------------
        public static AuthorMetadata MapAuthorMetadata(string authorId, OpenLibraryAuthor author)
        {
            var metadata = MapAuthorMetadata(authorId, author.Name ?? author.PersonalName);

            metadata.Overview = author.Bio;
            metadata.Status = author.DeathDate.IsNotNullOrWhiteSpace() ? AuthorStatusType.Ended : AuthorStatusType.Continuing;

            // Photo IDs of -1 are placeholders Open Library uses for removed images.
            var photoId = author.Photos?.FirstOrDefault(x => x > 0);
            if (photoId is > 0)
            {
                metadata.Images.Clear();
                metadata.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Poster, $"{CoversUrl}/a/id/{photoId}-L.jpg"));
            }

            foreach (var link in author.Links ?? new List<OpenLibraryLink>())
            {
                if (link.Url.IsNotNullOrWhiteSpace())
                {
                    metadata.Links.Add(new Links { Url = link.Url, Name = link.Title ?? link.Url });
                }
            }

            return metadata;
        }

        /// <summary>Minimal metadata, used for search results where only the author's key and name are known.</summary>
        public static AuthorMetadata MapAuthorMetadata(string authorId, string name)
        {
            var metadata = new AuthorMetadata
            {
                ForeignAuthorId = authorId,
                TitleSlug = authorId,
                Name = (name ?? authorId).CleanSpaces(),
                Status = AuthorStatusType.Continuing
            };

            metadata.SortName = metadata.Name.ToLower();
            metadata.NameLastFirst = metadata.Name.ToLastFirst();
            metadata.SortNameLastFirst = metadata.NameLastFirst.ToLower();

            // The covers service can resolve an author photo by OLID, which saves a request per author.
            // default=false makes it answer 404 instead of a blank placeholder when there is no photo.
            metadata.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Poster, $"{CoversUrl}/a/olid/{authorId}-L.jpg?default=false"));
            metadata.Links.Add(new Links { Url = $"{SiteUrl}/authors/{authorId}", Name = LinkName });

            return metadata;
        }

        // --- Editions ------------------------------------------------------------------------------
        public static Edition MapEdition(OpenLibraryEdition resource, string fallbackTitle)
        {
            var id = OpenLibraryIds.FromKey(resource.Key);

            var isbn = resource.Isbn13?.Select(OpenLibraryValues.NormalizeIsbn).FirstOrDefault(x => x != null)
                       ?? resource.Isbn10?.Select(OpenLibraryValues.NormalizeIsbn).FirstOrDefault(x => x != null);

            var edition = new Edition
            {
                ForeignEditionId = id,
                TitleSlug = id,
                Title = (resource.Title.IsNotNullOrWhiteSpace() ? resource.Title : fallbackTitle ?? id).CleanSpaces(),
                Isbn13 = isbn,
                Asin = resource.Identifiers?.GetValueOrDefault("amazon")?.FirstOrDefault(x => x.IsNotNullOrWhiteSpace()),
                Language = OpenLibraryValues.LanguageFromKey(resource.Languages?.FirstOrDefault()?.Key),
                Overview = resource.Description ?? string.Empty,
                Format = resource.PhysicalFormat,
                IsEbook = IsEbookFormat(resource.PhysicalFormat),
                Disambiguation = resource.EditionName,
                Publisher = resource.Publishers?.FirstOrDefault(),
                PageCount = resource.NumberOfPages ?? 0,
                ReleaseDate = OpenLibraryValues.ParseDate(resource.PublishDate),

                // Open Library only rates works, not editions.
                Ratings = new Ratings { Votes = 0, Value = 0 }
            };

            var coverId = resource.Covers?.FirstOrDefault(x => x > 0);
            if (coverId is > 0)
            {
                edition.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Cover, $"{CoversUrl}/b/id/{coverId}-L.jpg"));
            }

            edition.Links.Add(new Links { Url = $"{SiteUrl}/books/{id}", Name = LinkName });

            return edition;
        }

        /// <summary>Maps the single edition embedded in a search doc (search results are deliberately light).</summary>
        public static Edition MapEdition(OpenLibrarySearchEditionDoc resource, string fallbackTitle)
        {
            var id = OpenLibraryIds.FromKey(resource.Key);
            var isbns = resource.Isbn?.Select(OpenLibraryValues.NormalizeIsbn).Where(x => x != null).ToList() ?? new List<string>();

            var edition = new Edition
            {
                ForeignEditionId = id,
                TitleSlug = id,
                Title = (resource.Title.IsNotNullOrWhiteSpace() ? resource.Title : fallbackTitle ?? id).CleanSpaces(),
                Isbn13 = isbns.FirstOrDefault(),
                Language = resource.Language?.FirstOrDefault(),
                Publisher = resource.Publisher?.FirstOrDefault(),
                ReleaseDate = OpenLibraryValues.ParseDate(resource.PublishDate?.FirstOrDefault()),
                Ratings = new Ratings { Votes = 0, Value = 0 }
            };

            if (resource.CoverId is > 0)
            {
                edition.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Cover, $"{CoversUrl}/b/id/{resource.CoverId}-L.jpg"));
            }

            edition.Links.Add(new Links { Url = $"{SiteUrl}/books/{id}", Name = LinkName });

            return edition;
        }

        public static bool IsEbookFormat(string format)
        {
            if (format.IsNullOrWhiteSpace())
            {
                return false;
            }

            var f = format.ToLowerInvariant();

            return f.Contains("ebook") || f.Contains("e-book") || f.Contains("electronic") || f.Contains("kindle") || f.Contains("epub");
        }

        /// <summary>
        /// Goodreads let Readarr monitor the most popular edition by default. Open Library has no per-edition
        /// ratings, so we pick the most "usable" edition instead:
        /// ISBN/ASIN (needed by the default metadata profile and by indexer searches) > English (the default
        /// profile only allows English) > has a cover > has a page count > has a date. Ties keep Open Library's order.
        /// </summary>
        public static int EditionScore(Edition edition)
        {
            var score = 0;

            if (edition.Isbn13.IsNotNullOrWhiteSpace() || edition.Asin.IsNotNullOrWhiteSpace())
            {
                score += 16;
            }

            if (edition.Language == "eng" || edition.Language == "en")
            {
                score += 8;
            }

            if (edition.Images.Any())
            {
                score += 4;
            }

            if (edition.PageCount > 0)
            {
                score += 2;
            }

            if (edition.ReleaseDate.HasValue)
            {
                score += 1;
            }

            return score;
        }

        /// <summary>Marks exactly one edition as monitored (Readarr asserts this invariant).</summary>
        public static void ChooseMonitoredEdition(List<Edition> editions)
        {
            if (!editions.Any())
            {
                return;
            }

            editions.ForEach(x => x.Monitored = false);

            // MaxBy returns the first maximum, so ties keep Open Library's ordering.
            editions.MaxBy(EditionScore).Monitored = true;
        }

        /// <param name="workId">Bare work ID (OLxxxW).</param>
        /// <param name="title">Work title; falls back to the monitored edition's title.</param>
        /// <param name="subjects">Raw Open Library subjects, cleaned into genres.</param>
        /// <param name="firstPublished">Work-level first publication date, if known.</param>
        /// <param name="ratingAverage">Work rating (Open Library rates works, not editions).</param>
        /// <param name="ratingCount">Number of ratings.</param>
        /// <param name="editions">Mapped editions. One of them is marked monitored here.</param>
        public static Book MapBook(string workId,
                                   string title,
                                   IEnumerable<string> subjects,
                                   DateTime? firstPublished,
                                   double? ratingAverage,
                                   int? ratingCount,
                                   List<Edition> editions)
        {
            editions = editions.Where(x => x.ForeignEditionId != null).DistinctBy(x => x.ForeignEditionId).ToList();
            ChooseMonitoredEdition(editions);

            var monitored = editions.SingleOrDefault(x => x.Monitored);

            var book = new Book
            {
                ForeignBookId = workId,
                TitleSlug = workId,
                Title = title.IsNotNullOrWhiteSpace() ? title.CleanSpaces() : monitored?.Title ?? workId,
                Genres = OpenLibraryValues.CleanSubjects(subjects),
                RelatedBooks = new List<int>(),
                AnyEditionOk = true,
                Editions = editions,
                SeriesLinks = new List<SeriesBookLink>(),
                Ratings = new Ratings
                {
                    Votes = ratingCount ?? 0,
                    Value = (decimal)(ratingAverage ?? 0)
                }
            };

            book.CleanTitle = Parser.Parser.CleanAuthorName(book.Title);

            // Prefer the work's own first-publication date; otherwise the earliest edition we know of.
            book.ReleaseDate = firstPublished ?? editions.Where(x => x.ReleaseDate.HasValue).Select(x => x.ReleaseDate).Min();

            book.Links.Add(new Links { Url = $"{SiteUrl}/works/{workId}", Name = LinkName });

            return book;
        }

        /// <summary>
        /// Maps one search hit to a light <see cref="Book"/> carrying a single (monitored) edition and its
        /// primary author's metadata. This is enough for the UI and for adding the book: AddBookService
        /// fetches the full work afterwards and looks the monitored edition up in it.
        /// Returns null for hits without an author or an edition, which Readarr cannot use.
        /// </summary>
        public static Book MapSearchDoc(OpenLibrarySearchDoc doc)
        {
            var workId = OpenLibraryIds.FromKey(doc.Key);
            var authorId = OpenLibraryIds.FromKey(doc.AuthorKey?.FirstOrDefault());
            var editionDoc = doc.Editions?.Docs?.FirstOrDefault();

            if (workId == null || authorId == null || editionDoc == null || OpenLibraryIds.FromKey(editionDoc.Key) == null)
            {
                return null;
            }

            var edition = MapEdition(editionDoc, doc.Title);

            var book = MapBook(workId,
                               doc.Title,
                               doc.Subject,
                               OpenLibraryValues.FromYear(doc.FirstPublishYear),
                               doc.RatingsAverage,
                               doc.RatingsCount,
                               new List<Edition> { edition });

            book.AuthorMetadata = MapAuthorMetadata(authorId, doc.AuthorName?.FirstOrDefault());

            return book;
        }
    }
}
