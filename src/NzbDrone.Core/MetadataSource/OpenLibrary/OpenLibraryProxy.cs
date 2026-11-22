using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LazyCache;
using LazyCache.Providers;
using Microsoft.Extensions.Caching.Memory;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.MetadataSource.OpenLibrary
{
    /// <summary>
    /// Open Library metadata provider. Deliberately does NOT implement Readarr's public metadata
    /// interfaces (IProvideAuthorInfo, ...): those are implemented once, by <see cref="MetadataSourceRouter"/>,
    /// which decides per call whether Open Library or BookInfo should answer.
    /// </summary>
    public interface IOpenLibraryProxy
    {
        Author GetAuthorInfo(string authorId, bool useCache = true);
        Tuple<string, Book, List<AuthorMetadata>> GetBookInfo(string workId);
        List<Book> SearchForNewBook(string title, string author, bool getAllEditions = true);
        List<Book> SearchByIsbn(string isbn);
        List<Book> SearchByAsin(string asin);
        List<Book> SearchByGoodreadsBookId(int goodreadsId, bool getAllEditions);
    }

    public class OpenLibraryProxy : IOpenLibraryProxy
    {
        // Open Library asks API users to stay gentle. Readarr's rate limiter is per host and shared by
        // every request built from _requestBuilder, so this caps us at ~3 requests/second overall.
        private const double RateLimitSeconds = 0.35;

        // An author's works come from the search API, sorted by edition count. Very prolific (or very
        // messy) author records can list thousands of works; past this cap we keep the most published ones.
        private const int MaxAuthorWorks = 500;
        private const int AuthorWorksPageSize = 100;

        // Editions are fetched per work. 1000 is the largest page the editions endpoint accepts.
        private const int EditionsPageSize = 1000;
        private const int MaxEditionPages = 5;

        private const int SearchLimit = 20;
        private const int MaxRetries = 5;

        // Fields requested from /search.json. "editions" makes Open Library embed the single best
        // matching edition of each work, which is what search results show.
        private const string WorkFields = "key,title,author_key,author_name,first_publish_year,edition_count,ratings_average,ratings_count,subject,cover_i";
        private const string SearchFields = WorkFields + ",editions,editions.key,editions.title,editions.language,editions.isbn,editions.cover_i,editions.publisher,editions.publish_date";

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IHttpClient _httpClient;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly Logger _logger;
        private readonly IHttpRequestBuilderFactory _requestBuilder;
        private readonly CachingService _authorCache;

        public OpenLibraryProxy(IHttpClient httpClient,
                                IAuthorService authorService,
                                IBookService bookService,
                                IEditionService editionService,
                                Logger logger)
        {
            _httpClient = httpClient;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _logger = logger;

            _requestBuilder = new HttpRequestBuilder(OpenLibraryMapper.SiteUrl + "/{route}")
                .Accept(HttpAccept.Json)
                .WithRateLimit(RateLimitSeconds)
                .KeepAlive()
                .CreateFactory();

            // Refreshing a library calls GetAuthorInfo once per book of an author (RefreshBookService),
            // and an author payload costs one request per work. Keep recent authors in memory, like
            // BookInfoProxy does.
            _authorCache = new CachingService(new MemoryCacheProvider(new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 })));
        }

        // --- Authors -------------------------------------------------------------------------------
        public Author GetAuthorInfo(string authorId, bool useCache = true)
        {
            _logger.Debug("Getting author details from Open Library for {0}", authorId);

            if (!useCache)
            {
                return GetAuthorInfoUncached(authorId);
            }

            return _authorCache.GetOrAdd(authorId,
                () => GetAuthorInfoUncached(authorId),
                new LazyCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                    Size = 1,
                    ExpirationMode = ExpirationMode.ImmediateEviction
                });
        }

        private Author GetAuthorInfoUncached(string authorId)
        {
            var record = GetRecord<OpenLibraryAuthor>($"authors/{authorId}") ?? throw new AuthorNotFoundException(authorId);

            // A merged author answers with the surviving record; use its ID so Readarr picks up the merge.
            var resolvedId = OpenLibraryIds.FromKey(record.Key) ?? authorId;
            var metadata = OpenLibraryMapper.MapAuthorMetadata(resolvedId, record);

            var books = new List<Book>();

            foreach (var doc in GetAuthorWorks(resolvedId))
            {
                var workId = OpenLibraryIds.FromKey(doc.Key);

                // Same rule as BookInfo: a book belongs to the author only if they are its primary author
                // (works where they are a co-author, translator, illustrator... belong to someone else).
                if (workId == null || OpenLibraryIds.FromKey(doc.AuthorKey?.FirstOrDefault()) != resolvedId)
                {
                    continue;
                }

                var editions = GetEditions(workId).Select(x => OpenLibraryMapper.MapEdition(x, doc.Title)).ToList();

                // A Readarr book needs at least one edition (one of them is always monitored).
                if (!editions.Any())
                {
                    continue;
                }

                var book = OpenLibraryMapper.MapBook(workId,
                                                     doc.Title,
                                                     doc.Subject,
                                                     OpenLibraryValues.FromYear(doc.FirstPublishYear),
                                                     doc.RatingsAverage,
                                                     doc.RatingsCount,
                                                     editions);
                book.AuthorMetadata = metadata;
                books.Add(book);
            }

            metadata.Ratings = OpenLibraryMapper.AggregateRatings(books);

            _logger.Debug("Open Library author {0} ({1}): {2} books", metadata.Name, resolvedId, books.Count);

            return new Author
            {
                Metadata = metadata,
                CleanName = Parser.Parser.CleanAuthorName(metadata.Name),
                Books = books,
                Series = new List<Series>()
            };
        }

        /// <summary>All works whose author list contains the author, most published first.</summary>
        private List<OpenLibrarySearchDoc> GetAuthorWorks(string authorId)
        {
            var works = new List<OpenLibrarySearchDoc>();

            for (var page = 1; works.Count < MaxAuthorWorks; page++)
            {
                var result = GetJson<OpenLibrarySearchResult>("search.json", b => b
                    .AddQueryParam("q", $"author_key:{authorId}")
                    .AddQueryParam("fields", WorkFields)
                    .AddQueryParam("sort", "editions")
                    .AddQueryParam("limit", AuthorWorksPageSize)
                    .AddQueryParam("page", page));

                var docs = result?.Docs ?? new List<OpenLibrarySearchDoc>();
                works.AddRange(docs);

                if (docs.Count < AuthorWorksPageSize)
                {
                    break;
                }
            }

            return works.Take(MaxAuthorWorks).ToList();
        }

        // --- Works (Readarr books) -----------------------------------------------------------------
        public Tuple<string, Book, List<AuthorMetadata>> GetBookInfo(string workId)
        {
            var work = GetRecord<OpenLibraryWork>($"works/{workId}") ?? throw new BookNotFoundException(workId);
            var resolvedId = OpenLibraryIds.FromKey(work.Key) ?? workId;

            // The work record has no ratings or first-publish year; the search index does.
            var doc = GetJson<OpenLibrarySearchResult>("search.json", b => b
                .AddQueryParam("q", $"key:/works/{resolvedId}")
                .AddQueryParam("fields", WorkFields))?.Docs?.FirstOrDefault();

            var authorId = OpenLibraryIds.FromKey(work.Authors?.FirstOrDefault()?.Author?.Key)
                           ?? OpenLibraryIds.FromKey(doc?.AuthorKey?.FirstOrDefault());

            if (authorId == null)
            {
                // Readarr cannot store a book without an author.
                _logger.Warn("Open Library work {0} has no author, ignoring it", resolvedId);
                throw new BookNotFoundException(workId);
            }

            var authorRecord = GetRecord<OpenLibraryAuthor>($"authors/{authorId}") ?? throw new AuthorNotFoundException(authorId);
            var resolvedAuthorId = OpenLibraryIds.FromKey(authorRecord.Key) ?? authorId;
            var metadata = OpenLibraryMapper.MapAuthorMetadata(resolvedAuthorId, authorRecord);

            var editions = GetEditions(resolvedId).Select(x => OpenLibraryMapper.MapEdition(x, work.Title)).ToList();

            // Use the same sources as GetAuthorInfo (search doc first) so that refreshing a book and
            // refreshing its author produce identical data instead of flip-flopping between them.
            var book = OpenLibraryMapper.MapBook(resolvedId,
                                                 doc?.Title ?? work.Title,
                                                 doc?.Subject ?? work.Subjects,
                                                 OpenLibraryValues.FromYear(doc?.FirstPublishYear) ?? OpenLibraryValues.ParseDate(work.FirstPublishDate),
                                                 doc?.RatingsAverage,
                                                 doc?.RatingsCount,
                                                 editions);
            book.AuthorMetadata = metadata;

            return Tuple.Create(resolvedAuthorId, book, new List<AuthorMetadata> { metadata });
        }

        /// <summary>Every edition of a work (paged, capped at MaxEditionPages * EditionsPageSize).</summary>
        private List<OpenLibraryEdition> GetEditions(string workId)
        {
            var editions = new List<OpenLibraryEdition>();

            for (var page = 0; page < MaxEditionPages; page++)
            {
                var offset = page * EditionsPageSize;
                var result = GetJson<OpenLibraryEditionsPage>($"works/{workId}/editions.json", b => b
                    .AddQueryParam("limit", EditionsPageSize)
                    .AddQueryParam("offset", offset));

                var entries = result?.Entries ?? new List<OpenLibraryEdition>();
                editions.AddRange(entries.Where(x => !x.IsRedirect && !x.IsDeleted));

                if (entries.Count < EditionsPageSize || offset + entries.Count >= result.Size)
                {
                    break;
                }
            }

            return editions;
        }

        // --- Search --------------------------------------------------------------------------------
        public List<Book> SearchForNewBook(string title, string author, bool getAllEditions = true)
        {
            try
            {
                var term = title.Trim();

                // Prefixed lookups, mirroring the ones BookInfoProxy supported: author:/work:/edition:/isbn:/asin:
                var split = term.Split(':', 2);
                if (split.Length == 2)
                {
                    var prefix = split[0].Trim().ToLowerInvariant();
                    var value = split[1].Trim();

                    switch (prefix)
                    {
                        case "isbn":
                            return SearchByIsbn(value);
                        case "asin":
                            return SearchByAsin(value);
                        case "edition":
                            // edition:<digits> is a Goodreads edition ID, which Open Library can sometimes map.
                            return int.TryParse(value, out var goodreadsId)
                                ? SearchByGoodreadsBookId(goodreadsId, getAllEditions)
                                : SearchById(value, getAllEditions);
                        case "author":
                        case "work":
                        case "openlibrary":
                            // Goodreads author/work IDs cannot be mapped to Open Library: SearchById returns nothing for them.
                            return SearchById(value, getAllEditions);
                    }
                }

                // A bare Open Library ID typed in the search box.
                if (OpenLibraryIds.FromKey(term) != null)
                {
                    return SearchById(term, getAllEditions);
                }

                var query = author.IsNullOrWhiteSpace() ? term : $"{term} {author}";

                return Search(query);
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex, ex.Message);
                throw new OpenLibraryException("Search for '{0}' failed. Unable to communicate with Open Library.", ex, title);
            }
            catch (Exception ex) when (ex is not OpenLibraryException and not NzbDroneClientException)
            {
                _logger.Warn(ex, ex.Message);
                throw new OpenLibraryException("Search for '{0}' failed. Invalid response received from Open Library.", ex, title);
            }
        }

        public List<Book> SearchByIsbn(string isbn)
        {
            var normalized = OpenLibraryValues.NormalizeIsbn(isbn);

            return normalized == null ? new List<Book>() : SearchSafely($"isbn:{normalized}");
        }

        public List<Book> SearchByAsin(string asin)
        {
            return asin.IsNullOrWhiteSpace() ? new List<Book>() : SearchSafely($"id_amazon:{asin.Trim()}");
        }

        /// <summary>
        /// Open Library records Goodreads edition IDs for some editions (mostly older ones), which lets
        /// files tagged with a Goodreads ID still be identified.
        /// </summary>
        public List<Book> SearchByGoodreadsBookId(int goodreadsId, bool getAllEditions)
        {
            return SearchSafely($"id_goodreads:{goodreadsId}");
        }

        /// <summary>Import identification must never fail because of a metadata hiccup: return nothing instead.</summary>
        private List<Book> SearchSafely(string query)
        {
            try
            {
                return Search(query);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Open Library search for '{0}' failed", query);
                return new List<Book>();
            }
        }

        private List<Book> Search(string query)
        {
            var result = GetJson<OpenLibrarySearchResult>("search.json", b => b
                .AddQueryParam("q", query)
                .AddQueryParam("fields", SearchFields)
                .AddQueryParam("limit", SearchLimit));

            var books = (result?.Docs ?? new List<OpenLibrarySearchDoc>())
                .Select(OpenLibraryMapper.MapSearchDoc)
                .Where(x => x != null)
                .ToList();

            books.ForEach(LinkToLibrary);

            return books;
        }

        private List<Book> SearchById(string rawId, bool getAllEditions)
        {
            var id = OpenLibraryIds.FromKey(rawId);

            if (id == null)
            {
                // Not an Open Library ID (e.g. a Goodreads author or work ID): nothing to look up.
                return new List<Book>();
            }

            try
            {
                if (OpenLibraryIds.IsAuthorId(id))
                {
                    var books = GetAuthorInfo(id).Books.Value;
                    books.ForEach(LinkToLibrary);
                    return books;
                }

                if (OpenLibraryIds.IsWorkId(id))
                {
                    var book = GetBookInfo(id).Item2;
                    LinkToLibrary(book);
                    return new List<Book> { book };
                }

                return SearchByEditionId(id, getAllEditions);
            }
            catch (AuthorNotFoundException)
            {
                return new List<Book>();
            }
            catch (BookNotFoundException)
            {
                return new List<Book>();
            }
        }

        private List<Book> SearchByEditionId(string editionId, bool getAllEditions)
        {
            var record = GetRecord<OpenLibraryEdition>($"books/{editionId}");
            var workId = OpenLibraryIds.FromKey(record?.Works?.FirstOrDefault()?.Key);

            if (workId == null)
            {
                return new List<Book>();
            }

            var book = GetBookInfo(workId).Item2;
            var editions = book.Editions.Value;
            var requested = editions.SingleOrDefault(x => x.ForeignEditionId == OpenLibraryIds.FromKey(record.Key));

            if (requested == null)
            {
                return new List<Book>();
            }

            // The user asked for this exact edition: monitor it rather than our default pick.
            editions.ForEach(x => x.Monitored = false);
            requested.Monitored = true;

            if (!getAllEditions)
            {
                book.Editions = new List<Edition> { requested };
            }

            LinkToLibrary(book);

            return new List<Book> { book };
        }

        /// <summary>
        /// Attaches search results to what is already in the library: existing book (keeps its DB fields and
        /// monitored edition) and existing author, or a new, unsaved Author built from the result's metadata.
        /// Same logic as BookInfoProxy.AddDbIds.
        /// </summary>
        private void LinkToLibrary(Book book)
        {
            var dbBook = _bookService.FindById(book.ForeignBookId);
            if (dbBook != null)
            {
                book.UseDbFieldsFrom(dbBook);

                var dbEditions = _editionService.GetEditionsByBook(dbBook.Id).ToDictionary(x => x.ForeignEditionId);

                foreach (var edition in book.Editions.Value)
                {
                    edition.Monitored = false;
                    if (dbEditions.TryGetValue(edition.ForeignEditionId, out var dbEdition))
                    {
                        edition.UseDbFieldsFrom(dbEdition);
                    }
                }

                if (book.Editions.Value.Any() && !book.Editions.Value.Any(x => x.Monitored))
                {
                    OpenLibraryMapper.ChooseMonitoredEdition(book.Editions.Value);
                }
            }

            var metadata = book.AuthorMetadata.Value;
            var author = _authorService.FindById(metadata.ForeignAuthorId) ?? new Author
            {
                CleanName = Parser.Parser.CleanAuthorName(metadata.Name),
                Metadata = metadata
            };

            book.Author = author;
            book.AuthorMetadata = author.Metadata.Value;
            book.AuthorMetadataId = author.AuthorMetadataId;
        }

        /// <summary>
        /// Fetches <c>/{path}.json</c>, following Open Library's in-band redirects (merged records).
        /// Returns null when the record does not exist or was deleted.
        /// </summary>
        private T GetRecord<T>(string path)
            where T : OpenLibraryRecord
        {
            for (var hop = 0; hop < 4; hop++)
            {
                var record = GetJson<T>($"{path}.json");

                if (record == null || record.IsDeleted)
                {
                    return null;
                }

                if (!record.IsRedirect)
                {
                    return record;
                }

                _logger.Debug("Open Library {0} was merged into {1}", path, record.Location);
                path = record.Location.Trim('/');
            }

            throw new OpenLibraryException("Too many redirects resolving {0}", path);
        }

        /// <summary>
        /// GET a JSON document. Returns null on 404 and throws on other HTTP errors.
        /// Retries on 429/503 and on transient network failures: openlibrary.org regularly drops or stalls
        /// connections, and loading one author takes one request per work, so a single hiccup must not
        /// fail the whole author.
        /// </summary>
        private T GetJson<T>(string route, Action<HttpRequestBuilder> configure = null)
            where T : class
        {
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                var builder = _requestBuilder.Create().SetSegment("route", route);
                configure?.Invoke(builder);

                var request = builder.Build();
                request.AllowAutoRedirect = true;
                request.SuppressHttpError = true;

                HttpResponse response;

                try
                {
                    response = _httpClient.Get(request);
                }
                catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(2 * attempt);
                    _logger.Debug(ex, "Open Library request to {0} failed (attempt {1}/{2}), retrying in {3}s", route, attempt, MaxRetries, delay.TotalSeconds);
                    Thread.Sleep(delay);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    WaitBeforeRetry(response, attempt);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.HasHttpError)
                {
                    throw new OpenLibraryException("Open Library returned {0} for {1}", (int)response.StatusCode, request.Url);
                }

                return JsonSerializer.Deserialize<T>(response.Content, SerializerOptions);
            }

            throw new OpenLibraryException("Open Library is rate limiting us, gave up on {0}", route);
        }

        /// <summary>Connection refused/reset/timed out: worth retrying, unlike HTTP-level errors.</summary>
        private static bool IsTransientNetworkError(Exception ex)
        {
            return ex is System.Net.Http.HttpRequestException
                       or WebException
                       or System.IO.IOException
                       or TaskCanceledException
                       or System.Net.Sockets.SocketException;
        }

        private void WaitBeforeRetry(HttpResponse response, int attempt)
        {
            // Honour Retry-After when present, otherwise back off linearly (5s, 10s, ...).
            var seconds = 5 * attempt;

            if (response.Headers.ContainsKey("Retry-After") && int.TryParse(response.Headers["Retry-After"], out var retryAfter))
            {
                seconds = Math.Clamp(retryAfter, 1, 120);
            }

            _logger.Info("Open Library returned {0}, backing off for {1}s", (int)response.StatusCode, seconds);
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }
    }
}
