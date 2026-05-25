using Dotmim.Sync.Extensions;
using Dotmim.Sync.Serialization;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Web.Client.Resume
{
    /// <summary>
    /// File-system backed implementation of <see cref="IClientResumeStateStore"/>. Each scope is
    /// stored as a single JSON file under a configurable directory.
    /// <para>
    /// The default directory sits next to the batch directory used by sync, so the resume state
    /// follows the same lifecycle as the batches it references: when the user wipes the sync tmp
    /// folder, the resume state goes with it.
    /// </para>
    /// </summary>
    public class FileClientResumeStateStore : IClientResumeStateStore
    {
        private static readonly ISerializer JsonSerializer = SerializersFactory.JsonSerializerFactory.GetSerializer();

        private readonly string directory;

        // Serializes concurrent SaveAsync calls. The resumable orchestrator's parallel
        // batch downloader fans out — without this, two tasks race on the same .tmp file
        // and one of them gets "file is being used by another process".
        private readonly SemaphoreSlim saveGate = new(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="FileClientResumeStateStore"/> class.
        /// </summary>
        /// <param name="directory">
        /// Directory where the JSON state files are written. Defaults to a "resume" subfolder under
        /// <see cref="SyncOptions.GetDefaultUserBatchDirectory"/>.
        /// </param>
        public FileClientResumeStateStore(string directory = null)
        {
            this.directory = string.IsNullOrEmpty(directory)
                ? Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), "resume")
                : directory;
        }

        /// <summary>
        /// Gets the directory used to store resume state files. Exposed for diagnostics and tests.
        /// </summary>
        public string Directory => this.directory;

        /// <inheritdoc />
        public async Task<ClientResumeState> LoadAsync(string scopeName, CancellationToken cancellationToken = default)
        {
            var fullPath = this.GetPath(scopeName);
            if (!File.Exists(fullPath))
                return null;

            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return await JsonSerializer.DeserializeAsync<ClientResumeState>(stream).ConfigureAwait(false);
            }
            catch
            {
                // A corrupt file is functionally equivalent to "no state" — return null so the next sync
                // simply starts fresh instead of failing hard.
                return null;
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(ClientResumeState state, CancellationToken cancellationToken = default)
        {
            Guard.ThrowIfNull(state);

            if (!System.IO.Directory.Exists(this.directory))
                System.IO.Directory.CreateDirectory(this.directory);

            var fullPath = this.GetPath(state.ScopeName);
            var tempPath = fullPath + ".tmp";

            // Serialize concurrent saves so we never race on the .tmp swap path. Without
            // this, two parallel batch-download tasks both try to create/replace the same
            // file and one of them throws "the process cannot access the file ... because
            // it is being used by another process."
            await this.saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                state.LastUpdatedUtc = DateTime.UtcNow;
                var bytes = await JsonSerializer.SerializeAsync(state).ConfigureAwait(false);

                // Write to a temp file then atomically replace, so a crash mid-write doesn't leave a
                // half-flushed state file on disk.
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
                    // Fallback for filesystems that don't support File.Replace
                    File.Copy(tempPath, fullPath, overwrite: true);
                    File.Delete(tempPath);
                }
            }
            finally
            {
                this.saveGate.Release();
            }
        }

        /// <inheritdoc />
        public Task DeleteAsync(string scopeName, CancellationToken cancellationToken = default)
        {
            var fullPath = this.GetPath(scopeName);
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                }
                catch
                {
                    // ignore: a stale resume file is harmless because we always validate before reusing
                }
            }

            return Task.CompletedTask;
        }

        private string GetPath(string scopeName)
        {
            // sanitize the scope name so it's a valid file name
            var safeScope = string.IsNullOrEmpty(scopeName) ? "default" : SanitizeFileName(scopeName);
            return Path.Combine(this.directory, string.Format(CultureInfo.InvariantCulture, "{0}.resume.json", safeScope));
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
