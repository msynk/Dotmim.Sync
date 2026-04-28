using Npgsql;

namespace Dotmim.Sync.Samples.PostgresServer;

internal static class PostgresDemoSchemaInitializer
{
    public static async Task EnsureAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using (var ext = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS postgis;", conn))
        {
            await ext.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var ddl = $"""
            CREATE TABLE IF NOT EXISTS public.{SyncSampleConstants.GeometryArrayTable} (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                name text NOT NULL,
                place_geom geometry(Point, 4326) NOT NULL,
                category_tags integer[] NOT NULL DEFAULT ARRAY[]::integer[],
                audit_created_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                audit_updated_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                audit_tenant_id uuid NOT NULL DEFAULT gen_random_uuid()
            );

            CREATE TABLE IF NOT EXISTS public.{SyncSampleConstants.ShadowTable} (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                event_name text NOT NULL,
                event_at_utc timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS public.{SyncSampleConstants.ExcludeTable} (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                first_name text NOT NULL,
                last_name text NOT NULL,
                email text NOT NULL,
                secret_note text NOT NULL,
                audit_created_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                audit_updated_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                audit_tenant_id uuid NOT NULL DEFAULT gen_random_uuid()
            );

            CREATE TABLE IF NOT EXISTS public.{SyncSampleConstants.LoadTestTable} (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                order_no integer NOT NULL UNIQUE,
                line_item text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
            );

            CREATE INDEX IF NOT EXISTS ix_{SyncSampleConstants.LoadTestTable}_created_at ON public.{SyncSampleConstants.LoadTestTable} (created_at);

            CREATE TABLE IF NOT EXISTS public.{SyncSampleConstants.GlobalAuditTable} (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                name text NOT NULL,
                price numeric(10,2) NOT NULL,
                internal_notes text NULL,
                audit_created_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                audit_updated_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                audit_tenant_id uuid NOT NULL DEFAULT gen_random_uuid()
            );

            CREATE TABLE IF NOT EXISTS public.{SyncSampleConstants.GlobalAuditFeaturedTable} (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                name text NOT NULL,
                price numeric(10,2) NOT NULL,
                internal_notes text NULL,
                audit_created_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                audit_updated_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                audit_tenant_id uuid NOT NULL DEFAULT gen_random_uuid()
            );
            """;

        await using (var create = new NpgsqlCommand(ddl, conn))
        {
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Migration for already-provisioned dev databases: add the audit_* columns to existing demo tables
        // so the SyncSetup.GloballyExcludeColumns demo can prove the global rule strips them from every scope,
        // regardless of whether the scope's setup mentions them.
        var migrateAuditColumnsDdl = $"""
            ALTER TABLE IF EXISTS public.{SyncSampleConstants.GeometryArrayTable}
                ADD COLUMN IF NOT EXISTS audit_created_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                ADD COLUMN IF NOT EXISTS audit_updated_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                ADD COLUMN IF NOT EXISTS audit_tenant_id  uuid        NOT NULL DEFAULT gen_random_uuid();

            ALTER TABLE IF EXISTS public.{SyncSampleConstants.ExcludeTable}
                ADD COLUMN IF NOT EXISTS audit_created_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                ADD COLUMN IF NOT EXISTS audit_updated_at timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                ADD COLUMN IF NOT EXISTS audit_tenant_id  uuid        NOT NULL DEFAULT gen_random_uuid();
            """;

        await using (var migrate = new NpgsqlCommand(migrateAuditColumnsDdl, conn))
        {
            await migrate.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await SeedTableAsync(
            conn,
            SyncSampleConstants.GeometryArrayTable,
            [
                $"INSERT INTO public.{SyncSampleConstants.GeometryArrayTable} (name, place_geom, category_tags) VALUES ('Headquarters', ST_SetSRID(ST_MakePoint(-73.935242, 40.730610), 4326), ARRAY[1, 2, 3]);",
                $"INSERT INTO public.{SyncSampleConstants.GeometryArrayTable} (name, place_geom, category_tags) VALUES ('Warehouse', ST_SetSRID(ST_MakePoint(2.3522, 48.8566), 4326), ARRAY[10, 20]);",
                $"INSERT INTO public.{SyncSampleConstants.GeometryArrayTable} (name, place_geom, category_tags) VALUES ('Store', ST_SetSRID(ST_MakePoint(-0.1276, 51.5074), 4326), ARRAY[7]);",
            ]).ConfigureAwait(false);

        await SeedTableAsync(
            conn,
            SyncSampleConstants.ShadowTable,
            [
                $"INSERT INTO public.{SyncSampleConstants.ShadowTable} (event_name, event_at_utc) VALUES ('UserLogin', NOW() - INTERVAL '10 minutes');",
                $"INSERT INTO public.{SyncSampleConstants.ShadowTable} (event_name, event_at_utc) VALUES ('InventoryRefreshed', NOW() - INTERVAL '3 minutes');",
                $"INSERT INTO public.{SyncSampleConstants.ShadowTable} (event_name, event_at_utc) VALUES ('NightlyJobCompleted', NOW());",
            ]).ConfigureAwait(false);

        await SeedTableAsync(
            conn,
            SyncSampleConstants.ExcludeTable,
            [
                $"INSERT INTO public.{SyncSampleConstants.ExcludeTable} (first_name, last_name, email, secret_note) VALUES ('Ava', 'Stone', 'ava@example.com', 'VIP customer - internal only');",
                $"INSERT INTO public.{SyncSampleConstants.ExcludeTable} (first_name, last_name, email, secret_note) VALUES ('Noah', 'Brooks', 'noah@example.com', 'Do not sync this private note');",
                $"INSERT INTO public.{SyncSampleConstants.ExcludeTable} (first_name, last_name, email, secret_note) VALUES ('Mia', 'Clark', 'mia@example.com', 'Sensitive classification metadata');",
            ]).ConfigureAwait(false);

        await SeedTableAsync(
            conn,
            SyncSampleConstants.GlobalAuditTable,
            [
                $"INSERT INTO public.{SyncSampleConstants.GlobalAuditTable} (name, price, internal_notes) VALUES ('Alpha Widget', 19.99, 'Stocked in aisle 3');",
                $"INSERT INTO public.{SyncSampleConstants.GlobalAuditTable} (name, price, internal_notes) VALUES ('Bravo Gadget', 29.50, 'Launch promo ends Friday');",
                $"INSERT INTO public.{SyncSampleConstants.GlobalAuditTable} (name, price, internal_notes) VALUES ('Charlie Kit', 49.00, 'Internal SKU mapping notes');",
            ]).ConfigureAwait(false);

        await SeedTableAsync(
            conn,
            SyncSampleConstants.GlobalAuditFeaturedTable,
            [
                $"INSERT INTO public.{SyncSampleConstants.GlobalAuditFeaturedTable} (name, price, internal_notes) VALUES ('Delta Featured', 99.00, 'Promoted offer — server-only reminder');",
                $"INSERT INTO public.{SyncSampleConstants.GlobalAuditFeaturedTable} (name, price, internal_notes) VALUES ('Echo Featured', 129.00, 'Bundle idea pending');",
            ]).ConfigureAwait(false);

        await TopUpLoadTestRowsAsync(conn).ConfigureAwait(false);
    }

    private static async Task TopUpLoadTestRowsAsync(NpgsqlConnection conn)
    {
        long count;
        await using (var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{SyncSampleConstants.LoadTestTable};", conn))
        {
            count = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (count >= SyncSampleConstants.LoadTestMinRowCount)
            return;

        var need = (int)Math.Min(SyncSampleConstants.LoadTestMinRowCount - count, 50_000);
        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO public.{SyncSampleConstants.LoadTestTable} (order_no, line_item)
            SELECT t.m + gs, repeat('x', 960)
            FROM (
                SELECT COALESCE(MAX(order_no), 0)::bigint AS m FROM public.{SyncSampleConstants.LoadTestTable}
            ) t
            CROSS JOIN generate_series(1, @need) AS gs;
            """,
            conn);
        insertCmd.Parameters.AddWithValue("need", need);
        await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task SeedTableAsync(NpgsqlConnection conn, string tableName, IEnumerable<string> inserts)
    {
        await using (var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{tableName};", conn))
        {
            var c = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (c > 0)
                return;
        }

        foreach (var sql in inserts)
        {
            await using var insert = new NpgsqlCommand(sql, conn);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
