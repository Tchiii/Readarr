using System;
using System.Net;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.MetadataSource.OpenLibrary
{
    /// <summary>
    /// Open Library could not be reached or answered with an unexpected error.
    /// Surfaces to API clients as 503, like <see cref="BookInfo.BookInfoException"/>.
    /// </summary>
    public class OpenLibraryException : NzbDroneClientException
    {
        public OpenLibraryException(string message, params object[] args)
            : base(HttpStatusCode.ServiceUnavailable, message, args)
        {
        }

        public OpenLibraryException(string message, Exception innerException, params object[] args)
            : base(HttpStatusCode.ServiceUnavailable, message, innerException, args)
        {
        }
    }
}
