using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.OpenLibrary;

namespace NzbDrone.Core.MetadataSource
{
    /// <summary>
    /// The single implementation of Readarr's metadata interfaces. It sends each call to one of two providers:
    ///
    /// <list type="bullet">
    /// <item><b>BookInfo</b> (<see cref="IBookInfoProxy"/>): Goodreads-ID based. The official server is gone,
    /// but compatible servers exist (e.g. rreading-glasses) and are configured through
    /// Settings → Development → Metadata Source.</item>
    /// <item><b>Open Library</b> (<see cref="IOpenLibraryProxy"/>): uses Open Library IDs (OLxxxA/W/M),
    /// queried directly from openlibrary.org, so no metadata server is needed.</item>
    /// </list>
    ///
    /// Rules:
    /// <list type="number">
    /// <item><b>Lookups by ID follow the ID.</b> Open Library IDs go to Open Library and anything else
    /// (numeric Goodreads IDs) goes to BookInfo. A library can therefore hold both kinds of entries,
    /// which is what makes a gradual migration from Goodreads to Open Library IDs possible.</item>
    /// <item><b>New lookups (search) go to BookInfo only when a custom Metadata Source is configured</b>,
    /// so existing rreading-glasses setups behave exactly as before. Otherwise Open Library answers.</item>
    /// </list>
    /// </summary>
    public class MetadataSourceRouter : IProvideAuthorInfo, IProvideBookInfo, ISearchForNewBook, ISearchForNewAuthor, ISearchForNewEntity
    {
        private readonly IConfigService _configService;
        private readonly IBookInfoProxy _bookInfo;
        private readonly IOpenLibraryProxy _openLibrary;

        public MetadataSourceRouter(IConfigService configService, IBookInfoProxy bookInfo, IOpenLibraryProxy openLibrary)
        {
            _configService = configService;
            _bookInfo = bookInfo;
            _openLibrary = openLibrary;
        }

        /// <summary>True when the user pointed Readarr at a BookInfo-compatible server.</summary>
        public bool UseBookInfoForSearch => _configService.MetadataSource.IsNotNullOrWhiteSpace();

        // --- Lookups by ID: routed on the ID format ------------------------------------------------
        public Author GetAuthorInfo(string readarrId, bool useCache = true)
        {
            return OpenLibraryIds.IsAuthorId(readarrId)
                ? _openLibrary.GetAuthorInfo(readarrId, useCache)
                : _bookInfo.GetAuthorInfo(readarrId, useCache);
        }

        public Tuple<string, Book, List<AuthorMetadata>> GetBookInfo(string id)
        {
            return OpenLibraryIds.IsWorkId(id)
                ? _openLibrary.GetBookInfo(id)
                : _bookInfo.GetBookInfo(id);
        }

        /// <summary>
        /// Only BookInfo servers publish a "changed since" feed. Returning null tells RefreshAuthorService
        /// it has no such information, so it falls back to its usual "should this author be refreshed" rule.
        /// Note: when a custom source is set, authors are only refreshed if that feed lists them, so Open
        /// Library authors in a mixed library rely on manual or per-author refreshes in that setup.
        /// </summary>
        public HashSet<string> GetChangedAuthors(DateTime startTime)
        {
            return UseBookInfoForSearch ? _bookInfo.GetChangedAuthors(startTime) : null;
        }

        // --- New lookups: routed on configuration --------------------------------------------------
        public List<Book> SearchForNewBook(string title, string author, bool getAllEditions = true)
        {
            return UseBookInfoForSearch
                ? _bookInfo.SearchForNewBook(title, author, getAllEditions)
                : _openLibrary.SearchForNewBook(title, author, getAllEditions);
        }

        public List<Book> SearchByIsbn(string isbn)
        {
            return UseBookInfoForSearch ? _bookInfo.SearchByIsbn(isbn) : _openLibrary.SearchByIsbn(isbn);
        }

        public List<Book> SearchByAsin(string asin)
        {
            return UseBookInfoForSearch ? _bookInfo.SearchByAsin(asin) : _openLibrary.SearchByAsin(asin);
        }

        public List<Book> SearchByGoodreadsBookId(int goodreadsId, bool getAllEditions)
        {
            return UseBookInfoForSearch
                ? _bookInfo.SearchByGoodreadsBookId(goodreadsId, getAllEditions)
                : _openLibrary.SearchByGoodreadsBookId(goodreadsId, getAllEditions);
        }

        public List<Author> SearchForNewAuthor(string title)
        {
            if (UseBookInfoForSearch)
            {
                return _bookInfo.SearchForNewAuthor(title);
            }

            return _openLibrary.SearchForNewBook(title, null)
                .Select(x => x.Author.Value)
                .DistinctBy(x => x.ForeignAuthorId)
                .ToList();
        }

        /// <summary>Mixed author/book results, each author listed right before its first book (same shape as BookInfo).</summary>
        public List<object> SearchForNewEntity(string title)
        {
            if (UseBookInfoForSearch)
            {
                return _bookInfo.SearchForNewEntity(title);
            }

            var result = new List<object>();
            var seenAuthors = new HashSet<string>();

            foreach (var book in _openLibrary.SearchForNewBook(title, null, false))
            {
                var author = book.Author.Value;

                if (seenAuthors.Add(author.ForeignAuthorId))
                {
                    result.Add(author);
                }

                result.Add(book);
            }

            return result;
        }
    }
}
