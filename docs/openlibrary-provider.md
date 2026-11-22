# Open Library metadata provider

Readarr can get its metadata straight from [Open Library](https://openlibrary.org) instead of a
BookInfo server. No metadata server needs to be hosted: each Readarr instance queries
openlibrary.org directly.

For the BookInfo (Goodreads ID) contract this replaces, see [metadata-api.md](metadata-api.md).

## Why Open Library IDs and not Goodreads IDs

Existing libraries store Goodreads IDs. A coverage study on 50 books (classics, recent bestsellers,
SFF series, French titles, non-fiction) showed that Open Library knows the books, but not their
Goodreads IDs:

| Lookup | Found |
|---|---|
| Goodreads edition ID → Open Library (`id_goodreads`) | **5 / 50** (0 / 10 for recent bestsellers and non-fiction) |
| Title + author → Open Library work | **50 / 50**, all of them the correct work |

Mapping Goodreads IDs to Open Library on the fly is therefore not viable, and keeping Goodreads
as the source of IDs would mean scraping Goodreads. Readarr uses Open Library IDs instead:

| Readarr | Open Library | Example |
|---|---|---|
| Author (`ForeignAuthorId`) | author | `OL79034A` |
| Book (`ForeignBookId`) | work | `OL893414W` |
| Edition (`ForeignEditionId`) | edition | `OL7500941M` |

The `Foreign*Id` columns are already strings, so no schema change is needed.

## Architecture

```
AddAuthorService, RefreshBookService, CandidateService, BookLookupController, ...
        │  IProvideAuthorInfo / IProvideBookInfo / ISearchForNewBook / ISearchForNewAuthor / ISearchForNewEntity
        ▼
MetadataSourceRouter  ── the only class implementing those interfaces
        ├─► IOpenLibraryProxy → OpenLibraryProxy → openlibrary.org (JSON API)
        └─► IBookInfoProxy    → BookInfoProxy    → BookInfo server (Goodreads IDs)
```

| File | Role |
|---|---|
| [`MetadataSourceRouter.cs`](../src/NzbDrone.Core/MetadataSource/MetadataSourceRouter.cs) | Picks the provider for each call |
| [`OpenLibrary/OpenLibraryProxy.cs`](../src/NzbDrone.Core/MetadataSource/OpenLibrary/OpenLibraryProxy.cs) | HTTP, rate limiting, retries, redirects, search |
| [`OpenLibrary/OpenLibraryMapper.cs`](../src/NzbDrone.Core/MetadataSource/OpenLibrary/OpenLibraryMapper.cs) | Pure mapping to Readarr's model (unit tested) |
| [`OpenLibrary/OpenLibraryValues.cs`](../src/NzbDrone.Core/MetadataSource/OpenLibrary/OpenLibraryValues.cs) | ID, date, ISBN, language and subject parsing |
| [`OpenLibrary/Resources/`](../src/NzbDrone.Core/MetadataSource/OpenLibrary/Resources/) | Open Library JSON DTOs |

The providers deliberately do not implement the public interfaces themselves. Readarr's DI container
registers every implementation of an interface, so two implementations would make resolution
ambiguous.

### Routing rules

1. **Lookups by ID follow the ID.** `OL…A` / `OL…W` go to Open Library, anything else (numeric
   Goodreads IDs) goes to BookInfo. A library can hold both kinds of entries at once, which is
   what makes a gradual migration possible.
2. **New lookups (search, ISBN/ASIN identification) go to BookInfo only when a custom
   Metadata Source is set** (*Settings → Development*). Existing rreading-glasses setups therefore
   behave exactly as before. With no custom source, Open Library is used.
3. `GetChangedAuthors` only exists on BookInfo servers. With Open Library it returns `null`, so
   Readarr falls back to its normal "should this author be refreshed" rule.

## Requests made

| Readarr call | Open Library requests |
|---|---|
| Author (`GetAuthorInfo`) | `/authors/{id}.json` + `/search.json?q=author_key:{id}` (1 per 100 works) + **1 `/works/{id}/editions.json` per work** |
| Book (`GetBookInfo`) | `/works/{id}.json` + `/search.json?q=key:/works/{id}` (ratings, year) + editions + `/authors/{id}.json` |
| Search | 1 `/search.json` (each hit carries its best edition) |
| `edition:OL…M` | `/books/{id}.json` + a book lookup |

- Every request goes through Readarr's per-host rate limiter: **at most ~3 requests/second**
  (`RateLimitSeconds = 0.35`).
- `429` / `503` responses: waits `Retry-After` (or 5 s, 10 s, …) and retries, up to 5 times.
- **Transient network errors** (connection reset or timed out) are retried with a 2 s, 4 s, …
  backoff. openlibrary.org drops connections regularly, and an author needs dozens of requests.
- Merged records (`type: /type/redirect`) are followed, and the surviving ID is returned, so Readarr
  sees the merge. Deleted records (`/type/delete`) are treated as not found.
- Fetched authors are kept in memory for 10 minutes. Refreshing a library asks for the same author
  once per book.

## Mapping notes

- **Primary author** = first author of the work. As with BookInfo, an author's page only lists the
  works where they are the primary author.
- **Monitored edition.** Open Library has no per-edition ratings, so the default edition is the most
  usable one: ISBN/ASIN > English > has a cover > has a page count > has a date
  (`OpenLibraryMapper.EditionScore`). When a user picks an edition (`edition:OL…M`), that one is monitored.
- **Dates.** `publish_date` is free text ("August 1, 1978", "c1978", "[1978?]"). A year alone maps to
  1 January, like Goodreads did. A book's date is the work's `first_publish_year`, otherwise its
  earliest edition date.
- **Ratings.** These are work-level ratings from the search index (`ratings_average`, `ratings_count`).
  Editions are unrated.
- **Metadata profile popularity.** `MinPopularity` (rating × votes, default 350) is calibrated on
  Goodreads (*Dune*: 1.7 M votes, against 464 on Open Library). Applied as-is, it kept 2 of Frank
  Herbert's 131 works and 0 of Tara Westover's. For Open Library books, any positive threshold
  therefore means **"has at least one rating"** (`MetadataProfileService.BookAllowedByRating`). Unrated
  works, which are often duplicates or junk records, are still filtered out, and unreleased books are
  always kept. `MinPopularity = 0` keeps everything. Goodreads books are unchanged.

  | Author | Works kept before | Works kept after |
  |---|---|---|
  | Frank Herbert | 2 / 131 | 34 / 131 |
  | Andy Weir | 3 / 14 | 7 / 14 |
  | Tara Westover | 0 / 1 | 1 / 1 |
  | Brandon Sanderson | 8 / 217 | 76 / 217 |
- **Genres.** These are Open Library subjects minus machine tags (`nyt:…`, `award:…`), lower-cased, at most 10.
- **Search results are light.** Each hit carries a single edition. `AddBookService` fetches the full work
  when the book is added and looks the chosen edition up in it.
- **Goodreads IDs in search.** `edition:{digits}` is looked up through Open Library's `id_goodreads`
  (old editions only). `author:{digits}` and `work:{digits}` cannot be mapped and return nothing.

## Known limitations

| Limitation | Impact | Possible fix |
|---|---|---|
| Unrated Open Library books are hidden by any positive `MinPopularity` (see below) | 🟠 A real but unrated book needs `MinPopularity = 0` to show | Better popularity signal (edition count, reading-log count) |
| No series | 🔴 Series pages are empty | Open Library has no series entity. Parse the edition `series` field, or use Hardcover |
| Prolific authors are slow to add | 🟠 One request per work at 3 req/s: about 45 s for Frank Herbert (131 works) | Fetch editions lazily, or share a cache server |
| At most 500 works per author and 5,000 editions per work | 🟢 Rare. Past the cap the most-published works are kept | — |
| Mixed library + custom Metadata Source: Open Library authors are not in the BookInfo "changed" feed | 🟢 They are only refreshed manually in that setup | Merge both strategies in the router |
| No migration of existing Goodreads libraries yet | 🟠 Existing entries stay on BookInfo | Migration tool: ISBN, then title + author matching |

## Tests

- `OpenLibraryValuesFixture`, `OpenLibraryMapperFixture`, `MetadataSourceRouterFixture`: unit tests, no network.
- `OpenLibraryProxyFixture`: `[IntegrationTest]`, talks to the real openlibrary.org:

```
DOTNET_ROLL_FORWARD=Major dotnet test _tests/net6.0/Readarr.Core.Test.dll --filter "FullyQualifiedName~OpenLibrary|FullyQualifiedName~MetadataSourceRouter"
```
