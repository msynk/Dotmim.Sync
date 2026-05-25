using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Dotmim.Sync.Migration;

namespace Dotmim.Sync
{
    /// <summary>
    /// Represents a list of tables to be added to the sync process.
    /// </summary>
    [DataContract(Name = "s"), Serializable]
    public class SyncSetup : IEquatable<SyncSetup>
    {

        /// <summary>
        /// Gets or Sets the tables involved in the sync.
        /// </summary>
        [DataMember(Name = "tbls", IsRequired = false, EmitDefaultValue = false, Order = 1)]
        public SetupTables Tables { get; set; }

        /// <summary>
        /// Gets or Sets the filters involved in the sync.
        /// </summary>
        [DataMember(Name = "fils", IsRequired = false, EmitDefaultValue = false, Order = 2)]
        public SetupFilters Filters { get; set; }

        /// <summary>
        /// Gets or sets specify a prefix for naming stored procedure. Default is empty string.
        /// </summary>
        [DataMember(Name = "spp", IsRequired = false, EmitDefaultValue = false, Order = 3)]
        public string StoredProceduresPrefix { get; set; }

        /// <summary>
        /// Gets or sets specify a suffix for naming stored procedures. Default is empty string.
        /// </summary>
        [DataMember(Name = "sps", IsRequired = false, EmitDefaultValue = false, Order = 4)]
        public string StoredProceduresSuffix { get; set; }

        /// <summary>
        /// Gets or sets specify a prefix for naming stored procedure. Default is empty string.
        /// </summary>
        [DataMember(Name = "tf", IsRequired = false, EmitDefaultValue = false, Order = 5)]
        public string TriggersPrefix { get; set; }

        /// <summary>
        /// Gets or sets specify a suffix for naming stored procedures. Default is empty string.
        /// </summary>
        [DataMember(Name = "ts", IsRequired = false, EmitDefaultValue = false, Order = 6)]
        public string TriggersSuffix { get; set; }

        /// <summary>
        /// Gets or sets specify a prefix for naming tracking tables. Default is empty string.
        /// </summary>
        [DataMember(Name = "ttp", IsRequired = false, EmitDefaultValue = false, Order = 7)]
        public string TrackingTablesPrefix { get; set; }

        /// <summary>
        /// Gets or sets specify a suffix for naming tracking tables.
        /// </summary>
        [DataMember(Name = "tts", IsRequired = false, EmitDefaultValue = false, Order = 8)]
        public string TrackingTablesSuffix { get; set; }

        /// <summary>
        /// Gets or sets column names to omit from synchronization for every table in this setup (scope).
        /// Applied in addition to <see cref="GlobalExcludedColumns"/> and to each <see cref="SetupTable.ExcludedColumns"/>.
        /// A column listed here is silently ignored on tables that don't have it, and is never applied to primary key columns.
        /// Use <see cref="SetupTable.IncludedColumns"/> on a specific table to bypass this (and the global) exclusion for that table.
        /// </summary>
        [DataMember(Name = "secols", IsRequired = false, EmitDefaultValue = false, Order = 9)]
        public SetupColumns ExcludedColumns { get; set; }

        // ── Static migration registry ──────────────────────────────────────────────────
        // Migrations are process-wide deployment configuration. They are registered once
        // at startup and are never serialised into scope_info.

        private static readonly ConcurrentDictionary<string, SyncMigration> _globalMigrations
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the process-wide dictionary of registered migrations, keyed by
        /// <see cref="SyncMigration.FromScopeName"/>.
        /// </summary>
        public static IReadOnlyDictionary<string, SyncMigration> GlobalMigrations => _globalMigrations;

        /// <summary>
        /// Registers a migration that bridges clients using an old scope to the server's
        /// current scope. Replaces any previously registered migration for the same
        /// <see cref="SyncMigration.FromScopeName"/>.
        /// <example>
        /// <code>
        /// SyncSetup.AddMigration(
        ///     new SyncMigration("v1")
        ///         .ForTable("Products", t => t.RenameColumn("ProductName", "Name")));
        /// </code>
        /// </example>
        /// </summary>
        public static void AddMigration(SyncMigration migration)
        {
            if (migration == null) throw new ArgumentNullException(nameof(migration));
            _globalMigrations[migration.FromScopeName] = migration;
        }

        /// <summary>
        /// Returns the registered <see cref="SyncMigration"/> for <paramref name="fromScopeName"/>,
        /// or <c>null</c> if none has been registered for that scope name.
        /// </summary>
        public static SyncMigration GetMigrationForScope(string fromScopeName)
        {
            if (string.IsNullOrWhiteSpace(fromScopeName)) return null;
            return _globalMigrations.TryGetValue(fromScopeName, out var m) ? m : null;
        }

        /// <summary>
        /// Gets the process-wide column exclusion list shared by every <see cref="SyncSetup"/> instance and every scope.
        /// A column listed here is silently ignored on tables that don't have it, and is never applied to primary key columns.
        /// Use <see cref="SetupTable.IncludedColumns"/> on a specific table to bypass this exclusion for that table.
        /// </summary>
        /// <remarks>
        /// Because this collection is static, it affects every <see cref="SyncSetup"/> instance in the current AppDomain.
        /// Populate it once during application startup (e.g. for audit/system columns like "CreatedOn", "UpdatedOn").
        /// </remarks>
        public static SetupColumns GlobalExcludedColumns { get; } = [];

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncSetup"/> class.
        /// Create a list of tables to be added to the sync process.
        /// </summary>
        public SyncSetup(IEnumerable<string> tables)
            : this() => this.Tables.AddRange(tables);

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncSetup"/> class.
        /// Create a list of tables to be added to the sync process.
        /// </summary>
        public SyncSetup(params string[] tables)
            : this() => this.Tables.AddRange(tables);

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncSetup"/> class.
        /// ctor.
        /// </summary>
        public SyncSetup()
        {
            this.Tables = [];
            this.Filters = [];
            this.ExcludedColumns = [];

            // this.Version = SyncVersion.Current.ToString();
        }

        /// <summary>
        /// Gets a value indicating whether check if Setup has tables.
        /// </summary>
        public bool HasTables => this.Tables?.Count > 0;

        /// <summary>
        /// Gets a value indicating whether check if Setup has at least one table with columns.
        /// </summary>
        public bool HasColumns => this.Tables?.SelectMany(t => t.Columns).Count() > 0;  // using SelectMany to get columns and not Collection<Column>

        /// <summary>
        /// Gets a value indicating whether this setup has instance-level <see cref="ExcludedColumns"/> defined.
        /// </summary>
        public bool HasExcludedColumns => this.ExcludedColumns?.Count > 0;

        /// <summary>
        /// Exclude a column from synchronization for every table in this setup (scope). Duplicates are ignored.
        /// A primary key or a column that does not exist on a given table is silently skipped for that table.
        /// </summary>
        public SyncSetup ExcludeColumn(string columnName)
        {
            this.ExcludedColumns ??= [];
            if (!this.ExcludedColumns.Contains(columnName))
                this.ExcludedColumns.Add(columnName);
            return this;
        }

        /// <summary>
        /// Exclude multiple columns from synchronization for every table in this setup (scope). Duplicates are ignored.
        /// </summary>
        public SyncSetup ExcludeColumns(params string[] columnNames)
        {
            this.ExcludedColumns ??= [];
            if (columnNames == null)
                return this;
            foreach (var name in columnNames)
            {
                if (!this.ExcludedColumns.Contains(name))
                    this.ExcludedColumns.Add(name);
            }

            return this;
        }

        /// <summary>
        /// Add a column to the process-wide <see cref="GlobalExcludedColumns"/> list. Duplicates are ignored.
        /// </summary>
        public static void GloballyExcludeColumn(string columnName)
        {
            if (!GlobalExcludedColumns.Contains(columnName))
                GlobalExcludedColumns.Add(columnName);
        }

        /// <summary>
        /// Add multiple columns to the process-wide <see cref="GlobalExcludedColumns"/> list. Duplicates are ignored.
        /// </summary>
        public static void GloballyExcludeColumns(params string[] columnNames)
        {
            if (columnNames == null)
                return;
            foreach (var name in columnNames)
            {
                if (!GlobalExcludedColumns.Contains(name))
                    GlobalExcludedColumns.Add(name);
            }
        }

        /// <summary>
        /// Computes, by name, the set of columns that should be excluded from synchronization for the given <paramref name="setupTable"/>.
        /// Union of <see cref="GlobalExcludedColumns"/>, this setup's <see cref="ExcludedColumns"/>, and the table's own <see cref="SetupTable.ExcludedColumns"/>,
        /// minus the names listed in <see cref="SetupTable.IncludedColumns"/> (which bypass global / setup-level exclusions for that table only).
        /// </summary>
        /// <remarks>
        /// The returned names are not checked against the physical schema; filter validation and schema resolution apply existence / primary key checks separately.
        /// A name appearing in both the table's explicit <see cref="SetupTable.ExcludedColumns"/> and its <see cref="SetupTable.IncludedColumns"/> stays excluded
        /// (the per-table exclusion takes precedence, since the Include list is a bypass for the "general" exclusion).
        /// </remarks>
        public IEnumerable<string> GetEffectiveExcludedColumnNames(SetupTable setupTable)
        {
            Guard.ThrowIfNull(setupTable);

            var sc = SyncGlobalization.DataSourceStringComparison;
            var result = new List<string>();

            bool Contains(IEnumerable<string> source, string name)
                => source != null && source.Any(n => string.Equals(n, name, sc));

            void AddIfMissing(string name)
            {
                if (!Contains(result, name))
                    result.Add(name);
            }

            // 1) Global + setup-level exclusions can be bypassed by the table's IncludedColumns.
            if (GlobalExcludedColumns != null)
            {
                foreach (var name in GlobalExcludedColumns)
                {
                    if (Contains(setupTable.IncludedColumns, name))
                        continue;
                    AddIfMissing(name);
                }
            }

            if (this.ExcludedColumns != null)
            {
                foreach (var name in this.ExcludedColumns)
                {
                    if (Contains(setupTable.IncludedColumns, name))
                        continue;
                    AddIfMissing(name);
                }
            }

            // 2) Table-level exclusions always apply; IncludedColumns cannot bypass an exclusion set on the same table.
            if (setupTable.ExcludedColumns != null)
            {
                foreach (var name in setupTable.ExcludedColumns)
                    AddIfMissing(name);
            }

            return result;
        }

        /// <summary>
        /// Check if two setups have the same local options.
        /// </summary>
        public bool HasSameOptions(SyncSetup otherSetup)
        {
            if (otherSetup == null)
                return false;

            var sc = SyncGlobalization.DataSourceStringComparison;

            return string.Equals(this.StoredProceduresPrefix, otherSetup.StoredProceduresPrefix, sc) &&
                string.Equals(this.StoredProceduresSuffix, otherSetup.StoredProceduresSuffix, sc) &&
                string.Equals(this.TrackingTablesPrefix, otherSetup.TrackingTablesPrefix, sc) &&
                string.Equals(this.TrackingTablesSuffix, otherSetup.TrackingTablesSuffix, sc) &&
                string.Equals(this.TriggersPrefix, otherSetup.TriggersPrefix, sc) &&
                string.Equals(this.TriggersSuffix, otherSetup.TriggersSuffix, sc);
        }

        /// <summary>
        /// Check if two setups have the same tables / filters structure.
        /// </summary>
        public bool HasSameStructure(SyncSetup otherSetup)
        {
            if (otherSetup == null)
                return false;

            // Checking inner lists
            if (!this.Tables.CompareWith(otherSetup.Tables))
                return false;

            if (!this.Filters.CompareWith(otherSetup.Filters))
                return false;

            var sc = SyncGlobalization.DataSourceStringComparison;
            var thisExEmpty = this.ExcludedColumns == null || this.ExcludedColumns.Count == 0;
            var otherExEmpty = otherSetup.ExcludedColumns == null || otherSetup.ExcludedColumns.Count == 0;
            if (thisExEmpty && otherExEmpty)
                return true;

            if (thisExEmpty != otherExEmpty)
                return false;

            return this.ExcludedColumns.CompareWith(otherSetup.ExcludedColumns, (c, oc) => string.Equals(c, oc, sc));
        }

        /// <inheritdoc cref="SyncNamedItem{T}.EqualsByProperties(T)" />
        public bool EqualsByProperties(SyncSetup otherSetup)
        {
            if (otherSetup == null)
                return false;

            if (!this.HasSameOptions(otherSetup))
                return false;

            return this.HasSameStructure(otherSetup);
        }

        /// <summary>
        /// Returns a string that represents the current object.
        /// </summary>
        public override string ToString() => $"{this.Tables.Count} tables";

        /// <summary>
        /// Gets a true boolean if other instance is defined as same based on all properties.
        /// </summary>
        public bool Equals(SyncSetup other) => this.EqualsByProperties(other);

        /// <summary>
        /// Gets a true boolean if other instance is defined as same based on all properties.
        /// </summary>
        public override bool Equals(object obj) => this.EqualsByProperties(obj as SyncSetup);

        /// <summary>
        /// Returns a hash code for this instance.
        /// </summary>
        public override int GetHashCode() => base.GetHashCode();
    }
}