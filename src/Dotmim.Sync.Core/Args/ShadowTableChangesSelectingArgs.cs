using Dotmim.Sync.Batch;
using Dotmim.Sync.Enumerations;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    /// <summary>
    /// Raised when changes are being selected for a shadow table (server has no physical table).
    /// There is no database query: use <see cref="AddOrEdit(Action{SyncRow})"/> or <see cref="AddOrEdit(Func{SyncRow, Task})"/> to enqueue rows for download.
    /// Those rows are written to the upsert batch (<see cref="SyncRowState.Modified"/>); when the client applies them, an existing row with the same primary key is updated and a missing row is inserted.
    /// Use <see cref="DeleteRow"/> to enqueue deletes by primary key value(s). If you enqueue nothing, this shadow table sends no data for the current cycle.
    /// </summary>
    public class ShadowTableChangesSelectingArgs : ProgressArgs
    {
        /// <inheritdoc cref="ShadowTableChangesSelectingArgs"/>
        public ShadowTableChangesSelectingArgs(
            SyncContext context,
            SyncTable schemaChangesTable,
            TableChangesSelected tableChangesSelected,
            BatchInfo batchInfo,
            Func<SyncRow, Task> addUpsertAsync,
            Func<SyncRow, Task> addDeleteAsync,
            DbConnection connection,
            DbTransaction transaction)
            : base(context, connection, transaction)
        {
            this.SchemaChangesTable = schemaChangesTable;
            this.TableChangesSelected = tableChangesSelected;
            this.BatchInfo = batchInfo;
            this.addUpsertAsync = addUpsertAsync ?? throw new ArgumentNullException(nameof(addUpsertAsync));
            this.addDeleteAsync = addDeleteAsync ?? throw new ArgumentNullException(nameof(addDeleteAsync));
        }

        /// <summary>
        /// Gets the in-memory schema used for serializing change rows (same shape as the client table).
        /// </summary>
        public SyncTable SchemaChangesTable { get; }

        /// <summary>
        /// Gets the running statistics for this table within the current get-changes operation.
        /// </summary>
        public TableChangesSelected TableChangesSelected { get; }

        /// <summary>
        /// Gets the batch directory information for the current session.
        /// </summary>
        public BatchInfo BatchInfo { get; }

        private readonly Func<SyncRow, Task> addUpsertAsync;
        private readonly Func<SyncRow, Task> addDeleteAsync;

        /// <summary>
        /// Enqueues an insert or update for download: builds a row with <see cref="SyncRowState.Modified"/>, invokes <paramref name="configure"/>, then serializes it into the upsert batch.
        /// On the client, the same primary key as an existing row results in an update; otherwise a new row is inserted.
        /// </summary>
        public Task AddOrEdit(Action<SyncRow> configure, CancellationToken cancellationToken = default)
        {
            Guard.ThrowIfNull(configure);
            cancellationToken.ThrowIfCancellationRequested();

            var row = this.SchemaChangesTable.NewRow(SyncRowState.Modified);
            configure(row);
            return this.addUpsertAsync(row);
        }

        /// <summary>
        /// Same as <see cref="AddOrEdit(Action{SyncRow})"/>, but the configuration callback is asynchronous.
        /// </summary>
        public async Task AddOrEdit(Func<SyncRow, Task> configureAsync, CancellationToken cancellationToken = default)
        {
            Guard.ThrowIfNull(configureAsync);
            cancellationToken.ThrowIfCancellationRequested();

            var row = this.SchemaChangesTable.NewRow(SyncRowState.Modified);
            await configureAsync(row).ConfigureAwait(false);
            await this.addUpsertAsync(row).ConfigureAwait(false);
        }

        /// <summary>
        /// Enqueues a delete for the row identified by primary key value(s), in the same order as <see cref="SyncTable.PrimaryKeys"/>.
        /// For a single-column primary key, pass that value only (for example <c>await args.DeleteRow(id);</c>).
        /// </summary>
        /// <param name="primaryKeyValues">One value per primary key column, in schema order.</param>
        public Task DeleteRow(params object[] primaryKeyValues)
        {
            Guard.ThrowIfNull(primaryKeyValues);

            if (primaryKeyValues.Length == 0)
                throw new ArgumentException("At least one primary key value is required.", nameof(primaryKeyValues));

            var pks = this.SchemaChangesTable.PrimaryKeys;
            if (pks == null || pks.Count == 0)
                throw new InvalidOperationException($"Shadow table {this.SchemaChangesTable.GetFullName()} has no primary keys defined.");

            if (pks.Count != primaryKeyValues.Length)
                throw new ArgumentException($"This table has {pks.Count} primary key column(s); provide exactly that many values in key order.", nameof(primaryKeyValues));

            var row = this.SchemaChangesTable.NewRow(SyncRowState.Deleted);
            for (var i = 0; i < pks.Count; i++)
                row[pks[i]] = primaryKeyValues[i];

            return this.addDeleteAsync(row);
        }

        /// <inheritdoc cref="ProgressArgs.ProgressLevel"/>
        public override SyncProgressLevel ProgressLevel => SyncProgressLevel.Debug;

        /// <inheritdoc cref="ProgressArgs.Message"/>
        public override string Message => $"[{this.SchemaChangesTable.GetFullName()}] Shadow table changes selecting.";

        /// <inheritdoc cref="ProgressArgs.EventId"/>
        public override int EventId => 13260;
    }

    /// <summary>
    /// Interceptor helpers for shadow tables.
    /// </summary>
    public partial class InterceptorsExtensions
    {
        /// <summary>
        /// Occurs when changes are being composed for a shadow table on the server (no database query).
        /// Use <see cref="ShadowTableChangesSelectingArgs.AddOrEdit(Action{SyncRow})"/> and <see cref="ShadowTableChangesSelectingArgs.DeleteRow"/> to enqueue changes.
        /// </summary>
        public static Guid OnShadowTableChangesSelecting(this BaseOrchestrator orchestrator, Action<ShadowTableChangesSelectingArgs> action)
            => orchestrator.AddInterceptor(action);

        /// <inheritdoc cref="OnShadowTableChangesSelecting(BaseOrchestrator, Action{ShadowTableChangesSelectingArgs})"/>
        public static Guid OnShadowTableChangesSelecting(this BaseOrchestrator orchestrator, Func<ShadowTableChangesSelectingArgs, Task> action)
            => orchestrator.AddInterceptor(action);
    }
}
