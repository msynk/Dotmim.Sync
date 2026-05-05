using System;
using System.Runtime.Serialization;

namespace Dotmim.Sync
{
    /// <summary>
    /// Column definition for a <see cref="SetupTable"/> that is a shadow table (no backing table on the server).
    /// Used with <see cref="SetupTable.IsShadowTable"/> to build the sync schema entirely from setup metadata.
    /// </summary>
    [DataContract(Name = "sstc"), Serializable]
    public class SetupShadowTableColumn
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SetupShadowTableColumn"/> class.
        /// </summary>
        public SetupShadowTableColumn() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SetupShadowTableColumn"/> class.
        /// </summary>
        public SetupShadowTableColumn(string columnName, Type type, bool isPrimaryKey = false)
        {
            this.ColumnName = columnName ?? throw new ArgumentNullException(nameof(columnName));
            this.DotnetType = type ?? throw new ArgumentNullException(nameof(type));
            this.IsPrimaryKey = isPrimaryKey;
        }

        /// <summary>
        /// Gets or sets the column name.
        /// </summary>
        [DataMember(Name = "n", IsRequired = true, Order = 1)]
        public string ColumnName { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this column is part of the primary key for the shadow table.
        /// </summary>
        [DataMember(Name = "pk", IsRequired = false, EmitDefaultValue = false, Order = 2)]
        public bool IsPrimaryKey { get; set; }

        /// <summary>
        /// Gets or sets the .NET type for this column.
        /// </summary>
        [IgnoreDataMember]
        public Type DotnetType { get; set; }

        /// <summary>
        /// Gets or sets the serialized type name (maps to <see cref="SyncColumn"/>'s compressed type format).
        /// </summary>
        [DataMember(Name = "t", IsRequired = true, Order = 3)]
        public string TypeName
        {
            get => this.DotnetType != null ? SyncColumn.GetAssemblyQualifiedName(this.DotnetType) : "-1";
            set => this.DotnetType = SyncColumn.GetTypeFromAssemblyQualifiedName(value);
        }
    }
}
