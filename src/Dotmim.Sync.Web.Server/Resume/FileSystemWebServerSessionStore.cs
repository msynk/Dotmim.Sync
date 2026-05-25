using Dotmim.Sync.Extensions;
using Dotmim.Sync.Serialization;
using Microsoft.AspNetCore.Http;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Web.Server.Resume
{
    /// <summary>
    /// Durable <see cref="IWebServerSessionStore"/> implementation that writes each
    /// <see cref="SessionCache"/> to a JSON file under a configurable directory.
    /// <para>
    /// Use this store when you want resumable syncs to survive process restarts (cold deploys,
    /// container reschedules, app pool recycles). Each session id maps to a single file, written
    /// atomically via a <c>tmp</c> swap so a crash mid-write cannot corrupt the state.
    /// </para>
    /// <para>
    /// Default directory is <see cref="SyncOptions.GetDefaultUserBatchDirectory"/>/server-sessions.
    /// You should normally point this at a path that follows the same lifecycle as
    /// <see cref="SyncOptions.BatchDirectory"/> on the server, since the cache references the partial
    /// batch folders sitting next to it.
    /// </para>
    /// </summary>
    public class FileSystemWebServerSessionStore : IWebServerSessionStore
    {
        private static readonly ISerializer JsonSerializer = SerializersFactory.JsonSerializerFactory.GetSerializer();

        private readonly string directory;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileSystemWebServerSessionStore"/> class.
        /// </summary>
        /// <param name="directory">
        /// Directory where session cache files are written. Defaults to a "server-sessions" subfolder
        /// under <see cref="SyncOptions.GetDefaultUserBatchDirectory"/>.
        /// </param>
        public FileSystemWebServerSessionStore(string directory = null)
        {
            this.directory = string.IsNullOrEmpty(directory)
                ? Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), "server-sessions")
                : directory;
        }

        /// <summary>
        /// Gets the directory used to store session cache files. Exposed for diagnostics and tests.
        /// </summary>
        public string Directory => this.directory;

        /// <inheritdoc />
        public async Task<SessionCache> LoadAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default)
        {
            var fullPath = this.GetPath(sessionId);
            if (!File.Exists(fullPath))
                return null;

            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return await JsonSerializer.DeserializeAsync<SessionCache>(stream).ConfigureAwait(false);
            }
            catch
            {
                // Corrupt file -> behave like missing state. The sync will start over, which is
                // strictly safer than crashing on a malformed cache.
                return null;
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(HttpContext httpContext, string sessionId, SessionCache cache, CancellationToken cancellationToken = default)
        {
            Guard.ThrowIfNull(cache);

            if (!System.IO.Directory.Exists(this.directory))
                System.IO.Directory.CreateDirectory(this.directory);

            var fullPath = this.GetPath(sessionId);
            var tempPath = fullPath + ".tmp";

            var bytes = await JsonSerializer.SerializeAsync(cache).ConfigureAwait(false);

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (File.Exists(fullPath))
                    File.Replace(tempPath, fullPath, null);
                else
                    File.Move(tempPath, fullPath);
            }
            catch
            {
                // Filesystems that don't support File.Replace fall back to copy+delete.
                File.Copy(tempPath, fullPath, overwrite: true);
                File.Delete(tempPath);
            }
        }

        /// <inheritdoc />
        public Task DeleteAsync(HttpContext httpContext, string sessionId, CancellationToken cancellationToken = default)
        {
            var fullPath = this.GetPath(sessionId);
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                }
                catch
                {
                    // ignore: leftover state will be overwritten on next save or simply ignored.
                }
            }

            return Task.CompletedTask;
        }

        private string GetPath(string sessionId)
        {
            var safe = string.IsNullOrEmpty(sessionId) ? "default" : SanitizeFileName(sessionId);
            return Path.Combine(this.directory, string.Format(CultureInfo.InvariantCulture, "{0}.session.json", safe));
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }

            return new string(chars);
        }
    }
}
