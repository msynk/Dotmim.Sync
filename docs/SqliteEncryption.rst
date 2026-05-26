SQLite Encryption
================================

Overview
^^^^^^^^^^

SQLite has no built-in support for encrypting database files. Encrypted SQLite databases come from third-party builds:

* `SEE <https://www.hwaci.com/sw/sqlite/see.html>`_
* `SQLCipher <https://www.zetetic.net/sqlcipher/>`_
* `SQLiteCrypt <http://www.sqlite-crypt.com/>`_
* `wxSQLite3 <https://utelle.github.io/wxsqlite3>`_

This article walks through SQLCipher via the open-source ``SQLitePCLRaw.bundle_e_sqlcipher`` bundle. The same pattern applies to other engines that follow the standard ``Microsoft.Data.Sqlite`` extensibility points.

.. hint:: More background on SQLite encryption with **Microsoft.Data.Sqlite**: `SQLite encryption <https://docs.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption?tabs=netcore-cli>`_.

.. hint:: Sample: `SQLite encryption sample <https://github.com/Mimetis/Dotmim.Sync/blob/master/Samples/SqliteEncryption>`_.

The trick is to override the default ``Microsoft.Data.Sqlite`` runtime bundle with the SQLCipher one.


Project setup
^^^^^^^^^^^^^^^^^^^^^^^^

Reference the SQLCipher bundle alongside ``Microsoft.Data.Sqlite.Core``:

.. code-block:: bash

    dotnet add package Microsoft.Data.Sqlite.Core
    dotnet add package SQLitePCLRaw.bundle_e_sqlcipher

A typical project file looks like:

.. code-block:: xml

    <Project Sdk="Microsoft.NET.Sdk">
        <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
        </PropertyGroup>

        <ItemGroup>
            <PackageReference Include="Dotmim.Sync.Sqlite" Version="1.3.16" />
            <PackageReference Include="Microsoft.Data.Sqlite.Core" Version="8.0.6" />
            <PackageReference Include="SQLitePCLRaw.bundle_e_sqlcipher" Version="2.1.10" />
        </ItemGroup>
    </Project>

.. note:: ``Dotmim.Sync.Sqlite`` brings in ``Microsoft.Data.Sqlite``, which transitively references ``Microsoft.Data.Sqlite.Core`` plus the standard ``SQLitePCLRaw.bundle_e_sqlite3`` bundle. Adding ``Microsoft.Data.Sqlite.Core`` and ``SQLitePCLRaw.bundle_e_sqlcipher`` at the project root takes precedence and replaces the standard SQLite native library with the SQLCipher one.

.. image:: assets/SqliteEncryption01.png


Code
^^^^^^^^

Code-wise, the only change is the connection string. Add a ``Password`` to enable encryption:

.. code-block:: csharp

    // From configuration:
    // "SqliteConnection": "Data Source=AdventureWorks.db;Password=YOUR_PASSWORD"
    var sqliteConnectionString = configuration.GetConnectionString("SqliteConnection");
    var clientProvider = new SqliteSyncProvider(sqliteConnectionString);

You can also build the connection string with ``SqliteConnectionStringBuilder``:

.. code-block:: csharp

    var builder = new SqliteConnectionStringBuilder
    {
        DataSource = "AdventureWorks.db",
        Password = "YOUR_PASSWORD",
    };

    var clientProvider = new SqliteSyncProvider(builder);

The rest of the sync code is unchanged.

.. warning:: Once a SQLite database is created with SQLCipher, it can only be opened with the same key. Pick a key management strategy that survives app reinstalls (Windows DPAPI, Android Keystore, iOS Keychain, etc.) or you will lose access to the local data.
