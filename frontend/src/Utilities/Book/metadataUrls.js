// Readarr entries carry either Goodreads IDs (numeric) or Open Library IDs (OL…A / OL…M).
// Build the external link from the ID format instead of assuming Goodreads.

const OPEN_LIBRARY_AUTHOR = /^OL\d+A$/;
const OPEN_LIBRARY_EDITION = /^OL\d+M$/;

export function getAuthorUrl(foreignAuthorId) {
  return OPEN_LIBRARY_AUTHOR.test(foreignAuthorId) ?
    `https://openlibrary.org/authors/${foreignAuthorId}` :
    `https://goodreads.com/author/show/${foreignAuthorId}`;
}

export function getEditionUrl(foreignEditionId) {
  return OPEN_LIBRARY_EDITION.test(foreignEditionId) ?
    `https://openlibrary.org/books/${foreignEditionId}` :
    `https://goodreads.com/book/show/${foreignEditionId}`;
}
