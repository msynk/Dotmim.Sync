using Dotmim.Sync.Batch;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Extensions;
using Dotmim.Sync.Serialization;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Web.Client.Resume
{
    /// <summary>
    /// A <see cref="WebRemoteOrchestrator"/> variant that persists per-batch progress to disk so an
    /// interrupted sync (network drop, process kill, app suspend) can resume from the last
    /// successfully transferred batch on the next call to <c>SynchronizeAsync</c> instead of
    /// restarting from scratch.
    /// <para>
    /// The resumable behavior is gated on <see cref="SyncOptions.Resumable"/>. When the flag is
    /// <c>false</c> (default) this class behaves exactly like the base orchestrator. When it is
    /// <c>true</c>, the orchestrator:
    /// </para>
    /// <list type="number">
    ///   <item><description>Reuses the previous <see cref="SyncContext.SessionId"/> via the
    ///   <see cref="SyncOptions.SessionIdProvider"/> hook so the server can reattach to its own
    ///   <c>SessionCache</c>.</description></item>
    ///   <item><description>Persists a <see cref="ClientResumeState"/> after each batch is uploaded
    ///   or downloaded.</description></item>
    ///   <item><description>Skips already-uploaded batches when resuming the upload phase.</description></item>
    ///   <item><description>Reuses already-downloaded batch files when resuming the download phase.</description></item>
    ///   <item><description>Suppresses the otherwise-aggressive folder cleanup so the partial
    ///   download survives across calls.</description></item>
    ///   <item><description>Discards the resume state once the sync completes successfully.</description></item>
    /// </list>
    /// <para>
    /// This is opt-in: register a <see cref="ResumableWebRemoteOrchestrator"/> in your
    /// <see cref="SyncAgent"/>, set <see cref="SyncOptions.Resumable"/> to <c>true</c> (or use the
    /// <c>resumable</c> overload of <c>SynchronizeAsync</c>), and provide an
    /// <see cref="IClientResumeStateStore"/>.
    /// </para>
    /// </summary>
    public class ResumableWebRemoteOrchestrator : WebRemoteOrchestrator
    {
        private readonly IClientResumeStateStore stateStore;

        // The state currently being built up for the in-flight sync. Captured once at the start of
        // each sync session so the various override methods don't have to re-load it from disk.
        private ClientResumeState activeState;

        // Marker used to suppress folder cleanup while a resume token is alive. Set by overridden
        // hooks before they call back into the base implementation.
        private bool suppressCleanup;

        /// <inheritdoc cref="WebRemoteOrchestrator(string, IConverter, HttpClient, SyncPolicy, int, string)"/>
        /// <param name="stateStore">
        /// Persistent store for the resume state. When <c>null</c>, a default
        /// <see cref="FileClientResumeStateStore"/> rooted under the batch directory is used.
        /// </param>
        public ResumableWebRemoteOrchestrator(
            string serviceUri,
            IClientResumeStateStore stateStore = null,
            IConverter customConverter = null,
            HttpClient client = null,
            SyncPolicy syncPolicy = null,
            int maxDownladingDegreeOfParallelism = 4,
            string identifier = null)
            : base(serviceUri, customConverter, client, syncPolicy, maxDownladingDegreeOfParallelism, identifier)
        {
            this.stateStore = stateStore ?? new FileClientResumeStateStore();
            this.WireSessionIdProvider();
        }

        /// <inheritdoc cref="WebRemoteOrchestrator(Uri, IConverter, HttpClient, SyncPolicy, int, string)"/>
        /// <param name="stateStore">
        /// Persistent store for the resume state. When <c>null</c>, a default
        /// <see cref="FileClientResumeStateStore"/> rooted under the batch directory is used.
        /// </param>
        public ResumableWebRemoteOrchestrator(
            Uri serviceUri,
            IClientResumeStateStore stateStore = null,
            IConverter customConverter = null,
            HttpClient client = null,
            SyncPolicy syncPolicy = null,
            int maxDownladingDegreeOfParallelism = 4,
            string identifier = null)
            : base(serviceUri, customConverter, client, syncPolicy, maxDownladingDegreeOfParallelism, identifier)
        {
            this.stateStore = stateStore ?? new FileClientResumeStateStore();
            this.WireSessionIdProvider();
        }

        /// <summary>
        /// Gets the resume state store used by this orchestrator.
        /// </summary>
        public IClientResumeStateStore StateStore => this.stateStore;

        /// <inheritdoc />
        public override SyncOptions Options
        {
            get => base.Options;
            internal set
            {
                base.Options = value;
                this.WireSessionIdProvider();
            }
        }

        /// <summary>
        /// Whenever <see cref="SyncOptions"/> is assigned (notably by the <see cref="SyncAgent"/>
        /// constructor that overrides our remote orchestrator options), install our
        /// <see cref="SessionIdProvider"/> hook so the agent will reuse a previously persisted
        /// session id when resumable is on.
        /// </summary>
        private void WireSessionIdProvider()
        {
            if (this.Options == null)
                return;

            // Don't clobber a custom provider the user set themselves.
            if (this.Options.SessionIdProvider != null)
                return;

            this.Options.SessionIdProvider = this.ProvideSessionId;
        }

        /// <summary>
        /// Decides which session id the next sync should use.
        /// <para>
        /// When resumable is on AND we have a saved state for the requested scope that's still in a
        /// resumable phase, returns the previously persisted id so the server can reattach. Otherwise
        /// returns a fresh <see cref="Guid.NewGuid"/>.
        /// </para>
        /// </summary>
        protected virtual Guid ProvideSessionId(string scopeName)
        {
            if (this.Options == null || !this.Options.Resumable)
                return Guid.NewGuid();

            // Synchronous load — this is a tight startup path called from SyncAgent before any await.
            // The store implementations are all local IO so this is acceptable.
            ClientResumeState saved = null;
            try
            {
                saved = this.stateStore.LoadAsync(scopeName).GetAwaiter().GetResult();
            }
            catch
            {
                // ignore: a broken store should never break the sync, just disables resume.
            }

            if (saved == null || saved.Phase == ClientResumePhase.None || saved.Phase == ClientResumePhase.Applied)
                return Guid.NewGuid();

            return saved.SessionId;
        }

        /// <inheritdoc />
        internal override async Task<(SyncContext Context, ServerSyncChanges ServerSyncChanges, ConflictResolutionPolicy ServerResolutionPolicy)>
            InternalApplyThenGetChangesAsync(ScopeInfoClient cScopeInfoClient, ScopeInfo cScopeInfo, SyncContext context, ClientSyncChanges clientChanges,
            DbConnection connection = default, DbTransaction transaction = default, IProgress<ProgressArgs> progress = null, CancellationToken cancellationToken = default)
        {
            if (!this.IsResumeActive)
            {
                // Not resumable: behave exactly like the base orchestrator.
                this.activeState = null;
                return await base.InternalApplyThenGetChangesAsync(cScopeInfoClient, cScopeInfo, context, clientChanges, connection, transaction, progress, cancellationToken).ConfigureAwait(false);
            }

            // Load (or create) the resume state for this sync. Validate it against the current
            // scope/parameters; mismatched state is dropped so we don't accidentally replay batches
            // belonging to a different filter set.
            this.activeState = await this.LoadAndValidateStateAsync(context, cScopeInfoClient, clientChanges, cancellationToken).ConfigureAwait(false);

            // Prevent the cleanup paths from nuking the partial folders while a resume token is alive.
            this.suppressCleanup = true;

            try
            {
                // Run the same flow as the base, but with our resume-aware overrides for upload and
                // download. The download path is hooked through DownladBatchInfoAsync /
                // DownloadBatchPartInfoAsync; the upload path needs to be inlined here because the
                // base method's loop does not consult any state.
                return await this.InternalApplyThenGetChangesResumableAsync(
                    cScopeInfoClient, cScopeInfo, context, clientChanges, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (NullReferenceException nre)
            {
                // Defensive: any NRE in the resumable path most likely means the persisted state
                // is stale or out of sync with the current schema/data. Wipe it and surface a
                // clear error so the user can retry from a clean slate. The full stack trace is
                // chained as the inner exception so a debugger can still pinpoint the line.
                await this.SafeDeleteStateAsync(context, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Resumable sync failed with a null-reference dereference. The persisted resume state " +
                    "has been deleted; the next SynchronizeAsync call will start from a clean slate. " +
                    "If this keeps happening, file a bug with the inner-exception stack trace.",
                    nre);
            }
            finally
            {
                this.suppressCleanup = false;
            }
        }

        private async Task SafeDeleteStateAsync(SyncContext context, CancellationToken cancellationToken)
        {
            try
            {
                await this.stateStore.DeleteAsync(context.ScopeName, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort — never let cleanup throw and mask the original error.
            }

            this.activeState = null;
        }

        /// <inheritdoc />
        internal override async Task<(SyncContext Context, ServerSyncChanges ServerSyncChanges)>
            InternalGetSnapshotAsync(ScopeInfo sScopeInfo, SyncContext context, DbConnection connection = default, DbTransaction transaction = default,
            IProgress<ProgressArgs> progress = null, CancellationToken cancellationToken = default)
        {
            if (!this.IsResumeActive)
                return await base.InternalGetSnapshotAsync(sScopeInfo, context, connection, transaction, progress, cancellationToken).ConfigureAwait(false);

            // Snapshot is downloaded the same way as a regular sync, just without an upload phase.
            // Initialize the state so DownladBatchInfoAsync below can persist progress.
            this.activeState = await this.LoadAndValidateStateAsync(context, null, null, cancellationToken).ConfigureAwait(false);
            this.suppressCleanup = true;
            try
            {
                return await base.InternalGetSnapshotAsync(sScopeInfo, context, connection, transaction, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                this.suppressCleanup = false;
            }
        }

        /// <inheritdoc />
        internal override async Task<SyncContext> InternalEndSessionAsync(SyncContext context, SyncResult result, ServerSyncChanges serverSyncChanges,
            SyncException syncException = default, IProgress<ProgressArgs> progress = null, CancellationToken cancellationToken = default)
        {
            // EndSession only runs when the SyncAgent reached the end of the flow successfully OR is
            // bailing out due to an exception. We only clear resume state on a clean end; otherwise we
            // leave it on disk so the next call can pick it up.
            var sessionContext = await base.InternalEndSessionAsync(context, result, serverSyncChanges, syncException, progress, cancellationToken).ConfigureAwait(false);

            if (syncException == null && this.IsResumeActive)
            {
                try
                {
                    await this.stateStore.DeleteAsync(context.ScopeName, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort cleanup; a stale resume file is harmless because we always validate it.
                }

                this.activeState = null;
            }

            return sessionContext;
        }

        /// <inheritdoc />
        protected internal override async Task DownladBatchInfoAsync(SyncContext context, SyncSet schema, BatchInfo serverBatchInfo,
            HttpMessageSummaryResponse summary, IProgress<ProgressArgs> progress, CancellationToken cancellationToken)
        {
            if (!this.IsResumeActive || this.activeState == null)
            {
                await base.DownladBatchInfoAsync(context, schema, serverBatchInfo, summary, progress, cancellationToken).ConfigureAwait(false);
                return;
            }

            // If we're resuming a download, restore the directory we used previously so the partial
            // files we already have on disk are reused as-is.
            if (!string.IsNullOrEmpty(this.activeState.ServerBatchDirectory))
            {
                serverBatchInfo.DirectoryRoot = Path.GetDirectoryName(this.activeState.ServerBatchDirectory);
                serverBatchInfo.DirectoryName = Path.GetFileName(this.activeState.ServerBatchDirectory);
            }
            else
            {
                // First time we're downloading: capture the directory in the state.
                this.activeState.ServerBatchDirectory = Path.Combine(serverBatchInfo.DirectoryRoot, serverBatchInfo.DirectoryName);
            }

            this.activeState.Phase = ClientResumePhase.Downloading;
            this.activeState.ServerBatchInfo = serverBatchInfo;
            this.activeState.RemoteClientTimestamp = serverBatchInfo.Timestamp;
            await this.stateStore.SaveAsync(this.activeState, cancellationToken).ConfigureAwait(false);

            // Make sure the directory exists before we start writing batch files into it.
            if (!Directory.Exists(serverBatchInfo.GetDirectoryFullPath()))
                Directory.CreateDirectory(serverBatchInfo.GetDirectoryFullPath());

            // Replicate the parallel download loop from the base, but skip already-downloaded batches.
            await this.InterceptAsync(new HttpBatchesDownloadingArgs(context, serverBatchInfo, this.GetServiceHost()), progress, cancellationToken).ConfigureAwait(false);

            var alreadyDone = this.activeState.DownloadedBatchIndexes ?? [];
            var bpis = serverBatchInfo.BatchPartsInfo
                .Where(bpi => !bpi.IsLastBatch && !alreadyDone.Contains(bpi.Index))
                .ToList();

            var lstbpi = serverBatchInfo.BatchPartsInfo.FirstOrDefault(bpi => bpi.IsLastBatch);
            lstbpi ??= serverBatchInfo.BatchPartsInfo.OrderByDescending(bpi => bpi.Index).FirstOrDefault();

            await bpis.ForEachAsync(
                bpi => this.DownloadBatchPartInfoAsync(context, schema, serverBatchInfo, bpi, HttpStep.GetMoreChanges, progress, cancellationToken),
                this.MaxDownladingDegreeOfParallelism).ConfigureAwait(false);

            // Last batch: only re-download if not already on disk.
            if (lstbpi != null && !alreadyDone.Contains(lstbpi.Index))
                await this.DownloadBatchPartInfoAsync(context, schema, serverBatchInfo, lstbpi, HttpStep.GetMoreChanges, progress, cancellationToken).ConfigureAwait(false);

            // Mark the download as complete in the state so a process kill *after* this point
            // resumes correctly and doesn't redo the download.
            this.activeState.Phase = ClientResumePhase.DownloadCompleted;
            await this.stateStore.SaveAsync(this.activeState, cancellationToken).ConfigureAwait(false);

            // Tell the server we're done so it can release its tmp folder. We only do this once all
            // local batches are on disk; if this call fails we'll reach it again on the next sync.
            await this.ProcessRequestAsync<HttpMessageSendChangesResponse>(
                context,
                new HttpMessageGetMoreChangesRequest(context, lstbpi == null ? 0 : lstbpi.Index),
                HttpStep.SendEndDownloadChanges, 0, progress, cancellationToken).ConfigureAwait(false);

            await this.InterceptAsync(new HttpBatchesDownloadedArgs(summary, context, this.GetServiceHost()), progress, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected internal override async Task DownloadBatchPartInfoAsync(SyncContext context, SyncSet schema, BatchInfo serverBatchInfo, BatchPartInfo bpi,
            HttpStep step, IProgress<ProgressArgs> progress, CancellationToken cancellationToken)
        {
            if (!this.IsResumeActive || this.activeState == null)
            {
                await base.DownloadBatchPartInfoAsync(context, schema, serverBatchInfo, bpi, step, progress, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (cancellationToken.IsCancellationRequested || bpi == null)
                return;

            // Skip if already downloaded — the .json file on disk is the source of truth, but we
            // trust the state too. Read under the same lock that mutates the set so we don't
            // race with another parallel-download task adding a sibling batch index.
            lock (this.activeState.DownloadedBatchIndexes)
            {
                if (this.activeState.DownloadedBatchIndexes.Contains(bpi.Index))
                    return;
            }

            await base.DownloadBatchPartInfoAsync(context, schema, serverBatchInfo, bpi, step, progress, cancellationToken).ConfigureAwait(false);

            // Take a snapshot under the lock so we (a) record this index atomically and
            // (b) hand a read-only copy to the serializer. Without the snapshot, JSON
            // serialization can iterate the live HashSet while another concurrent task
            // mutates it, producing torn JSON or InvalidOperationException.
            ClientResumeState snapshot;
            lock (this.activeState.DownloadedBatchIndexes)
            {
                this.activeState.DownloadedBatchIndexes.Add(bpi.Index);
                snapshot = CloneForSave(this.activeState);
            }

            await this.stateStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns a shallow copy of the resume state with a fresh <see cref="HashSet{T}"/> for
        /// <see cref="ClientResumeState.DownloadedBatchIndexes"/>, so the serializer can iterate
        /// it without contending with the live set being mutated by other parallel-download tasks.
        /// </summary>
        private static ClientResumeState CloneForSave(ClientResumeState src) => new()
        {
            ScopeName = src.ScopeName,
            ClientScopeId = src.ClientScopeId,
            ParametersHash = src.ParametersHash,
            SessionId = src.SessionId,
            Phase = src.Phase,
            LastUploadedBatchIndex = src.LastUploadedBatchIndex,
            ClientBatchDirectory = src.ClientBatchDirectory,
            ServerBatchDirectory = src.ServerBatchDirectory,
            ServerBatchInfo = src.ServerBatchInfo,
            RemoteClientTimestamp = src.RemoteClientTimestamp,
            DownloadedBatchIndexes = new HashSet<int>(src.DownloadedBatchIndexes),
            LastUpdatedUtc = src.LastUpdatedUtc,
        };

        /// <inheritdoc />
        protected internal override Task WebRemoteCleanFolderAsync(SyncContext context, BatchInfo changes)
        {
            // While a resume token is alive, partial download/upload folders MUST stay on disk so the
            // next call can pick them up.
            if (this.suppressCleanup)
                return Task.CompletedTask;

            return base.WebRemoteCleanFolderAsync(context, changes);
        }

        private bool IsResumeActive => this.Options != null && this.Options.Resumable && this.stateStore != null;

        /// <summary>
        /// Loads the saved state for this scope, or creates a fresh one if none exists. If a state
        /// exists but doesn't match the current parameters or client scope id, it is discarded — we
        /// can't safely replay batches generated against a different filter set.
        /// </summary>
        private async Task<ClientResumeState> LoadAndValidateStateAsync(SyncContext context, ScopeInfoClient cScopeInfoClient, ClientSyncChanges clientChanges, CancellationToken cancellationToken)
        {
            var saved = await this.stateStore.LoadAsync(context.ScopeName, cancellationToken).ConfigureAwait(false);

            var paramHash = context.Hash; // already computed from context.Parameters
            var clientScopeId = cScopeInfoClient?.Id ?? context.ClientId ?? Guid.Empty;

            var canReuse = saved != null
                && saved.SessionId == context.SessionId
                && string.Equals(saved.ScopeName, context.ScopeName, SyncGlobalization.DataSourceStringComparison)
                && (saved.ClientScopeId == Guid.Empty || saved.ClientScopeId == clientScopeId)
                && string.Equals(saved.ParametersHash ?? string.Empty, paramHash ?? string.Empty, StringComparison.Ordinal)
                && saved.Phase != ClientResumePhase.None
                && saved.Phase != ClientResumePhase.Applied
                && DiskReferencesStillValid(saved);

            if (canReuse)
            {
                // Defensive: hydrate fields the constructor's default would have set but that may
                // be missing from older or partial JSON (e.g. EmitDefaultValue=false drops empty sets).
                saved.DownloadedBatchIndexes ??= [];
                return saved;
            }

            // Drop a stale state if one is sitting on disk so it can't be reused later by accident.
            if (saved != null)
                await this.stateStore.DeleteAsync(context.ScopeName, cancellationToken).ConfigureAwait(false);

            var clientBatchDir = clientChanges?.ClientBatchInfo == null
                ? null
                : Path.Combine(clientChanges.ClientBatchInfo.DirectoryRoot ?? string.Empty, clientChanges.ClientBatchInfo.DirectoryName ?? string.Empty);

            return new ClientResumeState
            {
                ScopeName = context.ScopeName,
                ClientScopeId = clientScopeId,
                ParametersHash = paramHash,
                SessionId = context.SessionId,
                Phase = ClientResumePhase.Uploading,
                LastUploadedBatchIndex = -1,
                ClientBatchDirectory = clientBatchDir,
                DownloadedBatchIndexes = [],
            };
        }

        /// <summary>
        /// Returns false if the saved state references a directory we expect to exist on disk but
        /// is missing — e.g., a temp folder that was wiped between attempts. In that case we can't
        /// safely resume because the batch files referenced by the state are gone, so we treat the
        /// state as unusable and start fresh.
        /// </summary>
        private static bool DiskReferencesStillValid(ClientResumeState saved)
        {
            // If we'd already entered the download phase, we expect the server batch directory
            // (and the .json batch files inside it) to be on disk. Missing directory => state is
            // dead and any reuse would NRE on the next file read.
            if (saved.Phase >= ClientResumePhase.Downloading
                && !string.IsNullOrEmpty(saved.ServerBatchDirectory)
                && !Directory.Exists(saved.ServerBatchDirectory))
            {
                return false;
            }

            // ClientBatchDirectory is captured at state-create time before the upload starts. If it's
            // gone, the upload loop can't read its source files.
            if (saved.Phase == ClientResumePhase.Uploading
                && !string.IsNullOrEmpty(saved.ClientBatchDirectory)
                && !Directory.Exists(saved.ClientBatchDirectory))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Resumable variant of the base <c>InternalApplyThenGetChangesAsync</c>. The shape is the
        /// same — upload everything, then receive the server's manifest and download — but the upload
        /// loop consults the resume state to skip batches that were posted in a previous attempt.
        /// </summary>
        private async Task<(SyncContext Context, ServerSyncChanges ServerSyncChanges, ConflictResolutionPolicy ServerResolutionPolicy)>
            InternalApplyThenGetChangesResumableAsync(ScopeInfoClient cScopeInfoClient, ScopeInfo cScopeInfo, SyncContext context, ClientSyncChanges clientChanges,
            IProgress<ProgressArgs> progress, CancellationToken cancellationToken)
        {
            // Defensive top-level guards so a corrupted activeState can never NRE here.
            // If activeState is null we somehow got past LoadAndValidateStateAsync without one;
            // wipe the persisted state and let the caller see a precise diagnostic.
            if (this.activeState == null)
                throw new InvalidOperationException("Resumable orchestrator entered the upload path without an active resume state. Internal invariant violated.");

            // Older saved states may have been serialized without the DownloadedBatchIndexes set
            // (EmitDefaultValue=false drops empty collections). Rehydrate so the parallel-download
            // path can lock and mutate it safely.
            this.activeState.DownloadedBatchIndexes ??= [];

            var schema = cScopeInfo.Schema;
            schema.EnsureSchema();

            clientChanges.ClientBatchInfo ??= new BatchInfo();

            // ----------------------------------------------------------------
            // STEP 1: upload, skipping anything already posted
            // ----------------------------------------------------------------
            HttpResponseMessage response = null;

            if (clientChanges.ClientBatchInfo.BatchPartsInfo.Count == 0)
            {
                // No client changes to upload — same as base: send a single empty request to trigger
                // the server's response with the summary.
                var changesToSend = new HttpMessageSendChangesRequest(context, cScopeInfoClient) { ClientLastSyncTimestamp = clientChanges.ClientTimestamp };
                context.ProgressPercentage += 0.125;
                await this.InterceptAsync(new HttpSendingClientChangesRequestArgs(changesToSend, 0, 0, this.GetServiceHost()), progress, cancellationToken).ConfigureAwait(false);
                response = await this.ProcessRequestAsync(changesToSend, HttpStep.SendChangesInProgress, this.Options.BatchSize, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                int tmpRowsSendedCount = 0;
                var initialPctProgress1 = context.ProgressPercentage;
                using var localSerializer = new LocalJsonSerializer(this, context);

                var orderedParts = clientChanges.ClientBatchInfo.BatchPartsInfo.OrderBy(bpi => bpi.Index).ToList();
                bool sentIsLastBatch = false;
                foreach (var bpi in orderedParts)
                {
                    // Resume guard: skip any batch already posted to the server in a previous attempt.
                    if (bpi.Index <= this.activeState.LastUploadedBatchIndex)
                        continue;

                    // The batch references a table by name/schema. Make sure the current schema
                    // actually has that table — if it doesn't (schema drift between runs), the
                    // saved batch is unusable and we have to give up the resume.
                    var schemaTableForBpi = schema.Tables[bpi.TableName, bpi.SchemaName];
                    if (schemaTableForBpi == null)
                        throw new InvalidOperationException(
                            $"Resumable upload references table '{bpi.SchemaName}.{bpi.TableName}' which is not present in the current schema. " +
                            "The persisted batch info appears to be stale; clear the resume state and try again.");

                    var schemaTable = CreateChangesTable(schemaTableForBpi, excludeShadow: true);

                    var changesToSend = new HttpMessageSendChangesRequest(context, cScopeInfoClient)
                    {
                        IsLastBatch = bpi.IsLastBatch,
                        BatchIndex = bpi.Index,
                        BatchCount = clientChanges.ClientBatchInfo.BatchPartsInfo.Count,
                        ClientLastSyncTimestamp = clientChanges.ClientTimestamp,
                    };

                    var containerTable = new ContainerTable(schemaTable);
                    changesToSend.Changes.Tables.Add(containerTable);

                    var fullPath = Path.Combine(clientChanges.ClientBatchInfo.GetDirectoryFullPath(), bpi.FileName);
                    foreach (var row in localSerializer.GetRowsFromFile(fullPath, schemaTable))
                    {
                        if (this.Converter != null && row.Length > 0)
                            this.Converter.BeforeSerialize(row, schemaTable);
                        containerTable.Rows.Add(row.ToArray());
                    }

                    tmpRowsSendedCount += containerTable.Rows.Count;
                    context.ProgressPercentage = initialPctProgress1 + ((changesToSend.BatchIndex + 1) * 0.2d / changesToSend.BatchCount);
                    await this.InterceptAsync(new HttpSendingClientChangesRequestArgs(changesToSend, tmpRowsSendedCount, clientChanges.ClientBatchInfo.RowsCount, this.GetServiceHost()), progress, cancellationToken).ConfigureAwait(false);

                    response = await this.ProcessRequestAsync(changesToSend, HttpStep.SendChangesInProgress, this.Options.BatchSize, progress, cancellationToken).ConfigureAwait(false);

                    // Persist progress *before* releasing the response handle so a crash mid-loop is
                    // safe to resume.
                    this.activeState.LastUploadedBatchIndex = bpi.Index;
                    if (bpi.IsLastBatch)
                    {
                        this.activeState.Phase = ClientResumePhase.UploadCompleted;
                        sentIsLastBatch = true;
                    }

                    await this.stateStore.SaveAsync(this.activeState, cancellationToken).ConfigureAwait(false);

                    if (!bpi.IsLastBatch)
                        response.Dispose();
                }

                // Two cases require a synthesized "empty" final request to retrieve the summary:
                //   1. The loop ran zero times (every batch was already uploaded in a previous attempt).
                //   2. The loop ran but didn't send a batch flagged IsLastBatch=true (shouldn't happen
                //      with a well-formed BatchInfo, but defensive — without IsLastBatch=true the server
                //      keeps returning the early "still receiving" summary that has BatchInfo=null,
                //      which would NRE when we deref summaryResponseContent.BatchInfo below).
                // In both cases, dispose the previous response (if any) and send a final IsLastBatch
                // request so the server runs apply and returns a real summary.
                if (response == null || !sentIsLastBatch)
                {
                    response?.Dispose();
                    var lastIndex = orderedParts.Count == 0 ? 0 : orderedParts[^1].Index;
                    var changesToSend = new HttpMessageSendChangesRequest(context, cScopeInfoClient)
                    {
                        IsLastBatch = true,
                        BatchIndex = lastIndex,
                        BatchCount = orderedParts.Count,
                        ClientLastSyncTimestamp = clientChanges.ClientTimestamp,
                    };
                    response = await this.ProcessRequestAsync(changesToSend, HttpStep.SendChangesInProgress, this.Options.BatchSize, progress, cancellationToken).ConfigureAwait(false);
                    this.activeState.Phase = ClientResumePhase.UploadCompleted;
                    await this.stateStore.SaveAsync(this.activeState, cancellationToken).ConfigureAwait(false);
                }
            }

            // ----------------------------------------------------------------
            // STEP 2: receive the summary, then download (resumably) all server batches
            // ----------------------------------------------------------------
            var serverBatchInfo = new BatchInfo();
            try
            {
                context.SyncStage = SyncStage.ChangesSelecting;
                context.ProgressPercentage = 0.55;

                HttpMessageSummaryResponse summaryResponseContent;
                using (var streamResponse = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    var responseSerializer = this.SerializerFactory.GetSerializer();
                    summaryResponseContent = await responseSerializer.DeserializeAsync<HttpMessageSummaryResponse>(streamResponse).ConfigureAwait(false);
                    context = summaryResponseContent.SyncContext;
                    await this.InterceptAsync(
                        new HttpGettingResponseMessageArgs(response, this.ServiceUri, HttpStep.SendChangesInProgress, context, summaryResponseContent, this.GetServiceHost()),
                        progress, cancellationToken).ConfigureAwait(false);
                }

                // The server only populates BatchInfo on a real summary (after IsLastBatch=true was
                // delivered). If we somehow ended up with an early "still receiving" summary, fail
                // fast with a clear error rather than NRE'ing on the next line.
                if (summaryResponseContent.BatchInfo == null)
                {
                    throw new InvalidOperationException(
                        "Resumable upload received an early summary with no BatchInfo. This usually means " +
                        "no batch with IsLastBatch=true was sent to the server in this attempt. The resume " +
                        "state may be stale; deleting it and restarting the sync should recover.");
                }

                serverBatchInfo.RowsCount = summaryResponseContent.BatchInfo.RowsCount;
                serverBatchInfo.Timestamp = summaryResponseContent.RemoteClientTimestamp;
                if (summaryResponseContent.BatchInfo.BatchPartsInfo != null)
                {
                    foreach (var bpi in summaryResponseContent.BatchInfo.BatchPartsInfo)
                        serverBatchInfo.BatchPartsInfo.Add(bpi);
                }

                // Reuse the server batch directory across attempts when possible.
                if (!string.IsNullOrEmpty(this.activeState.ServerBatchDirectory))
                {
                    serverBatchInfo.DirectoryRoot = Path.GetDirectoryName(this.activeState.ServerBatchDirectory);
                    serverBatchInfo.DirectoryName = Path.GetFileName(this.activeState.ServerBatchDirectory);
                }
                else
                {
                    serverBatchInfo.DirectoryRoot = this.Options.BatchDirectory;
                    serverBatchInfo.DirectoryName = string.Concat(
                        "WEB_REMOTE_GETCHANGES_",
                        DateTime.UtcNow.ToString("yyyy_MM_dd_ss", CultureInfo.InvariantCulture),
                        Path.GetRandomFileName().Replace(".", string.Empty));
                }

                await this.DownladBatchInfoAsync(context, schema, serverBatchInfo, summaryResponseContent, progress, cancellationToken).ConfigureAwait(false);

                this.CompleteTime = DateTime.UtcNow;

                var serverSyncChanges = new ServerSyncChanges(
                    summaryResponseContent.RemoteClientTimestamp,
                    serverBatchInfo,
                    summaryResponseContent.ServerChangesSelected,
                    summaryResponseContent.ClientChangesApplied);

                return (context, serverSyncChanges, summaryResponseContent.ConflictResolutionPolicy);
            }
            catch (HttpSyncWebException)
            {
                // Don't delete the server batch folder on transient HTTP errors — we want to resume
                // from it on the next call.
                throw;
            }
            catch (Exception ex)
            {
                throw this.GetSyncError(context, ex);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }
}
