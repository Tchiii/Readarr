using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Migration
{
    /// <summary>One author of the local library, with everything the planner needs (no DB access while planning).</summary>
    public class LocalAuthor
    {
        public Author Author { get; set; }
        public List<Book> Books { get; set; } = new List<Book>();
        public Dictionary<int, List<Edition>> EditionsByBook { get; set; } = new Dictionary<int, List<Edition>>();
        public HashSet<int> EditionIdsWithFiles { get; set; } = new HashSet<int>();

        public List<Edition> EditionsOf(Book book) => EditionsByBook.GetValueOrDefault(book.Id) ?? new List<Edition>();
    }

    public interface IOpenLibraryMigrationPlanner
    {
        AuthorMigration PlanAuthor(LocalAuthor local);
    }

    /// <summary>
    /// Works out the Open Library IDs for one Goodreads-ID author, their books and the editions that matter.
    /// Read-only: <see cref="OpenLibraryMigrationService"/> applies the result.
    ///
    /// Strategy, from most to least reliable:
    /// <list type="number">
    /// <item>Books by ISBN: each local book's ISBNs are looked up; a hit also "votes" for an Open Library author.</item>
    /// <item>Author: the most voted Open Library author, otherwise an exact (normalised) name match.</item>
    /// <item>Remaining books by exact (normalised) title among the author's own works.</item>
    /// <item>Editions that have files, and the monitored one, by ISBN, otherwise the work's default edition, so
    /// that no file is ever left pointing to an edition the next refresh would delete.</item>
    /// </list>
    /// Anything uncertain is left on its Goodreads ID and explained in the report.
    /// </summary>
    public class OpenLibraryMigrationPlanner : IOpenLibraryMigrationPlanner
    {
        // ISBN lookups per book: enough to get past an edition Open Library lacks, without hammering the API.
        private const int MaxIsbnLookupsPerBook = 3;

        private readonly IOpenLibraryProxy _openLibrary;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly Logger _logger;

        public OpenLibraryMigrationPlanner(IOpenLibraryProxy openLibrary,
                                           IAuthorService authorService,
                                           IBookService bookService,
                                           IEditionService editionService,
                                           Logger logger)
        {
            _openLibrary = openLibrary;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _logger = logger;
        }

        public AuthorMigration PlanAuthor(LocalAuthor local)
        {
            var author = local.Author;
            var plan = new AuthorMigration
            {
                AuthorId = author.Id,
                AuthorMetadataId = author.AuthorMetadataId,
                Name = author.Name,
                OldId = author.ForeignAuthorId
            };

            var isbnHits = FindBooksByIsbn(local, out var votes);

            var olAuthor = PickAuthor(author.Name, votes, out var method);
            if (olAuthor == null)
            {
                plan.Problem = "No Open Library author matched (no ISBN hit, and no author with exactly this name).";
                plan.Books = local.Books.Select(b => Unmatched(local, b, "Author not matched.")).ToList();
                return plan;
            }

            if (_authorService.FindById(olAuthor) != null)
            {
                plan.Problem = $"{olAuthor} is already in the library as a separate author. Remove one of the two, then run the migration again.";
                plan.Books = local.Books.Select(b => Unmatched(local, b, "Author not migrated.")).ToList();
                return plan;
            }

            plan.NewId = olAuthor;
            plan.Method = method;

            // Only works whose primary author is this author: those are the ones Open Library-based refreshes
            // list under the author (see OpenLibraryProxy.GetAuthorInfo), so a book matched to anything else
            // would be dropped by the next refresh.
            var primaryWorks = _openLibrary.GetAuthorWorkDocs(olAuthor)
                .Where(w => OpenLibraryIds.FromKey(w.AuthorKey?.FirstOrDefault()) == olAuthor)
                .ToList();

            var usedWorks = new Dictionary<string, string>();

            // Books with files first, so that they win when two local books map to the same work.
            foreach (var book in local.Books.OrderByDescending(b => HasFiles(local, b)).ThenBy(b => b.Id))
            {
                plan.Books.Add(PlanBook(local, book, olAuthor, isbnHits.GetValueOrDefault(book.Id), primaryWorks, usedWorks));
            }

            plan.Books = plan.Books.OrderBy(b => b.Title).ToList();

            _logger.Debug("Open Library migration plan for {0}: {1} ({2}), {3}/{4} books matched", author.Name, olAuthor, method, plan.Books.Count(b => b.NewId != null), plan.Books.Count);

            return plan;
        }

        private Dictionary<int, OpenLibrarySearchDoc> FindBooksByIsbn(LocalAuthor local, out Dictionary<string, int> votes)
        {
            var hits = new Dictionary<int, OpenLibrarySearchDoc>();
            votes = new Dictionary<string, int>();

            foreach (var book in local.Books)
            {
                foreach (var isbn in CandidateIsbns(local, book))
                {
                    var doc = _openLibrary.SearchWorks($"isbn:{isbn}", 1).FirstOrDefault();
                    var authorKey = doc == null ? null : AuthorKeyNamed(doc, local.Author.Name);

                    if (authorKey == null)
                    {
                        continue;
                    }

                    hits[book.Id] = doc;
                    votes[authorKey] = votes.GetValueOrDefault(authorKey) + 1;
                    break;
                }
            }

            return hits;
        }

        /// <summary>ISBNs to try for a book: editions with files first, then the monitored one, then the rest.</summary>
        private static IEnumerable<string> CandidateIsbns(LocalAuthor local, Book book)
        {
            return local.EditionsOf(book)
                .OrderByDescending(e => local.EditionIdsWithFiles.Contains(e.Id))
                .ThenByDescending(e => e.Monitored)
                .Select(e => OpenLibraryValues.NormalizeIsbn(e.Isbn13))
                .Where(x => x != null)
                .Distinct()
                .Take(MaxIsbnLookupsPerBook);
        }

        /// <summary>The work's author key whose name matches the local author (works can have several authors).</summary>
        private static string AuthorKeyNamed(OpenLibrarySearchDoc doc, string localName)
        {
            var keys = doc.AuthorKey ?? new List<string>();
            var names = doc.AuthorName ?? new List<string>();

            for (var i = 0; i < keys.Count && i < names.Count; i++)
            {
                if (OpenLibraryMatching.NamesMatch(names[i], localName))
                {
                    return OpenLibraryIds.FromKey(keys[i]);
                }
            }

            return null;
        }

        private string PickAuthor(string name, Dictionary<string, int> votes, out MatchMethod method)
        {
            if (votes.Any())
            {
                method = MatchMethod.IsbnVotes;
                return votes.MaxBy(x => x.Value).Key;
            }

            method = MatchMethod.AuthorName;

            // Same name for several records (Open Library has duplicate authors): take the one with most works,
            // which is the one Open Library's own pages and search favour.
            var candidate = _openLibrary.SearchAuthors(name)
                .Where(c => OpenLibraryMatching.NamesMatch(c.Name, name) ||
                            (c.AlternateNames ?? new List<string>()).Any(n => OpenLibraryMatching.NamesMatch(n, name)))
                .MaxBy(c => c.WorkCount);

            return OpenLibraryIds.FromKey(candidate?.Key);
        }

        private BookMigration PlanBook(LocalAuthor local, Book book, string olAuthor, OpenLibrarySearchDoc isbnHit, List<OpenLibrarySearchDoc> primaryWorks, Dictionary<string, string> usedWorks)
        {
            var result = Unmatched(local, book, null);

            OpenLibrarySearchDoc doc = null;

            if (isbnHit != null && OpenLibraryIds.FromKey(isbnHit.AuthorKey?.FirstOrDefault()) == olAuthor)
            {
                doc = isbnHit;
                result.Method = MatchMethod.Isbn;
            }
            else
            {
                // Open Library sometimes has several works with the same title (e.g. a separate "French Edition"
                // work): prefer the most published one that no other local book has taken yet.
                var sameTitle = primaryWorks.Where(w => OpenLibraryMatching.TitlesMatch(w.Title, book.Title)).ToList();
                doc = sameTitle.Where(w => !usedWorks.ContainsKey(OpenLibraryIds.FromKey(w.Key))).MaxBy(w => w.EditionCount)
                      ?? sameTitle.MaxBy(w => w.EditionCount);
                result.Method = doc == null ? MatchMethod.None : MatchMethod.Title;
            }

            if (doc == null)
            {
                result.Problem = isbnHit != null
                    ? $"Found by ISBN ({isbnHit.Title}), but its primary author on Open Library is someone else."
                    : "No Open Library work with this title by this author.";
                return result;
            }

            var workId = OpenLibraryIds.FromKey(doc.Key);

            if (usedWorks.TryGetValue(workId, out var otherTitle))
            {
                result.Method = MatchMethod.None;
                result.Problem = $"Same Open Library work ({workId}) as '{otherTitle}': probably a duplicate in the library.";
                return result;
            }

            if (_bookService.FindById(workId) != null)
            {
                result.Method = MatchMethod.None;
                result.Problem = $"{workId} is already in the library.";
                return result;
            }

            result.MatchedTitle = doc.Title;

            if (!PlanEditions(local, book, workId, doc.Title, result))
            {
                result.Method = MatchMethod.None;
                result.MatchedTitle = null;
                return result;
            }

            result.NewId = workId;
            usedWorks[workId] = book.Title;

            return result;
        }

        /// <summary>
        /// Maps the editions that must survive (with files, or monitored). Returns false when an edition with files
        /// cannot be mapped: migrating the book would then orphan those files, so the book is left alone.
        /// </summary>
        private bool PlanEditions(LocalAuthor local, Book book, string workId, string workTitle, BookMigration result)
        {
            var localEditions = local.EditionsOf(book);
            var toMap = localEditions
                .Where(e => local.EditionIdsWithFiles.Contains(e.Id) || e.Monitored)
                .OrderByDescending(e => local.EditionIdsWithFiles.Contains(e.Id))
                .ThenByDescending(e => e.Monitored)
                .ToList();

            result.EditionsDropped = localEditions.Count - toMap.Count;

            if (!toMap.Any())
            {
                return true;
            }

            var olEditions = _openLibrary.GetWorkEditions(workId)
                .Select(x => OpenLibraryMapper.MapEdition(x, workTitle))
                .Where(x => x.ForeignEditionId != null)
                .ToList();

            // Fallback order: the edition Readarr would monitor by default, then the next most usable ones.
            var byPreference = olEditions.OrderByDescending(OpenLibraryMapper.EditionScore).ToList();
            var used = new HashSet<string>();

            // Pass 1: exact ISBN matches for every edition, so that a fallback in pass 2 can never take an
            // Open Library edition that is the exact match of another local edition.
            var targets = new Dictionary<int, (Edition Target, MatchMethod Method)>();

            foreach (var edition in toMap)
            {
                var isbn = OpenLibraryValues.NormalizeIsbn(edition.Isbn13);
                var target = isbn == null ? null : olEditions.FirstOrDefault(o => o.Isbn13 == isbn && IsFree(o.ForeignEditionId, used));

                if (target != null)
                {
                    used.Add(target.ForeignEditionId);
                    targets[edition.Id] = (target, MatchMethod.Isbn);
                }
            }

            // Pass 2: everything else gets the best edition still free (editions with files first).
            foreach (var edition in toMap)
            {
                var hasFiles = local.EditionIdsWithFiles.Contains(edition.Id);
                Edition target;
                MatchMethod method;

                if (targets.TryGetValue(edition.Id, out var exact))
                {
                    (target, method) = exact;
                }
                else
                {
                    target = byPreference.FirstOrDefault(o => IsFree(o.ForeignEditionId, used));
                    method = MatchMethod.DefaultEdition;
                }

                if (target == null)
                {
                    if (hasFiles)
                    {
                        result.Problem = $"No Open Library edition left for edition '{edition.Title}', which has files.";
                        result.Editions.Clear();
                        return false;
                    }

                    // A monitored edition without files can simply be replaced at the next refresh.
                    result.EditionsDropped++;
                    continue;
                }

                used.Add(target.ForeignEditionId);
                result.Editions.Add(new EditionMigration
                {
                    EditionId = edition.Id,
                    Title = edition.Title,
                    Isbn13 = edition.Isbn13,
                    OldId = edition.ForeignEditionId,
                    NewId = target.ForeignEditionId,
                    Method = method,
                    HasFiles = hasFiles
                });
            }

            return true;
        }

        /// <summary>Not yet picked for this book, and not already used by an edition in the database.</summary>
        private bool IsFree(string editionId, HashSet<string> used)
        {
            return !used.Contains(editionId) && _editionService.GetEditionByForeignEditionId(editionId) == null;
        }

        private static bool HasFiles(LocalAuthor local, Book book)
        {
            return local.EditionsOf(book).Any(e => local.EditionIdsWithFiles.Contains(e.Id));
        }

        private static BookMigration Unmatched(LocalAuthor local, Book book, string problem)
        {
            return new BookMigration
            {
                BookId = book.Id,
                Title = book.Title,
                OldId = book.ForeignBookId,
                HasFiles = HasFiles(local, book),
                Method = MatchMethod.None,
                Problem = problem
            };
        }
    }
}
