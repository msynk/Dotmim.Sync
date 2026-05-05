using System;

namespace Dotmim.Sync
{
    /// <summary>
    /// Describes one column when defining a shadow table in a single call (see <see cref="SetupTable.DefineShadowTableColumns"/> and <see cref="SetupTables.AddShadowTable"/>).
    /// </summary>
    public readonly struct ShadowTableColumnDefinition
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ShadowTableColumnDefinition"/> struct.
        /// </summary>
        /// <param name="columnName">Column name.</param>
        /// <param name="dotnetType">CLR type for the column.</param>
        /// <param name="isPrimaryKey">Whether this column is part of the primary key.</param>
        public ShadowTableColumnDefinition(string columnName, Type dotnetType, bool isPrimaryKey = false)
        {
            this.ColumnName = columnName ?? throw new ArgumentNullException(nameof(columnName));
            this.DotnetType = dotnetType ?? throw new ArgumentNullException(nameof(dotnetType));
            this.IsPrimaryKey = isPrimaryKey;
        }

        /// <summary>
        /// Gets the column name.
        /// </summary>
        public string ColumnName { get; }

        /// <summary>
        /// Gets the CLR type for the column.
        /// </summary>
        public Type DotnetType { get; }

        /// <summary>
        /// Gets a value indicating whether this column is part of the primary key.
        /// </summary>
        public bool IsPrimaryKey { get; }

        /// <summary>
        /// Creates a column definition with CLR type <typeparamref name="T"/>.
        /// </summary>
        public static ShadowTableColumnDefinition For<T>(string columnName, bool isPrimaryKey = false)
            => new(columnName, typeof(T), isPrimaryKey);
    }
}
