using Dotmim.Sync.DatabaseStringParsers;
using Dotmim.Sync.Enumerations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;

namespace Dotmim.Sync
{
    /// <summary>
    /// Represents a table to be synchronized.
    /// </summary>
    [DataContract(Name = "st"), Serializable]
    public class SetupTable : SyncNamedItem<SetupTable>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SetupTable"/> class.
        /// public ctor for serialization purpose.
        /// </summary>
        public SetupTable()
        {
        }

        /// <summary>
        /// Gets or Sets the table name.
        /// </summary>
        [DataMember(Name = "tn", IsRequired = true, Order = 1)]
        public string TableName { get; set; }

        /// <summary>
        /// Gets or Sets the schema name.
        /// </summary>
        [DataMember(Name = "sn", IsRequired = false, EmitDefaultValue = false, Order = 2)]
        public string SchemaName { get; set; }

        /// <summary>
        /// Gets or Sets the table columns collection.
        /// </summary>
        [DataMember(Name = "cols", IsRequired = false, EmitDefaultValue = false, Order = 3)]
        public SetupColumns Columns { get; set; }

        /// <summary>
        /// Gets or Sets the Sync direction (may be Bidirectional, DownloadOnly, UploadOnly)
        /// Default is Bidirectional.
        /// </summary>
        [DataMember(Name = "sd", IsRequired = false, EmitDefaultValue = false, Order = 4)]
        public SyncDirection SyncDirection { get; set; }

        /// <summary>
        /// Gets or Sets the shadow columns collection.
        /// Shadow columns do not exist in the server database; they are created on the client at provisioning time
        /// and their values are populated at runtime (e.g. in the OnRowsChangesSelected interceptor).
        /// </summary>
        [DataMember(Name = "shcols", IsRequired = false, EmitDefaultValue = false, Order = 5)]
        public Collection<SetupShadowColumn> ShadowColumns { get; set; }

        /// <summary>
        /// Gets or sets column names to omit from synchronization for this table.
        /// Applied after the include list in <see cref="Columns"/> (if any): the effective column set is (included columns) minus (excluded columns).
        /// Exclusions apply only to columns that exist on the data source; primary key columns cannot be excluded.
        /// </summary>
        [DataMember(Name = "ecols", IsRequired = false, EmitDefaultValue = false, Order = 6)]
        public SetupColumns ExcludedColumns { get; set; }

        /// <summary>
        /// Gets or sets column names that must participate in synchronization for this table even if they are listed in
        /// <see cref="SyncSetup.GlobalExcludedColumns"/> or in the owning setup's <see cref="SyncSetup.ExcludedColumns"/>.
        /// This acts as a per-table bypass of the "general" (global / setup-level) exclusion rules and does not override
        /// an entry in this same table's <see cref="ExcludedColumns"/>.
        /// </summary>
        [DataMember(Name = "icols", IsRequired = false, EmitDefaultValue = false, Order = 7)]
        public SetupColumns IncludedColumns { get; set; }

        /// <summary>
        /// Gets a value indicating whether check if SetupTable has columns. If not columns specified, all the columns from server database are retrieved.
        /// </summary>
        [IgnoreDataMember]
        public bool HasColumns => this.Columns?.Count > 0;

        /// <summary>
        /// Gets a value indicating whether this setup table has excluded columns defined.
        /// </summary>
        [IgnoreDataMember]
        public bool HasExcludedColumns => this.ExcludedColumns?.Count > 0;

        /// <summary>
        /// Gets a value indicating whether this setup table has per-table re-included columns defined
        /// (columns that bypass <see cref="SyncSetup.GlobalExcludedColumns"/> / <see cref="SyncSetup.ExcludedColumns"/>).
        /// </summary>
        [IgnoreDataMember]
        public bool HasIncludedColumns => this.IncludedColumns?.Count > 0;

        /// <summary>
        /// Gets a value indicating whether this SetupTable has shadow columns defined.
        /// </summary>
        [IgnoreDataMember]
        public bool HasShadowColumns => this.ShadowColumns?.Count > 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="SetupTable"/> class.
        /// Specify a table to add to the sync process
        /// If you don't specify any columns, all columns in the data source will be imported.
        /// </summary>
        public SetupTable(string tableName, string schemaName = null)
        {
            Guard.ThrowIfNull(tableName);

            var fullName = string.IsNullOrEmpty(schemaName) ? tableName : $"{schemaName}.{tableName}";

            // Potentially user can pass something like [SalesLT].[Product]
            // or SalesLT.Product or Product. TableParser will handle it
            var tableParser = new TableParser(fullName);

            this.TableName = tableParser.TableName;

            // https://github.com/Mimetis/Dotmim.Sync/issues/621#issuecomment-968369322
            this.SchemaName = string.IsNullOrEmpty(tableParser.SchemaName) ? string.Empty : tableParser.SchemaName;

            this.Columns = [];
            this.ExcludedColumns = [];
            this.IncludedColumns = [];
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SetupTable"/> class.
        /// Specify a table and its columns, to add to the sync process
        /// If you're specifying some columns, all others columns in the data source will be ignored.
        /// </summary>
        public SetupTable(string tableName, IEnumerable<string> columnsName, string schemaName = null)
            : this(tableName, schemaName) => this.Columns.AddRange(columnsName);

        /// <summary>
        /// Add a shadow column definition. Shadow columns are created on the client database at provisioning time
        /// and their values are set at runtime via the OnRowsChangesSelected interceptor.
        /// </summary>
        public SetupTable AddShadowColumn<T>(string columnName)
        {
            this.ShadowColumns ??= [];
            if (this.ShadowColumns.Any(c => string.Equals(c.ColumnName, columnName, SyncGlobalization.DataSourceStringComparison)))
                throw new Exception($"Shadow column {columnName} already exists in the table {this.TableName}");

            this.ShadowColumns.Add(new SetupShadowColumn(columnName, typeof(T)));
            return this;
        }

        /// <summary>
        /// Add a shadow column definition with an explicit .NET type.
        /// </summary>
        public SetupTable AddShadowColumn(string columnName, Type type)
        {
            this.ShadowColumns ??= [];
            if (this.ShadowColumns.Any(c => string.Equals(c.ColumnName, columnName, SyncGlobalization.DataSourceStringComparison)))
                throw new Exception($"Shadow column {columnName} already exists in the table {this.TableName}");

            this.ShadowColumns.Add(new SetupShadowColumn(columnName, type));
            return this;
        }

        /// <summary>
        /// Exclude a column from synchronization (must exist on the data source; cannot be a primary key column).
        /// </summary>
        public SetupTable ExcludeColumn(string columnName)
        {
            this.ExcludedColumns ??= [];
            this.ExcludedColumns.Add(columnName);
            return this;
        }

        /// <summary>
        /// Exclude multiple columns from synchronization.
        /// </summary>
        public SetupTable ExcludeColumns(params string[] columnNames)
        {
            this.ExcludedColumns ??= [];
            this.ExcludedColumns.AddRange(columnNames);
            return this;
        }

        /// <summary>
        /// Re-include a column on this table even if it appears in <see cref="SyncSetup.GlobalExcludedColumns"/>
        /// or in the owning setup's <see cref="SyncSetup.ExcludedColumns"/>. Cannot bypass this table's own
        /// <see cref="ExcludedColumns"/>. The column must still exist on the data source.
        /// </summary>
        public SetupTable IncludeColumn(string columnName)
        {
            this.IncludedColumns ??= [];
            this.IncludedColumns.Add(columnName);
            return this;
        }

        /// <summary>
        /// Re-include multiple columns on this table even if they appear in <see cref="SyncSetup.GlobalExcludedColumns"/>
        /// or in the owning setup's <see cref="SyncSetup.ExcludedColumns"/>.
        /// </summary>
        public SetupTable IncludeColumns(params string[] columnNames)
        {
            this.IncludedColumns ??= [];
            this.IncludedColumns.AddRange(columnNames);
            return this;
        }

        /// <summary>
        /// ToString override. Gets the full name + columns count.
        /// </summary>
        public override string ToString()
        {
            var parts = this.GetFullName();
            if (this.HasColumns)
                parts += $" ({this.Columns.Count} columns)";
            if (this.HasExcludedColumns)
                parts += $" (-{this.ExcludedColumns.Count} excluded)";
            if (this.HasIncludedColumns)
                parts += $" (+{this.IncludedColumns.Count} included)";
            return parts;
        }

        /// <summary>
        /// Gets the full name of the table, based on schema name + "." + table name (if schema name exists).
        /// </summary>
        public string GetFullName()
            => string.IsNullOrEmpty(this.SchemaName) ? this.TableName : $"{this.SchemaName}.{this.TableName}";

        /// <inheritdoc cref="SyncNamedItem{T}.EqualsByProperties(T)"/>
        public override bool EqualsByProperties(SetupTable otherInstance)
        {
            if (otherInstance == null)
                return false;

            var sc = SyncGlobalization.DataSourceStringComparison;

            if (!this.EqualsByName(otherInstance))
                return false;

            var thisExEmpty = this.ExcludedColumns == null || this.ExcludedColumns.Count == 0;
            var otherExEmpty = otherInstance.ExcludedColumns == null || otherInstance.ExcludedColumns.Count == 0;
            var excludedEqual = (thisExEmpty && otherExEmpty)
                || (!thisExEmpty && !otherExEmpty
                    && this.ExcludedColumns.CompareWith(otherInstance.ExcludedColumns, (c, oc) => string.Equals(c, oc, sc)));

            var thisIncEmpty = this.IncludedColumns == null || this.IncludedColumns.Count == 0;
            var otherIncEmpty = otherInstance.IncludedColumns == null || otherInstance.IncludedColumns.Count == 0;
            var includedEqual = (thisIncEmpty && otherIncEmpty)
                || (!thisIncEmpty && !otherIncEmpty
                    && this.IncludedColumns.CompareWith(otherInstance.IncludedColumns, (c, oc) => string.Equals(c, oc, sc)));

            // checking properties
            return this.SyncDirection == otherInstance.SyncDirection
                    && this.Columns.CompareWith(otherInstance.Columns, (c, oc) => string.Equals(c, oc, sc))
                    && excludedEqual
                    && includedEqual;
        }

        /// <inheritdoc cref="SyncNamedItem{T}.GetAllNamesProperties"/>
        public override IEnumerable<string> GetAllNamesProperties()
        {
            yield return this.TableName;
            yield return this.SchemaName;
        }
    }
}