import { createHash, randomUUID } from "node:crypto";
import { mkdir, open, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { basename, dirname, join } from "node:path";

export interface BackupMetadata {
  id: string;
  createdAt: string;
  originalPath: string;
  originalSha256: string;
  clientVersion: string;
  managedPaths: string[];
  backupPath: string;
}

function sha256(content: string): string {
  return createHash("sha256").update(content, "utf8").digest("hex");
}

export class AtomicConfigStore {
  constructor(private readonly clientVersion: string) {}

  async replaceWithBackup(targetPath: string, nextContent: string, managedPaths: string[]): Promise<BackupMetadata> {
    const directory = dirname(targetPath);
    const stateDirectory = join(directory, ".chatgpt-connector");
    const backupDirectory = join(stateDirectory, "backups");
    const lockPath = join(stateDirectory, "config.lock");
    await mkdir(backupDirectory, { recursive: true });

    const lock = await open(lockPath, "wx", 0o600).catch((error: NodeJS.ErrnoException) => {
      if (error.code === "EEXIST") throw new Error("config_locked");
      throw error;
    });
    try {
      const original = await readFile(targetPath, "utf8");
      const originalStat = await stat(targetPath);
      const id = `${new Date().toISOString().replace(/[:.]/g, "-")}-${randomUUID()}`;
      const backupPath = join(backupDirectory, `${id}-${basename(targetPath)}`);
      await writeFile(backupPath, original, { mode: 0o600, flag: "wx" });

      const metadata: BackupMetadata = {
        id,
        createdAt: new Date().toISOString(),
        originalPath: targetPath,
        originalSha256: sha256(original),
        clientVersion: this.clientVersion,
        managedPaths: [...managedPaths],
        backupPath,
      };
      await writeFile(`${backupPath}.json`, JSON.stringify(metadata, null, 2), { mode: 0o600, flag: "wx" });

      const temporaryPath = join(directory, `.${basename(targetPath)}.${randomUUID()}.tmp`);
      const temporary = await open(temporaryPath, "wx", originalStat.mode & 0o777);
      try {
        await temporary.writeFile(nextContent, "utf8");
        await temporary.sync();
      } finally {
        await temporary.close();
      }
      try {
        await rename(temporaryPath, targetPath);
      } catch (error) {
        await rm(temporaryPath, { force: true });
        throw error;
      }
      return metadata;
    } finally {
      await lock.close();
      await rm(lockPath, { force: true });
    }
  }

  async verifyBackup(metadata: BackupMetadata): Promise<boolean> {
    const content = await readFile(metadata.backupPath, "utf8");
    return sha256(content) === metadata.originalSha256;
  }
}
