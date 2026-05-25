using Dotmim.Sync.Serialization;
using Dotmim.Sync.Web.Server.Resume;
using System.Collections.ObjectModel;

namespace Dotmim.Sync.Web.Server
{

    /// <summary>
    /// Specifies options for the Web Server.
    /// </summary>
    public class WebServerOptions
    {

        /// <summary>
        /// Gets converters used by different clients.
        /// </summary>
        public Collection<IConverter> Converters { get; }

        /// <summary>
        /// Gets the serializer factories.
        /// </summary>
        public Collection<ISerializerFactory> SerializerFactories { get; }

        /// <summary>
        /// Gets or sets the store used to persist <see cref="SessionCache"/> across HTTP requests.
        /// <para>
        /// Defaults to <see cref="AspNetSessionWebServerSessionStore"/>, which preserves the historical
        /// behavior (cache lives in <c>HttpContext.Session</c>). Swap in
        /// <see cref="FileSystemWebServerSessionStore"/> (or your own implementation) to make sync
        /// state survive across server restarts so resumable clients can continue mid-flight after a
        /// cold start.
        /// </para>
        /// </summary>
        public IWebServerSessionStore SessionStore { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServerOptions"/> class.
        /// Create a new instance of options with default values.
        /// </summary>
        public WebServerOptions()
            : base()
        {
            this.Converters = [];
            this.SerializerFactories =
            [
                SerializersFactory.JsonSerializerFactory
            ];
            this.SessionStore = new AspNetSessionWebServerSessionStore();
        }
    }
}
