using System;
using System.Runtime.Serialization;

namespace Dotmim.Sync
{
    /// <summary>
    /// Represents a shadow column to be created on the client database at provisioning time.
    /// Shadow columns do not exist in the server database; they are defined in the sync setup
    /// and their values are populated at runtime (e.g. in the OnRowsChangesSelected interceptor).
    /// </summary>
    [DataContract(Name = "ssc"), Serializable]
    public class SetupShadowColumn
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SetupShadowColumn"/> class.
        /// Parameterless ctor for serialization.
        /// </summary>
        public SetupShadowColumn() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SetupShadowColumn"/> class.
        /// </summary>
        public SetupShadowColumn(string columnName, Type type)
        {
            this.ColumnName = columnName ?? throw new ArgumentNullException(nameof(columnName));
            this.DotnetType = type ?? throw new ArgumentNullException(nameof(type));
        }

        /// <summary>
        /// Gets or Sets the column name.
        /// </summary>
        [DataMember(Name = "n", IsRequired = true, Order = 1)]
        public string ColumnName { get; set; }

        /// <summary>
        /// Gets or Sets the .NET type for this shadow column, stored as the assembly qualified name string for serialization.
        /// </summary>
        [IgnoreDataMember]
        public Type DotnetType { get; set; }

        /// <summary>
        /// Gets or Sets the serialized type name (maps to SyncColumn's compressed type format).
        /// </summary>
        [DataMember(Name = "t", IsRequired = true, Order = 2)]
        public string TypeName
        {
            get => this.DotnetType != null ? SyncColumn.GetAssemblyQualifiedName(this.DotnetType) : "-1";
            set => this.DotnetType = SyncColumn.GetTypeFromAssemblyQualifiedName(value);
        }
    }
}
