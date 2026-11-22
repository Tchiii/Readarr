using System;
using System.Collections.Generic;
using System.IO;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaCover
{
    public interface IMediaCoverProxy
    {
        string RegisterUrl(string url);

        string GetUrl(string hash);
        byte[] GetImage(string hash);
    }

    public class MediaCoverProxy : IMediaCoverProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly ICached<string> _cache;

        public MediaCoverProxy(IHttpClient httpClient, IConfigFileProvider configFileProvider, ICacheManager cacheManager)
        {
            _httpClient = httpClient;
            _configFileProvider = configFileProvider;
            _cache = cacheManager.GetCache<string>(GetType());
        }

        public string RegisterUrl(string url)
        {
            if (url.IsNullOrWhiteSpace())
            {
                return null;
            }

            var hash = url.SHA256Hash();

            _cache.Set(hash, url, TimeSpan.FromHours(24));

            _cache.ClearExpired();

            var fileName = Path.GetFileName(url);
            return _configFileProvider.UrlBase + @"/MediaCoverProxy/" + hash + "/" + fileName;
        }

        public string GetUrl(string hash)
        {
            var result = _cache.Find(hash);

            if (result == null)
            {
                throw new KeyNotFoundException("Url no longer in cache");
            }

            return result;
        }

        public byte[] GetImage(string hash)
        {
            var url = GetUrl(hash);

            // Cover hosts may redirect (Open Library serves older covers from archive.org). Without this the
            // redirect response itself, with an empty body, was returned and the UI showed a placeholder.
            // Library covers already follow redirects (HttpClient.DownloadFileAsync).
            var request = new HttpRequest(url)
            {
                AllowAutoRedirect = true,
                SuppressHttpError = true
            };

            var response = _httpClient.Get(request);

            // A missing image (e.g. an Open Library author without a photo) is a normal case:
            // return null so the caller answers 404 instead of logging a fatal error.
            return response.HasHttpError ? null : response.ResponseData;
        }
    }
}
