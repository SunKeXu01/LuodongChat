import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import pg from "pg";

export async function runMigrations(databaseUrl: string, directory = "db/migrations"): Promise<string[]> {
  const pool = new pg.Pool({ connectionString: databaseUrl, max: 1 });
  const applied: string[] = [];
  const client = await pool.connect();
  try {
    await client.query("SELECT pg_advisory_lock(724190301)");
    await client.query(`CREATE TABLE IF NOT EXISTS schema_migrations (
      name text PRIMARY KEY,
      applied_at timestamptz NOT NULL DEFAULT now()
    )`);
    const files = (await readdir(directory))
      .filter((name) => /^\d+.*\.sql$/.test(name) && !name.endsWith(".down.sql"))
      .sort();
    for (const name of files) {
      const exists = await client.query("SELECT 1 FROM schema_migrations WHERE name = $1", [name]);
      if ((exists.rowCount ?? 0) > 0) continue;
      const sql = await readFile(join(directory, name), "utf8");
      await client.query("BEGIN");
      try {
        await client.query(sql);
        await client.query("INSERT INTO schema_migrations (name) VALUES ($1) ON CONFLICT (name) DO NOTHING", [name]);
        await client.query("COMMIT");
        applied.push(name);
      } catch (error) {
        await client.query("ROLLBACK");
        throw error;
      }
    }
    return applied;
  } finally {
    await client.query("SELECT pg_advisory_unlock(724190301)").catch(() => undefined);
    client.release();
    await pool.end();
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const databaseUrl = process.env.DATABASE_URL;
  if (!databaseUrl) throw new Error("DATABASE_URL is required");
  const applied = await runMigrations(databaseUrl);
  console.info(JSON.stringify({ event: "migrations_complete", applied }));
}
