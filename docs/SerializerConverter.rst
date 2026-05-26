Converters and Serializers
=======================================


Overview
^^^^^^^^^^^

In HTTP scenarios, two pluggable components shape what travels on the wire:

* A **serializer** turns DMS messages and rows into bytes. The default serializer is JSON, via ``System.Text.Json``.
* A **converter** mutates row values before serialization (and back after deserialization). For example to base64-encode a binary blob, or to compact ``DateTime`` into ticks.

.. note:: Serializers and converters are HTTP-only. In TCP scenarios DMS streams binary data directly between providers without going through them.

The handshake between a ``WebRemoteOrchestrator`` (client) and a ``WebServerAgent`` (server) starts with the client sending a ``dotmim-sync-serialization-format`` HTTP header. The server uses the same serializer for the rest of the session.

Example header:

.. code-block:: text

    dotmim-sync-serialization-format: { "f": "json", "s": 500 }

Meaning:

* ``f`` (format key): which serializer to use, by ``Key``. ``json`` is the default.
* ``s`` (size): client-requested batch size in KB.

Once the server reads the header, it serializes its responses with the matching serializer and produces batch files of approximately ``s`` KB.

.. note:: Batching is covered in `Configuration <Configuration.html>`_.


Custom serializer (MessagePack example)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

.. hint:: Sample: `Converter & Serializer <https://github.com/Mimetis/Dotmim.Sync/tree/master/Samples/ConverterWebSync>`_.

To swap the default serializer, implement ``ISerializerFactory`` and ``ISerializer``, then register the factory on both sides.

The interfaces:

.. code-block:: csharp

    public interface ISerializerFactory
    {
        string Key { get; }
        ISerializer GetSerializer();
    }

    public interface ISerializer
    {
        Task<T> DeserializeAsync<T>(Stream ms);
        Task<object> DeserializeAsync(Stream ms, Type type);
        T Deserialize<T>(string value);

        Task<byte[]> SerializeAsync<T>(T obj);
        Task<byte[]> SerializeAsync(object obj, Type type);

        byte[] Serialize<T>(T obj);
        byte[] Serialize(object obj, Type type);
    }


An implementation backed by `MessagePack-CSharp <https://github.com/MessagePack-CSharp/MessagePack-CSharp>`_:

.. code-block:: csharp

    public class CustomMessagePackSerializerFactory : ISerializerFactory
    {
        public string Key => "mpack";
        public ISerializer GetSerializer() => new CustomMessagePackSerializer();
    }

    public class CustomMessagePackSerializer : ISerializer
    {
        private static readonly MessagePackSerializerOptions Options =
            ContractlessStandardResolver.Options;

        public Task<T> DeserializeAsync<T>(Stream ms)
            => MessagePackSerializer.DeserializeAsync<T>(ms, Options).AsTask();

        public Task<object> DeserializeAsync(Stream ms, Type type)
            => MessagePackSerializer.DeserializeAsync(type, ms, Options).AsTask();

        public T Deserialize<T>(string value)
            => MessagePackSerializer.Deserialize<T>(Convert.FromBase64String(value), Options);

        public Task<byte[]> SerializeAsync<T>(T obj)
            => Task.FromResult(MessagePackSerializer.Serialize(obj, Options));

        public Task<byte[]> SerializeAsync(object obj, Type type)
            => Task.FromResult(MessagePackSerializer.Serialize(type, obj, Options));

        public byte[] Serialize<T>(T obj) => MessagePackSerializer.Serialize(obj, Options);
        public byte[] Serialize(object obj, Type type) => MessagePackSerializer.Serialize(type, obj, Options);
    }

Reference this factory on both sides.

On the server, register it via ``WebServerOptions``:

.. code-block:: csharp

    var connectionString = builder.Configuration.GetConnectionString("SqlConnection");
    var setup = new SyncSetup(/* ... */);

    var webServerOptions = new WebServerOptions();
    webServerOptions.SerializerFactories.Add(new CustomMessagePackSerializerFactory());

    builder.Services.AddSyncServer(
        new SqlSyncChangeTrackingProvider(connectionString),
        setup,
        options: null,
        webServerOptions: webServerOptions);

.. note:: ``WebServerOptions.SerializerFactories`` (plural) is the registered list. JSON is in there by default; you append your custom factories. The client picks one by ``Key``.


On the client, set the orchestrator's ``SerializerFactory``:

.. code-block:: csharp

    var serverProxyOrchestrator = new WebRemoteOrchestrator("https://localhost:44342/api/sync")
    {
        SerializerFactory = new CustomMessagePackSerializerFactory(),
    };

    var clientProvider = new SqlSyncProvider(clientConnectionString);
    var agent = new SyncAgent(clientProvider, serverProxyOrchestrator);

The whole client / server traffic is now MessagePack-encoded.

Sniffing the serialized payload from interceptors works well to validate the swap:

.. code-block:: csharp

    serverProxyOrchestrator.OnHttpSendingRequest(args =>
    {
        // args.Request gives you the HttpRequestMessage about to be sent.
    });

    serverProxyOrchestrator.OnHttpGettingResponse(args =>
    {
        // args.Response gives you the response received.
    });

.. image:: assets/CustomMSPackSerializer.png


Custom converter
^^^^^^^^^^^^^^^^^^

A converter rewrites individual columns on each row before serialization (and again after deserialization). The serializer happens only after every converter ran.

The interface:

.. code-block:: csharp

    public interface IConverter
    {
        /// <summary>Unique key advertised to the other side.</summary>
        string Key { get; }

        /// <summary>Mutate the row before it is serialized.</summary>
        void BeforeSerialize(SyncRow row, SyncTable schemaTable);

        /// <summary>Mutate the row after it has been deserialized.</summary>
        void AfterDeserialized(SyncRow row, SyncTable schemaTable);
    }

A small example: encode photos as base64 and pack ``DateTime`` columns into ticks for a smaller payload.

.. code-block:: csharp

    public class CustomConverter : IConverter
    {
        public string Key => "cuscom";

        public void BeforeSerialize(SyncRow row, SyncTable schemaTable)
        {
            if (schemaTable.TableName != "Product")
                return;

            if (row["ThumbNailPhoto"] != null)
                row["ThumbNailPhoto"] = Convert.ToBase64String((byte[])row["ThumbNailPhoto"]);

            foreach (var col in schemaTable.Columns.Where(c => c.GetDataType() == typeof(DateTime)))
            {
                if (row[col.ColumnName] != null)
                    row[col.ColumnName] = ((DateTime)row[col.ColumnName]).Ticks;
            }
        }

        public void AfterDeserialized(SyncRow row, SyncTable schemaTable)
        {
            if (schemaTable.TableName != "Product")
                return;

            if (row["ThumbNailPhoto"] is string b64)
                row["ThumbNailPhoto"] = Convert.FromBase64String(b64);

            foreach (var col in schemaTable.Columns.Where(c => c.GetDataType() == typeof(DateTime)))
            {
                if (row[col.ColumnName] != null)
                    row[col.ColumnName] = new DateTime(Convert.ToInt64(row[col.ColumnName]));
            }
        }
    }


On the client, attach it to the ``WebRemoteOrchestrator``:

.. code-block:: csharp

    var proxyClientProvider = new WebRemoteOrchestrator("https://localhost:44342/api/sync")
    {
        SerializerFactory = new CustomMessagePackSerializerFactory(),
        Converter = new CustomConverter(),
    };

On the server, register it in ``WebServerOptions.Converters`` (the server can advertise more than one converter; the client picks the one it wants):

.. code-block:: csharp

    var webServerOptions = new WebServerOptions();
    webServerOptions.SerializerFactories.Add(new CustomMessagePackSerializerFactory());
    webServerOptions.Converters.Add(new CustomConverter());

Without converter:

.. image:: /assets/CustomConverterWithout.png

With converter:

.. image:: /assets/CustomConverterWith.png
