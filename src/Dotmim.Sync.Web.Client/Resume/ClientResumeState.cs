using Dotmim.Sync.Batch;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Dotmim.Sync.Web.Client.Resume
{
    /// <summary>
    /// Represents the persisted state of an in-flight resumable sync session, owned by the client.
    /// <para>
    /// A <see cref="ClientResumeState"/> is written to durable storage (via
    /// <see cref="IClientResumeStateStore"/>) every time a batch is successfully transferred. On the
    /// next <c>SynchronizeAsync</c> call, the resumable orchestrator loads the state, reuses the
    /// previous <see cref="SessionId"/>, and skips any batch that has already been uploaded or
    /// downloaded.
    /// </para>
    /// </summary>
    [DataContract(Name = "rstate"), Serializable]
    public class ClientResumeState
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClientResumeState"/> class.
        /// </summary>
        public ClientResumeState()
        {
            this.DownloadedBatchIndexes = [];
            this.LastUploadedBatchIndex = -1;
            this.LastUpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets or sets the named scope this resume state belongs to.
        /// </summary>
        [DataMember(Name = "sn", IsRequired = true, Order = 1)]
        public string ScopeName { get; set; }

        /// <summary>
        /// Gets or sets the client scope id (the per-client identifier persisted in
        /// <c>scope_info_client</c>). Combined with <see cref="ScopeName"/> it forms the storage key.
        /// </summary>
        [DataMember(Name = "csid", IsRequired = true, Order = 2)]
        public Guid ClientScopeId { get; set; }

        /// <summary>
        /// Gets or sets the parameters hash that was used when this session was started. The state is
        /// only reused on the next sync if the hash matches, otherwise we cannot guarantee the saved
        /// batches are still relevant.
        /// </summary>
        [DataMember(Name = "h", IsRequired = false, Order = 3)]
        public string ParametersHash { get; set; }

        /// <summary>
        /// Gets or sets the session id of the in-flight sync. The resumable orchestrator forces this
        /// id back into the <see cref="SyncContext"/> on the next call so the server can reattach to
        /// its own <c>SessionCache</c>.
        /// </summary>
        [DataMember(Name = "sid", IsRequired = true, Order = 4)]
        public Guid SessionId { get; set; }

        /// <summary>
        /// Gets or sets the resumable transfer phase the previous sync was in when it stopped.
        /// </summary>
        [DataMember(Name = "ph", IsRequired = true, Order = 5)]
        public ClientResumePhase Phase { get; set; }

        /// <summary>
        /// Gets or sets the highest batch index that was successfully posted to the server during the
        /// upload phase. Defaults to <c>-1</c>, meaning no batch has been uploaded yet.
        /// </summary>
        [DataMember(Name = "lub", IsRequired = false, Order = 6)]
        public int LastUploadedBatchIndex { get; set; }

        /// <summary>
        /// Gets or sets the directory containing the local <c>ClientBatchInfo</c> being uploaded. The
        /// resumable orchestrator keeps this folder around (bypassing <see cref="SyncOptions.CleanFolder"/>)
        /// for as long as the resume state references it.
        /// </summary>
        [DataMember(Name = "cbd", IsRequired = false, EmitDefaultValue = false, Order = 7)]
        public string ClientBatchDirectory { get; set; }

        /// <summary>
        /// Gets or sets the local directory where partially downloaded server batches are accumulated.
        /// </summary>
        [DataMember(Name = "sbd", IsRequired = false, EmitDefaultValue = false, Order = 8)]
        public string ServerBatchDirectory { get; set; }

        /// <summary>
        /// Gets or sets the manifest of server batches we are downloading. Captured from the
        /// <c>HttpMessageSummaryResponse</c> after the upload phase completes. <c>null</c> while the
        /// session is still in <see cref="ClientResumePhase.Uploading"/>.
        /// </summary>
        [DataMember(Name = "sbi", IsRequired = false, EmitDefaultValue = false, Order = 9)]
        public BatchInfo ServerBatchInfo { get; set; }

        /// <summary>
        /// Gets or sets the remote client timestamp returned by the server for this session.
        /// </summary>
        [DataMember(Name = "rct", IsRequired = false, Order = 10)]
        public long RemoteClientTimestamp { get; set; }

        /// <summary>
        /// Gets or sets the set of batch indexes that have been fully downloaded and persisted on the
        /// client. Used to skip already-completed batches on resume.
        /// </summary>
        [DataMember(Name = "dbi", IsRequired = false, EmitDefaultValue = false, Order = 11)]
        public HashSet<int> DownloadedBatchIndexes { get; set; }

        /// <summary>
        /// Gets or sets when this state was last updated. Useful for diagnostics and TTL-style cleanup.
        /// </summary>
        [DataMember(Name = "lu", IsRequired = false, Order = 12)]
        public DateTime LastUpdatedUtc { get; set; }
    }

    /// <summary>
    /// Phases of a resumable sync session, from the client perspective.
    /// </summary>
    public enum ClientResumePhase
    {
        /// <summary>
        /// No state yet, or the previous session has been cleaned up successfully.
        /// </summary>
        None = 0,

        /// <summary>
        /// The client is uploading its own batches to the server.
        /// </summary>
        Uploading = 1,

        /// <summary>
        /// All client batches have been uploaded and the server's manifest has been received.
        /// </summary>
        UploadCompleted = 2,

        /// <summary>
        /// The client is downloading server batches.
        /// </summary>
        Downloading = 3,

        /// <summary>
        /// All server batches have been downloaded but the local apply has not yet completed.
        /// </summary>
        DownloadCompleted = 4,

        /// <summary>
        /// The local apply succeeded; the resume state should be discarded on the next save.
        /// </summary>
        Applied = 5,
    }
}
