using Npgsql;

namespace Dotmim.Sync.Samples.Migration.Server;

/// <summary>
/// Creates the demo tables and applies incremental schema changes on each server start.
/// Every step is idempotent — safe to run against an already-up-to-date database.
/// </summary>
internal static class MigrationSchemaInitializer
{
    public static async Task EnsureAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        // ── Step 1: create tables (v1 column names used here so a brand-new DB ──────
        //           starts with the old names; step 2 immediately renames them).
        var createDdl = $"""
            CREATE TABLE IF NOT EXISTS public.{MigrationConstants.ProductsTable} (
                id              uuid            PRIMARY KEY DEFAULT gen_random_uuid(),
                product_name    text            NOT NULL,
                description     text            NOT NULL DEFAULT '',
                price           numeric(10, 2)  NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS public.{MigrationConstants.OrdersTable} (
                id          uuid            PRIMARY KEY DEFAULT gen_random_uuid(),
                product_id  uuid            NOT NULL REFERENCES public.{MigrationConstants.ProductsTable}(id),
                order_date  timestamptz     NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                total       numeric(10, 2)  NOT NULL DEFAULT 0,
                status      text            NOT NULL DEFAULT 'pending'
            );
            """;

        await using (var cmd = new NpgsqlCommand(createDdl, conn))
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // ── Step 2: rename columns to the v2 names (idempotent DO block) ─────────────
        //   mig_products : product_name  →  name
        //   mig_orders   : order_date    →  created_at
        var renameDdl = $"""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name   = '{MigrationConstants.ProductsTable}'
                      AND column_name  = 'product_name'
                ) THEN
                    ALTER TABLE public.{MigrationConstants.ProductsTable}
                        RENAME COLUMN product_name TO name;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name   = '{MigrationConstants.OrdersTable}'
                      AND column_name  = 'order_date'
                ) THEN
                    ALTER TABLE public.{MigrationConstants.OrdersTable}
                        RENAME COLUMN order_date TO created_at;
                END IF;
            END $$;
            """;

        await using (var cmd = new NpgsqlCommand(renameDdl, conn))
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // ── Step 3: seed initial rows (only when table is still empty) ────────────────
        //   Uses the v2 column names that are now in effect after step 2.
        await SeedAsync(conn, MigrationConstants.ProductsTable,
        [
            $"""INSERT INTO public.{MigrationConstants.ProductsTable} (id, name, description, price) VALUES ('a1000000-0000-0000-0000-000000000001', 'Widget Alpha', 'A sturdy all-purpose widget',              9.99);""",
            $"""INSERT INTO public.{MigrationConstants.ProductsTable} (id, name, description, price) VALUES ('a1000000-0000-0000-0000-000000000002', 'Gadget Beta',  'High-precision gadget for professionals', 49.50);""",
            $"""INSERT INTO public.{MigrationConstants.ProductsTable} (id, name, description, price) VALUES ('a1000000-0000-0000-0000-000000000003', 'Kit Gamma',    'Starter kit bundle',                     24.00);""",
        ]).ConfigureAwait(false);

        await SeedAsync(conn, MigrationConstants.OrdersTable,
        [
            $"""INSERT INTO public.{MigrationConstants.OrdersTable} (product_id, created_at, total, status) VALUES ('a1000000-0000-0000-0000-000000000001', NOW() - INTERVAL '5 days', 19.98, 'shipped');""",
            $"""INSERT INTO public.{MigrationConstants.OrdersTable} (product_id, created_at, total, status) VALUES ('a1000000-0000-0000-0000-000000000002', NOW() - INTERVAL '2 days', 49.50, 'pending');""",
            $"""INSERT INTO public.{MigrationConstants.OrdersTable} (product_id, created_at, total, status) VALUES ('a1000000-0000-0000-0000-000000000003', NOW() - INTERVAL '1 day',  48.00, 'processing');""",
        ]).ConfigureAwait(false);
    }

    private static async Task SeedAsync(NpgsqlConnection conn, string tableName, IEnumerable<string> inserts)
    {
        long count;
        await using (var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{tableName};", conn))
            count = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

        if (count > 0)
            return;

        foreach (var sql in inserts)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
