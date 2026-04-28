using Dotmim.Sync.Enumerations;
using System;
using System.Data.Common;
using System.Threading.Tasks;

namespace Dotmim.Sync
{

    /// <summary>
    /// Contains SyncRow selected to be written in the batchpart info.
    /// </summary>
    public class RowsChangesSelectedArgs : ProgressArgs
    {
        /// <inheritdoc cref="RowsChangesSelectedArgs" />
        public RowsChangesSelectedArgs(SyncContext context, SyncRow syncRow, SyncTable schemaTable, DbConnection connection, DbTransaction transaction)
            : base(context, connection, transaction)
        {
            this.SyncRow = syncRow;
            this.SchemaTable = schemaTable;
        }

        /// <summary>
        /// Gets or Sets the sync row selected from the underline table.
        /// You can change this sync row here, before it's serialized on disk.
        /// </summary>
        public SyncRow SyncRow { get; set; }

        /// <summary>
        /// Gets the table schema.
        /// </summary>
        public SyncTable SchemaTable { get; }

        /// <summary>
        /// Set a value for a shadow column on the current row.
        /// If the shadow column does not yet exist on the schema table, it is added automatically.
        /// The row buffer is expanded as needed.
        /// </summary>
        public void SetShadowColumnValue(string columnName, Type type, object value)
        {
            var existing = this.SchemaTable.Columns[columnName];
            if (existing == null)
            {
                this.SchemaTable.AddShadowColumn(columnName, type);
                this.SyncRow.ExpandBuffer(this.SchemaTable);
            }
            else if (this.SyncRow.Length < this.SchemaTable.Columns.Count)
            {
                this.SyncRow.ExpandBuffer(this.SchemaTable);
            }

            this.SyncRow[columnName] = value;
        }

        /// <inheritdoc cref="ProgressArgs.ProgressLevel"/>
        public override SyncProgressLevel ProgressLevel => SyncProgressLevel.Trace;

        /// <inheritdoc cref="ProgressArgs.Message"/>
        public override string Message => $"[{this.SchemaTable.GetFullName()}] [SyncRow] {this.SyncRow.ToString()}.";

        /// <inheritdoc cref="ProgressArgs.EventId"/>
        public override int EventId => 13051;
    }

    /// <summary>
    /// Interceptor extension methods.
    /// </summary>
    public partial class InterceptorsExtensions
    {

        /// <summary>
        /// Intercept the provider action when a sync row is about to be serialized in a batch part info after have been selected from the data source.
        /// <example>
        /// <code>
        /// var localOrchestrator = new LocalOrchestrator(clientProvider);
        /// localOrchestrator.OnRowsChangesSelected(args =>
        /// {
        ///     Console.WriteLine($"Row read from local database for table:{args.SchemaTable.GetFullName()}");
        ///     Console.WriteLine($"{args.SyncRow}");
        /// });
        /// </code>
        /// </example>
        /// </summary>
        public static Guid OnRowsChangesSelected(this BaseOrchestrator orchestrator, Action<RowsChangesSelectedArgs> action)
            => orchestrator.AddInterceptor(action);

        /// <inheritdoc cref="OnRowsChangesSelected(BaseOrchestrator, Action{RowsChangesSelectedArgs})"/>
        public static Guid OnRowsChangesSelected(this BaseOrchestrator orchestrator, Func<RowsChangesSelectedArgs, Task> action)
            => orchestrator.AddInterceptor(action);
    }
}