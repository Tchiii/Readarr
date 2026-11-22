# Metadata API contract (BookInfo)

> Readarr can now also use Open Library directly, without any metadata server.
> See [openlibrary-provider.md](openlibrary-provider.md).

This document describes the HTTP contract that Readarr expects from its metadata server.
The official server (`https://api.bookinfo.club/v1/`) is offline, so any replacement must
implement exactly this contract for Readarr to work without code changes.

Everything below is derived from the client code, mainly
[`BookInfoProxy.cs`](../src/NzbDrone.Core/MetadataSource/BookInfo/BookInfoProxy.cs) and the
resource classes in [`BookInfoResource/`](../src/NzbDrone.Core/MetadataSource/BookInfo/BookInfoResource/).

## 1. Overview

### Base URL

The URL is built as `{base}/{route}`:

- default: `https://api.bookinfo.club/v1/` (hardcoded in
  [`ReadarrCloudRequestBuilder.cs`](../src/NzbDrone.Common/Cloud/ReadarrCloudRequestBuilder.cs));
- override: the **Metadata Source** field in *Settings → Development* (config key `MetadataSource`).
  A trailing `/` is trimmed and `/{route}` is appended.

### Endpoints

| Method | Route | Purpose | Expected success |
|---|---|---|---|
| `GET` | `author/{authorId}` | Author with all their works, editions and series | `200` + `AuthorResource` |
| `GET` | `work/{workId}` | One work with its editions | `200` + `WorkResource`, or a redirect to an author |
| `GET` | `book/{editionId}` | Resolve an edition to its work or author | **redirect** (never `200`) |
| `POST` | `book/bulk` | Several editions at once (search results) | `200` + `BulkBookResource` |
| `GET` | `author/changed?since=…` | Authors changed since a date (refresh optimisation) | `200` + `RecentUpdatesResource` |

### Identifiers: the main constraint

All IDs are **integers, and they are Goodreads IDs**:

- `authorId` = Goodreads author ID (e.g. `58` = Frank Herbert);
- `workId` = Goodreads work ID (e.g. `3634639` = *Dune*);
- `editionId` = Goodreads book/edition ID (e.g. `44767458`).

Every existing Readarr database stores these IDs (`ForeignAuthorId`, `ForeignBookId`,
`ForeignEditionId`), and search returns Goodreads IDs (see §4). A replacement server must
therefore **accept Goodreads IDs as input**, even if its data comes from elsewhere
(Open Library, Hardcover, …). A server that uses its own ID scheme would break existing
libraries and search.

### JSON conventions

- **Property names are PascalCase and case-sensitive** (`ForeignId`, not `foreignId`) for
  `author/*` and `work/*`. These are parsed with `System.Text.Json`, `PropertyNameCaseInsensitive = false`,
  and no naming policy. A camelCase response deserialises to empty objects **without any error**.
- `book/bulk` and `author/changed` are parsed with Newtonsoft.Json (case-insensitive), but
  PascalCase should be used everywhere for consistency.
- **Dates**: ISO 8601 strings (e.g. `"1965-08-01T00:00:00Z"`). They are parsed with `DateTime.Parse`
  and then converted to UTC. All dates may be `null`.
- Unknown properties are ignored.

### Common status codes

| Status | Client behaviour |
|---|---|
| `429 Too Many Requests` | Waits `Retry-After` seconds (integer; defaults to 5 if missing or not an integer), then retries. Applies to every endpoint except `author/changed`. |
| `404 Not Found` | `AuthorNotFoundException` / `BookNotFoundException` / `EditionNotFoundException`, depending on the endpoint |
| `400 Bad Request` | `BadRequestException` (`author/*`, `work/*`) |
| other errors | Generic `BookInfoException` |

### "Still generating" responses (polling)

The original server could reply `200` with a stub while it built the data in the background:

- `author/{id}`: if `Works` is `null`, the client waits 2 s and retries, **bypassing its cache**,
  up to 60 times (about 2 minutes).
- `work/{id}`: same thing if `Books` is `null`.

A replacement server can use this to fetch upstream data lazily. If the data is
ready immediately, `Works` and `Books` must be **non-null arrays** (possibly empty).

## 2. Endpoints

### `GET author/{authorId}`

The client follows redirects automatically and caches successful responses for 30 minutes.

**Response:** an `AuthorResource` (§3), with `Works` and `Series` filled in.

Rules applied by the client:

- A work is kept only if `ForeignId > 0` **and** its *primary author* is this author (see
  [primary author](#primary-author)). Works where the author is only a translator, illustrator, etc.
  should still be included with correct contributors, and the client will filter them out.
- Each work must contain its editions in `Books`, each with `Contributors`.
- `Series` lists the series that contain these works. Each `LinkItems[].ForeignWorkId` should
  refer to a work in `Works`; other links are ignored. Series with no `LinkItems` are ignored.

### `GET work/{workId}`

The client does **not** follow redirects automatically for this route.

**Response, either:**

- `200` + `WorkResource` (§3), with `Books` non-null and `Authors` **non-empty** (otherwise
  `BookInfoException`). `Authors` must include the [primary author](#primary-author).
  `Series` lists the series that contain the work;
- or a **redirect** (`3xx`) whose `Location` ends with `/author/{authorId}`. The client then
  fetches that author and picks the work with `ForeignId == workId` from it (`404` if missing).
  Any other redirect target throws `NotImplementedException`.

### `GET book/{editionId}`

Used when the user searches for `edition:{id}`, and by quick search when it returns a single result.

**Response: must be a redirect.** A `200` is treated as an error. The `Location` header must end with:

- `/work/{workId}`: the client then calls `work/{workId}`;
- or `/author/{authorId}`: the client then calls `author/{authorId}`.

The client takes the last two path segments of `Location` (`…/{type}/{id}`), so absolute
and relative URLs both work. The resulting work **must** contain the requested edition in
`Books` (matched by `ForeignId`), otherwise the client throws `EditionNotFoundException`.

`404`: the edition is unknown.

### `POST book/bulk`

Used by quick search (`getAllEditions = false`) when Goodreads search returns more than one result.

**Request:** `Content-Type: application/json`, body = a JSON array of edition IDs:

```json
[44767458, 234225, 17899948]
```

**Response:** a `BulkBookResource` (§3):

- `Works`: one work per requested edition. **Put the requested edition first in `Books`**.
  The client sorts results by the position of `Books[0].ForeignId` in the request.
  Every work's editions must have `Contributors`.
- `Authors`: must contain the primary author of **every** returned work. Otherwise the client
  throws `BookInfoException`, unless that author is already in the user's library.
- `Series`: the series those works belong to (may be empty, but must not be `null`).

The client follows redirects automatically.

### `GET author/changed?since={iso8601}`

Example: `author/changed?since=2026-11-01T00:00:00.0000000Z` (format `"o"`, round-trip).

Used to refresh only the authors that changed. Errors are ignored (a `null` result means
"no information": the client falls back to its normal refresh).

**Response:** a `RecentUpdatesResource`:

```json
{ "Limited": false, "Since": "2026-11-01T00:00:00Z", "Ids": [58, 1077326] }
```

- `Limited: true` means the list is incomplete, so the client ignores it and refreshes everything.
- **A minimal implementation can always return `{"Limited": true, "Since": "...", "Ids": []}`**.

## 3. Resources

The *Readarr field* column shows where the value ends up in Readarr's model.

### `AuthorResource`

| Property | Type | Readarr field / usage |
|---|---|---|
| `ForeignId` | int | `AuthorMetadata.ForeignAuthorId` and `TitleSlug` |
| `Name` | string | `Name`. `SortName`, `NameLastFirst` and `SortNameLastFirst` are derived from it |
| `Description` | string? | `Overview` |
| `ImageUrl` | string? | Image, type *Poster* |
| `Url` | string? | Link named "Goodreads" |
| `RatingCount` | int | `Ratings.Votes` |
| `AverageRating` | double | `Ratings.Value` |
| `Works` | `WorkResource[]`? | Author's books (`author/*` only; `null` triggers polling) |
| `Series` | `SeriesResource[]`? | Author's series (`author/*` only) |

In `WorkResource.Authors` and `BulkBookResource.Authors`, only the scalar fields are used.

### `WorkResource` (a Readarr *Book*)

| Property | Type | Readarr field / usage |
|---|---|---|
| `ForeignId` | int | `Book.ForeignBookId` and `TitleSlug` |
| `Title` | string? | `Title`. If empty, the title of the most popular edition is used |
| `Url` | string? | Link named "Goodreads Editions" |
| `ReleaseDate` | date? | `ReleaseDate`. If missing, the earliest edition date is used, preferring dates that are not 1 January |
| `Genres` | string[]? | `Genres` |
| `RelatedWorks` | int[]? | `RelatedBooks` (work IDs) |
| `Books` | `BookResource[]`? | Editions (`null` triggers polling in `work/*`) |
| `Series` | `SeriesResource[]` | Series (`work/*` only) |
| `Authors` | `AuthorResource[]` | Authors (`work/*` only; must not be empty) |

The work's rating is computed by the client: it is the vote-weighted average of its editions.

### `BookResource` (a Readarr *Edition*)

| Property | Type | Readarr field / usage |
|---|---|---|
| `ForeignId` | int | `Edition.ForeignEditionId` and `TitleSlug` |
| `Title` | string | `Title` (must not be `null`: `CleanSpaces()` is called on it) |
| `Isbn13` | string? | `Isbn13` |
| `Asin` | string? | `Asin` |
| `Description` | string? | `Overview` |
| `Language` | string? | `Language`. ISO 639-1 (`en`) or 639-2/3 (`eng`); `en-US` / `en_US` are accepted. Filtered by the metadata profile (default: `eng, null`) |
| `Format` | string? | `Format` (e.g. `Paperback`, `Kindle Edition`, `Audiobook`) |
| `IsEbook` | bool | `IsEbook` |
| `EditionInformation` | string? | `Disambiguation` |
| `Publisher` | string? | `Publisher` |
| `NumPages` | int? | `PageCount` (`null` becomes `0`) |
| `ReleaseDate` | date? | `ReleaseDate` |
| `ImageUrl` | string? | Image, type *Cover* |
| `Url` | string? | Link named "Goodreads Book" |
| `RatingCount` | int | `Ratings.Votes` |
| `AverageRating` | double | `Ratings.Value` |
| `Contributors` | `ContributorResource[]` | Used to find the primary author |

Readarr monitors the most popular edition of each work by default (`Ratings.Popularity`, i.e. votes × rating).

### `ContributorResource`

| Property | Type | Usage |
|---|---|---|
| `ForeignId` | int | Goodreads author ID |
| `Role` | string? | Not read by the client (e.g. `"Author"`, `"Translator"`) |

### `SeriesResource` / `SeriesWorkLinkResource`

| Property | Type | Readarr field |
|---|---|---|
| `ForeignId` | int | `Series.ForeignSeriesId` |
| `Title` | string | `Title` |
| `Description` | string? | `Description` |
| `LinkItems` | `SeriesWorkLinkResource[]` | Links between the series and its books (must not be `null`) |
| `LinkItems[].ForeignWorkId` | int | Linked work (`0` = ignored) |
| `LinkItems[].PositionInSeries` | string? | `Position`, as displayed (e.g. `"1"`, `"2.5"`, `"1-3"`) |
| `LinkItems[].SeriesPosition` | int | `SeriesPosition` (numeric sort order) |
| `LinkItems[].Primary` | bool | `IsPrimary` |

### `BulkBookResource`

```json
{ "Works": [ /* WorkResource */ ], "Series": [ /* SeriesResource */ ], "Authors": [ /* AuthorResource */ ] }
```

### Primary author

Readarr never uses an explicit "author" field on a work. It computes the primary author like this:

> Among the work's editions (`Books`) that have at least one contributor, take the one with the
> highest `RatingCount × AverageRating`. The primary author is the `ForeignId` of its **first** contributor.

**The first contributor of each edition must therefore be the main author**, not a
translator or editor. If no edition has contributors, the author ID is `0`, and the work
is dropped from the author's list.

## 4. What does *not* go through this API

| Feature | Source | Code | Status (2026-11-22) |
|---|---|---|---|
| Text search (title, author, ISBN, ASIN) | `https://www.goodreads.com/book/auto_complete?format=json&q=…`, called **directly** | [`GoodreadsSearchProxy.cs`](../src/NzbDrone.Core/MetadataSource/GoodreadsSearchProxy/GoodreadsSearchProxy.cs) | **Works** (HTTP 200, returns `bookId`, `workId`, `author.id`) |
| Goodreads Series / Lists import lists | Scrapes `goodreads.com` | [`GoodreadsProxy.cs`](../src/NzbDrone.Core/MetadataSource/Goodreads/GoodreadsProxy.cs) | Not checked |

Search flow:

1. Goodreads autocomplete returns `(bookId, workId, author.id)` triples.
2. **Full search** (`getAllEditions = true`, used for adding a book or author): calls
   `author/{author.id}` for each distinct author and keeps the matching works, then calls
   `work/{workId}` for any work not found that way.
3. **Quick search** (`getAllEditions = false`, used by the combined search): calls `book/{bookId}`
   if there is a single result, otherwise `book/bulk` with all the `bookId`s.

Special search prefixes: `author:{id}`, `work:{id}`, `edition:{id}` (Goodreads IDs) call the
corresponding endpoint directly. `isbn:{x}` and `asin:{x}` go through the Goodreads search.

Search therefore depends on Goodreads, independently of the metadata server. If
Goodreads closes or blocks the autocomplete endpoint, search will need its own
replacement (for example a `search` route on our server).

## 5. Minimal example

`GET author/58`:

```json
{
  "ForeignId": 58,
  "Name": "Frank Herbert",
  "Description": "Franklin Patrick Herbert Jr. was an American science fiction author…",
  "ImageUrl": "https://example.org/authors/58.jpg",
  "Url": "https://www.goodreads.com/author/show/58",
  "RatingCount": 2500000,
  "AverageRating": 4.1,
  "Works": [
    {
      "ForeignId": 3634639,
      "Title": "Dune",
      "Url": "https://www.goodreads.com/work/editions/3634639",
      "ReleaseDate": "1965-08-01T00:00:00Z",
      "Genres": ["science-fiction", "fiction"],
      "RelatedWorks": [],
      "Books": [
        {
          "ForeignId": 44767458,
          "Title": "Dune",
          "Isbn13": "9780593099322",
          "Asin": null,
          "Description": "Set on the desert planet Arrakis…",
          "Language": "eng",
          "Format": "Paperback",
          "IsEbook": false,
          "EditionInformation": null,
          "Publisher": "Ace",
          "NumPages": 658,
          "ReleaseDate": "2019-10-01T00:00:00Z",
          "ImageUrl": "https://example.org/editions/44767458.jpg",
          "Url": "https://www.goodreads.com/book/show/44767458",
          "RatingCount": 1730908,
          "AverageRating": 4.29,
          "Contributors": [{ "ForeignId": 58, "Role": "Author" }]
        }
      ]
    }
  ],
  "Series": [
    {
      "ForeignId": 45935,
      "Title": "Dune",
      "Description": null,
      "LinkItems": [
        { "ForeignWorkId": 3634639, "PositionInSeries": "1", "SeriesPosition": 1, "Primary": true }
      ]
    }
  ]
}
```

`GET book/44767458` → `302 Found`, `Location: /v1/work/3634639`.

## 6. Implementation checklist

- [ ] Accepts Goodreads IDs (author, work, edition) as input
- [ ] PascalCase JSON on every route
- [ ] `Works` / `Books` / `LinkItems` / `Series` are never `null` once the data is ready
- [ ] The first contributor of each edition is the main author
- [ ] `work/*` responses include their authors in `Authors`
- [ ] `book/{id}` replies with a redirect, never with `200`
- [ ] `book/bulk` puts the requested edition first in each work's `Books`
- [ ] `429` responses include an integer `Retry-After` (in seconds)
- [ ] `author/changed` can always return `Limited: true`
- [ ] `Language` uses ISO 639 codes
