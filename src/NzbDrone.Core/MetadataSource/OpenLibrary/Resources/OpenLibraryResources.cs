using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Resources
{
    // DTOs for the Open Library JSON API (https://openlibrary.org/developers/api).
    // Only the fields Readarr maps are declared; everything else is ignored on deserialisation.

    /// <summary>
    /// Fields shared by every Open Library record. When two records are merged, the old key keeps
    /// answering with <c>type = /type/redirect</c> and a <c>location</c> pointing to the surviving record;
    /// deleted records answer with <c>type = /type/delete</c>.
    /// </summary>
    public abstract class OpenLibraryRecord
    {
        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("type")]
        public OpenLibraryKeyRef Type { get; set; }

        [JsonPropertyName("location")]
        public string Location { get; set; }

        [JsonIgnore]
        public bool IsRedirect => Type?.Key == "/type/redirect" && !string.IsNullOrWhiteSpace(Location);

        [JsonIgnore]
        public bool IsDeleted => Type?.Key == "/type/delete";
    }

    /// <summary>A bare <c>{"key": "/..."}</c> reference.</summary>
    public class OpenLibraryKeyRef
    {
        [JsonPropertyName("key")]
        public string Key { get; set; }
    }

    /// <summary><c>GET /authors/{OLxxxA}.json</c></summary>
    public class OpenLibraryAuthor : OpenLibraryRecord
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("personal_name")]
        public string PersonalName { get; set; }

        [JsonPropertyName("bio")]
        [JsonConverter(typeof(OpenLibraryTextConverter))]
        public string Bio { get; set; }

        [JsonPropertyName("photos")]
        public List<long> Photos { get; set; }

        [JsonPropertyName("links")]
        public List<OpenLibraryLink> Links { get; set; }

        [JsonPropertyName("death_date")]
        public string DeathDate { get; set; }
    }

    public class OpenLibraryLink
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }
    }

    /// <summary><c>GET /works/{OLxxxW}.json</c></summary>
    public class OpenLibraryWork : OpenLibraryRecord
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("description")]
        [JsonConverter(typeof(OpenLibraryTextConverter))]
        public string Description { get; set; }

        [JsonPropertyName("subjects")]
        public List<string> Subjects { get; set; }

        [JsonPropertyName("authors")]
        public List<OpenLibraryWorkAuthor> Authors { get; set; }

        [JsonPropertyName("first_publish_date")]
        public string FirstPublishDate { get; set; }

        [JsonPropertyName("covers")]
        public List<long> Covers { get; set; }
    }

    /// <summary>One entry of <c>work.authors</c>: <c>{"author": {"key": "/authors/OLxxxA"}, ...}</c></summary>
    public class OpenLibraryWorkAuthor
    {
        [JsonPropertyName("author")]
        public OpenLibraryKeyRef Author { get; set; }
    }

    /// <summary>One edition, as returned by <c>/works/{id}/editions.json</c> and <c>/books/{OLxxxM}.json</c>.</summary>
    public class OpenLibraryEdition : OpenLibraryRecord
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("subtitle")]
        public string Subtitle { get; set; }

        [JsonPropertyName("works")]
        public List<OpenLibraryKeyRef> Works { get; set; }

        [JsonPropertyName("authors")]
        public List<OpenLibraryKeyRef> Authors { get; set; }

        [JsonPropertyName("isbn_13")]
        public List<string> Isbn13 { get; set; }

        [JsonPropertyName("isbn_10")]
        public List<string> Isbn10 { get; set; }

        [JsonPropertyName("identifiers")]
        public Dictionary<string, List<string>> Identifiers { get; set; }

        [JsonPropertyName("languages")]
        public List<OpenLibraryKeyRef> Languages { get; set; }

        [JsonPropertyName("publishers")]
        public List<string> Publishers { get; set; }

        [JsonPropertyName("publish_date")]
        public string PublishDate { get; set; }

        [JsonPropertyName("number_of_pages")]
        public int? NumberOfPages { get; set; }

        [JsonPropertyName("physical_format")]
        public string PhysicalFormat { get; set; }

        [JsonPropertyName("edition_name")]
        public string EditionName { get; set; }

        [JsonPropertyName("description")]
        [JsonConverter(typeof(OpenLibraryTextConverter))]
        public string Description { get; set; }

        [JsonPropertyName("covers")]
        public List<long> Covers { get; set; }
    }

    /// <summary><c>GET /works/{id}/editions.json</c> (paginated through <c>links.next</c>).</summary>
    public class OpenLibraryEditionsPage
    {
        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("links")]
        public OpenLibraryPageLinks Links { get; set; }

        [JsonPropertyName("entries")]
        public List<OpenLibraryEdition> Entries { get; set; }
    }

    public class OpenLibraryPageLinks
    {
        [JsonPropertyName("next")]
        public string Next { get; set; }
    }

    /// <summary><c>GET /search.json</c></summary>
    public class OpenLibrarySearchResult
    {
        [JsonPropertyName("numFound")]
        public int NumFound { get; set; }

        [JsonPropertyName("docs")]
        public List<OpenLibrarySearchDoc> Docs { get; set; }
    }

    /// <summary>A work, as returned by the search API. Ratings and first publish year only exist here.</summary>
    public class OpenLibrarySearchDoc
    {
        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("author_key")]
        public List<string> AuthorKey { get; set; }

        [JsonPropertyName("author_name")]
        public List<string> AuthorName { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("edition_count")]
        public int EditionCount { get; set; }

        [JsonPropertyName("ratings_average")]
        public double? RatingsAverage { get; set; }

        [JsonPropertyName("ratings_count")]
        public int? RatingsCount { get; set; }

        [JsonPropertyName("subject")]
        public List<string> Subject { get; set; }

        [JsonPropertyName("cover_i")]
        public long? CoverId { get; set; }

        /// <summary>The single best-matching edition (the search API returns one per work).</summary>
        [JsonPropertyName("editions")]
        public OpenLibrarySearchEditions Editions { get; set; }
    }

    public class OpenLibrarySearchEditions
    {
        [JsonPropertyName("docs")]
        public List<OpenLibrarySearchEditionDoc> Docs { get; set; }
    }

    /// <summary>An edition embedded in a search doc. Unlike the edition API, most fields are arrays here.</summary>
    public class OpenLibrarySearchEditionDoc
    {
        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("language")]
        public List<string> Language { get; set; }

        [JsonPropertyName("isbn")]
        public List<string> Isbn { get; set; }

        [JsonPropertyName("cover_i")]
        public long? CoverId { get; set; }

        [JsonPropertyName("publisher")]
        public List<string> Publisher { get; set; }

        [JsonPropertyName("publish_date")]
        public List<string> PublishDate { get; set; }
    }

    /// <summary>
    /// Open Library stores free text either as a plain string or as a typed value:
    /// <c>"bio": "..."</c> or <c>"bio": {"type": "/type/text", "value": "..."}</c>. Both map to a string.
    /// </summary>
    public class OpenLibraryTextConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.StartObject:
                    string value = null;
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        if (doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            value = v.GetString();
                        }
                    }

                    return value;
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }
}
