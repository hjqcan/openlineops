import path from 'node:path';

export function createLocalSqliteConnectionString(databasePath: string): string {
  if (!path.isAbsolute(databasePath)
      || databasePath.includes('\0')
      || databasePath.includes('\r')
      || databasePath.includes('\n')) {
    throw new Error('A local SQLite database path must be one absolute filesystem path.');
  }

  return `Data Source="${databasePath.replaceAll('"', '""')}"`;
}
