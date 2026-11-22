using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Migration
{
    /// <summary>
    /// Moves Goodreads-ID entries (authors, books, editions) to Open Library IDs.
    /// Dry run by default: it only writes a report. Send <c>{"name": "MigrateToOpenLibrary", "apply": true}</c>
    /// to POST /api/v1/command to change the database.
    /// </summary>
    public class MigrateToOpenLibraryCommand : Command
    {
        public bool Apply { get; set; }

        public override bool SendUpdatesToClient => true;

        public override string CompletionMessage => Apply ? "Migration to Open Library applied" : "Migration to Open Library planned (dry run)";
    }

    /// <summary>How an entry was matched. Serialised as-is in the report.</summary>
    public enum MatchMethod
    {
        None,
        Isbn,
        Title,
        AuthorName,
        IsbnVotes,
        DefaultEdition
    }

    /// <summary>Report + instructions for the whole library.</summary>
    public class OpenLibraryMigrationPlan
    {
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool Applied { get; set; }
        public List<AuthorMigration> Authors { get; set; } = new List<AuthorMigration>();

        public int AuthorsMatched => Authors.Count(x => x.NewId != null);
        public int BooksMatched => Authors.Sum(x => x.Books.Count(b => b.NewId != null));
        public int BooksTotal => Authors.Sum(x => x.Books.Count);
    }

    public class AuthorMigration
    {
        public int AuthorId { get; set; }
        public int AuthorMetadataId { get; set; }
        public string Name { get; set; }
        public string OldId { get; set; }

        /// <summary>Open Library author ID, or null when the author could not be matched (nothing is changed then).</summary>
        public string NewId { get; set; }
        public MatchMethod Method { get; set; }

        /// <summary>Human-readable reason when the author is skipped.</summary>
        public string Problem { get; set; }

        public List<BookMigration> Books { get; set; } = new List<BookMigration>();
    }

    public class BookMigration
    {
        public int BookId { get; set; }
        public string Title { get; set; }
        public string OldId { get; set; }
        public string NewId { get; set; }
        public string MatchedTitle { get; set; }
        public MatchMethod Method { get; set; }
        public bool HasFiles { get; set; }

        /// <summary>
        /// Why the book keeps its Goodreads ID. Once its author is migrated, the next refresh removes unmatched
        /// books that have no files; books with files are kept.
        /// </summary>
        public string Problem { get; set; }

        public List<EditionMigration> Editions { get; set; } = new List<EditionMigration>();

        /// <summary>Editions left unmapped (no files, not monitored): the next refresh replaces them with Open Library's.</summary>
        public int EditionsDropped { get; set; }
    }

    public class EditionMigration
    {
        public int EditionId { get; set; }
        public string Title { get; set; }
        public string Isbn13 { get; set; }
        public string OldId { get; set; }
        public string NewId { get; set; }
        public MatchMethod Method { get; set; }
        public bool HasFiles { get; set; }
    }
}
