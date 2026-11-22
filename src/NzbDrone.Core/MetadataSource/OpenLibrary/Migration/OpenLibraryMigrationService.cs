using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Migration
{
    /// <summary>
    /// Runs <see cref="MigrateToOpenLibraryCommand"/>: plans every Goodreads-ID author (see
    /// <see cref="OpenLibraryMigrationPlanner"/>), writes a JSON report to the app data folder and, when asked to,
    /// rewrites the foreign IDs in place.
    ///
    /// Rewriting IDs in place (instead of deleting and re-adding) keeps everything attached to the database rows:
    /// files, history, monitoring, tags, quality profiles. A RefreshAuthor is then queued for each migrated author
    /// to pull Open Library's metadata.
    /// </summary>
    public class OpenLibraryMigrationService : IExecute<MigrateToOpenLibraryCommand>
    {
        private static readonly JsonSerializerOptions ReportOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly IOpenLibraryMigrationPlanner _planner;
        private readonly IAuthorService _authorService;
        private readonly IAuthorMetadataRepository _authorMetadataRepository;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public OpenLibraryMigrationService(IOpenLibraryMigrationPlanner planner,
                                           IAuthorService authorService,
                                           IAuthorMetadataRepository authorMetadataRepository,
                                           IBookService bookService,
                                           IEditionService editionService,
                                           IMediaFileService mediaFileService,
                                           IManageCommandQueue commandQueueManager,
                                           IAppFolderInfo appFolderInfo,
                                           IDiskProvider diskProvider,
                                           Logger logger)
        {
            _planner = planner;
            _authorService = authorService;
            _authorMetadataRepository = authorMetadataRepository;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _commandQueueManager = commandQueueManager;
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void Execute(MigrateToOpenLibraryCommand message)
        {
            var plan = Run(message.Apply);
            var reportPath = WriteReport(plan);

            _logger.ProgressInfo("Open Library migration {0}: {1}/{2} authors and {3}/{4} books matched. Report: {5}",
                plan.Applied ? "applied" : "planned (dry run)",
                plan.AuthorsMatched,
                plan.Authors.Count,
                plan.BooksMatched,
                plan.BooksTotal,
                reportPath);
        }

        public OpenLibraryMigrationPlan Run(bool apply)
        {
            var plan = new OpenLibraryMigrationPlan { Applied = apply };

            // Authors already on Open Library IDs are left alone, so the command can be re-run safely.
            var authors = _authorService.GetAllAuthors()
                .Where(a => !OpenLibraryIds.IsAuthorId(a.ForeignAuthorId))
                .OrderBy(a => a.Name)
                .ToList();

            for (var i = 0; i < authors.Count; i++)
            {
                var author = authors[i];
                _logger.ProgressInfo("Open Library migration: {0} ({1}/{2})", author.Name, i + 1, authors.Count);

                var local = Load(author);
                AuthorMigration authorPlan;

                try
                {
                    authorPlan = _planner.PlanAuthor(local);
                }
                catch (Exception ex)
                {
                    // Typically Open Library being unreachable: skip this author, the next run will retry it.
                    _logger.Warn(ex, "Could not plan the Open Library migration of {0}", author.Name);
                    authorPlan = new AuthorMigration
                    {
                        AuthorId = author.Id,
                        AuthorMetadataId = author.AuthorMetadataId,
                        Name = author.Name,
                        OldId = author.ForeignAuthorId,
                        Problem = $"Open Library error: {ex.Message}"
                    };
                }

                plan.Authors.Add(authorPlan);

                if (apply && authorPlan.NewId != null)
                {
                    ApplyAuthor(authorPlan, local);
                    _commandQueueManager.Push(new RefreshAuthorCommand(author.Id));
                }
            }

            return plan;
        }

        private LocalAuthor Load(Author author)
        {
            var books = _bookService.GetBooksByAuthor(author.Id);
            var editions = _editionService.GetEditionsByBook(books.Select(b => b.Id));

            return new LocalAuthor
            {
                Author = author,
                Books = books,
                EditionsByBook = editions.GroupBy(e => e.BookId).ToDictionary(g => g.Key, g => g.ToList()),
                EditionIdsWithFiles = _mediaFileService.GetFilesByAuthor(author.Id).Select(f => f.EditionId).ToHashSet()
            };
        }

        /// <summary>
        /// Rewrites foreign IDs: editions first, then books, then the author. If something fails midway, already
        /// rewritten entries are still consistent on their own, because MetadataSourceRouter routes each lookup on
        /// the ID it is given.
        /// </summary>
        private void ApplyAuthor(AuthorMigration plan, LocalAuthor local)
        {
            var editionsById = local.EditionsByBook.Values.SelectMany(x => x).ToDictionary(e => e.Id);
            var booksById = local.Books.ToDictionary(b => b.Id);

            var editions = new List<Edition>();
            var books = new List<Book>();

            foreach (var bookPlan in plan.Books.Where(b => b.NewId != null))
            {
                foreach (var editionPlan in bookPlan.Editions)
                {
                    var edition = editionsById[editionPlan.EditionId];
                    edition.ForeignEditionId = editionPlan.NewId;
                    edition.TitleSlug = editionPlan.NewId;
                    editions.Add(edition);
                }

                var book = booksById[bookPlan.BookId];
                book.ForeignBookId = bookPlan.NewId;
                book.TitleSlug = bookPlan.NewId;
                books.Add(book);
            }

            _editionService.UpdateMany(editions);
            _bookService.UpdateMany(books);

            var metadata = local.Author.Metadata.Value;
            metadata.ForeignAuthorId = plan.NewId;
            metadata.TitleSlug = plan.NewId;
            _authorMetadataRepository.Update(metadata);

            _logger.Info("Migrated {0} to Open Library ({1} → {2}): {3} books, {4} editions", plan.Name, plan.OldId, plan.NewId, books.Count, editions.Count);
        }

        private string WriteReport(OpenLibraryMigrationPlan plan)
        {
            var path = Path.Combine(_appFolderInfo.AppDataFolder, $"openlibrary-migration-{plan.CreatedAt:yyyyMMdd-HHmmss}{(plan.Applied ? string.Empty : "-dryrun")}.json");

            _diskProvider.WriteAllText(path, JsonSerializer.Serialize(plan, ReportOptions));

            return path;
        }
    }
}
