export type ConfigValue = null | boolean | number | string | ConfigValue[] | { [key: string]: ConfigValue };

export interface RestoreResult {
  merged: ConfigValue;
  restoredPaths: string[];
  conflicts: string[];
}

function clone<T extends ConfigValue>(value: T): T {
  return structuredClone(value);
}

function equal(a: ConfigValue | undefined, b: ConfigValue | undefined): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function get(root: ConfigValue, path: readonly string[]): ConfigValue | undefined {
  let value: ConfigValue | undefined = root;
  for (const segment of path) {
    if (value === null || Array.isArray(value) || typeof value !== "object") return undefined;
    value = value[segment];
  }
  return value;
}

function set(root: ConfigValue, path: readonly string[], value: ConfigValue | undefined): void {
  if (root === null || Array.isArray(root) || typeof root !== "object" || path.length === 0) {
    throw new Error("Managed paths must address object fields");
  }
  let parent: { [key: string]: ConfigValue } = root;
  for (const segment of path.slice(0, -1)) {
    const child = parent[segment];
    if (child === null || Array.isArray(child) || typeof child !== "object") parent[segment] = {};
    parent = parent[segment] as { [key: string]: ConfigValue };
  }
  const key = path[path.length - 1];
  if (!key) throw new Error("Managed path cannot be empty");
  if (value === undefined) delete parent[key];
  else parent[key] = clone(value);
}

export function restoreManagedFields(
  before: ConfigValue,
  applied: ConfigValue,
  current: ConfigValue,
  managedPaths: readonly string[],
): RestoreResult {
  const merged = clone(current);
  const restoredPaths: string[] = [];
  const conflicts: string[] = [];

  for (const dottedPath of managedPaths) {
    const path = dottedPath.split(".").filter(Boolean);
    if (path.length === 0) throw new Error("Managed path cannot be empty");
    const beforeValue = get(before, path);
    const appliedValue = get(applied, path);
    const currentValue = get(current, path);
    if (equal(currentValue, appliedValue)) {
      set(merged, path, beforeValue);
      restoredPaths.push(dottedPath);
    } else if (!equal(currentValue, beforeValue)) {
      conflicts.push(dottedPath);
    }
  }
  return { merged, restoredPaths, conflicts };
}
