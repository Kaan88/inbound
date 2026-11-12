// UI.R37: the server's version in the status bar says whether it is the latest, as far as the server's own last update
// check knows (status.latestVersion, kept current by update.latest). The page checks nothing itself. A server with no
// update check (the Docker server, a portable build, an older server) shows the version alone. The desktop window's
// rules are the same (src/DownloadManager.Desktop.WinUI/Servers/VersionStatus.cs).

function parts(value: string | null | undefined): number[] | null {
  const text = clean(value);
  if (!/^\d+(\.\d+){1,3}$/.test(text)) return null;
  const numbers = text.split('.').map(Number);
  while (numbers.length < 4) numbers.push(0);
  return numbers;
}

function clean(value: string | null | undefined): string {
  return value?.trim().replace(/^v/i, '') ?? '';
}

/** Negative when `version` is older than `latest`; null when either can't be read. */
export function compareVersions(version: string | null | undefined, latest: string | null | undefined): number | null {
  const a = parts(version);
  const b = parts(latest);
  if (!a || !b) return null;
  for (let i = 0; i < 4; i++) if (a[i] !== b[i]) return a[i] < b[i] ? -1 : 1;
  return 0;
}

/** "0.7.0 (latest)", "0.7.0 · update available (0.7.1)", or "0.7.0" when the latest isn't known. */
export function versionWords(version: string | null | undefined, latest: string | null | undefined): string {
  if (!version?.trim()) return '';
  const order = compareVersions(version, latest);
  if (order === null) return version;
  return order < 0 ? `${version} · update available (${clean(latest)})` : `${version} (latest)`;
}
