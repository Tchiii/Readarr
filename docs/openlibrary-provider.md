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
  Readarr displays a book's rating from its selected edition, because Goodreads rated each edition, so every
  edition carries its work's rating. Without that, the UI showed 0 everywhere. The author rating is the
  vote-weighted average of their books, since Open Library does not rate authors.
- **Covers.** Covers come from `covers.openlibrary.org`. Older covers redirect to archive.org, which
  Readarr's search-result cover proxy did not follow, so it returned an empty image. It now follows
  redirects (`MediaCoverProxy`) and answers 404 for missing images (authors without a photo) instead of
  logging a fatal error.
- **Languages.** Edition languages are ISO 639-2 codes (`eng`, `fre`, …). The default metadata profile
  only allows `eng, null`, so non-English users must add their language (e.g. `fre`) to keep those editions.
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

## Migrating a Goodreads library

The `MigrateToOpenLibrary` command moves Goodreads-ID entries to Open Library IDs. It is a **dry run by
default**: it only writes a report.

```
POST /api/v1/command  {"name": "MigrateToOpenLibrary"}                  # dry run: report only
POST /api/v1/command  {"name": "MigrateToOpenLibrary", "apply": true}   # change the database
```

The report is written to the app data folder as `openlibrary-migration-<timestamp>[-dryrun].json`. It
lists every author, book and edition with the old ID, the new ID, how each was matched and, for anything
left unchanged, why.

### How entries are matched

From most to least reliable (`OpenLibraryMigrationPlanner`):

1. **Books by ISBN.** Up to 3 ISBNs per book are looked up, starting with editions that have files. Each
   hit votes for the Open Library author whose name matches.
2. **Author.** The author with the most votes wins. Without votes, the author must have exactly the same
   normalised name (accents, case, punctuation and word order ignored; the record with most works wins).
3. **Remaining books by title** among the author's own works (primary author only). Titles are compared
   without trailing series parentheses or subtitles. When several works share a title, the most published
   one that is not yet taken wins.
4. **Editions with files, and the monitored edition**, by ISBN first (across all editions, so a fallback
   never takes another edition's exact match), otherwise the best remaining edition. A book whose
   file-carrying edition cannot be mapped is **not migrated**, so no file is ever orphaned.

Matching is deliberately strict: an uncertain entry keeps its Goodreads ID, because a wrong match would
attach the wrong metadata to someone's files.

### What applying does

- Rewrites `ForeignAuthorId`, `ForeignBookId` and `ForeignEditionId` **in place**. The database rows stay
  the same, so files, history, monitoring, tags and profiles stay attached.
- Queues a `RefreshAuthor` for each migrated author to pull Open Library's metadata. That refresh replaces
  the unmapped Goodreads editions with Open Library's, and **removes unmatched books that have no files**,
  like any book that disappears from the metadata. Unmatched books with files are kept.
- Authors already on Open Library IDs are skipped, so the command can be run again, for example after
  Open Library was unreachable for some authors.
- Conflicts are reported and left alone: an Open Library author or work that is already in the library,
  or two local books mapping to the same work.

### Test on a simulated Goodreads library

A copy of a real library (3 authors, 11 books, 125 editions) had all its IDs replaced with numeric
Goodreads-style ones and all of Alain Damasio's ISBNs removed. A fake file was attached to an
unmonitored edition with no ISBN, and another to a monitored edition. Applying the migration in the
running app gave:

- 3/3 authors and **11/11 books back on their exact original Open Library IDs**, with 0 wrong matches.
  Damasio was matched by name and title, the others by ISBN.
- Both files still attached to their (same) edition rows, which now carry valid Open Library IDs.
- After the refresh, no edition left on a numeric ID, and no errors in the log.

## Known limitations

| Limitation | Impact | Possible fix |
|---|---|---|
| Unrated Open Library books are hidden by any positive `MinPopularity` (see below) | 🟠 A real but unrated book needs `MinPopularity = 0` to show | Better popularity signal (edition count, reading-log count) |
| No series | 🔴 Series pages are empty | Open Library has no series entity. Parse the edition `series` field, or use Hardcover |
| Prolific authors are slow to add | 🟠 One request per work at 3 req/s: about 45 s for Frank Herbert (131 works) | Fetch editions lazily, or share a cache server |
| At most 500 works per author and 5,000 editions per work | 🟢 Rare. Past the cap the most-published works are kept | — |
| Mixed library + custom Metadata Source: Open Library authors are not in the BookInfo "changed" feed | 🟢 They are only refreshed manually in that setup | Merge both strategies in the router |
| Migration only matches what Open Library knows | 🟠 Unmatched books stay on Goodreads IDs; those without files are removed at the next refresh | Check the dry-run report first; add missing data to Open Library |

## Tests

Checked end to end in the running app (search, add author, add a single book, refresh, author and
search pages in a browser) with Tara Westover, Andy Weir (6 books in 12 s: the real bibliography,
no junk) and Alain Damasio.

Open Library data quality is uneven. Some works are duplicated (e.g. "La Zone du Dehors" and
"La Zone Du Dehors (French Edition)" are two separate works), and dates are often only a year.

- `OpenLibraryValuesFixture`, `OpenLibraryMapperFixture`, `MetadataSourceRouterFixture`: unit tests, no network.
- `OpenLibraryProxyFixture`: `[IntegrationTest]`, talks to the real openlibrary.org:

```
DOTNET_ROLL_FORWARD=Major dotnet test _tests/net6.0/Readarr.Core.Test.dll --filter "FullyQualifiedName~OpenLibrary|FullyQualifiedName~MetadataSourceRouter"
```
