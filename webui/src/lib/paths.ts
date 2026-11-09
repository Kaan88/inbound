// Showing a server path in a narrow place (U-WU-5): the end of a path is the part that tells folders apart, so a long
// path is cut in the middle and its last folder always shows. The full path goes in the tooltip.

/** The path split at its last separator: the head (cut when short of room) and the tail (kept). */
export function pathParts(path: string): { head: string; tail: string } {
  const trimmed = path.length > 1 ? path.replace(/[\\/]+$/, '') : path;
  // A drive root (C:\) or the root (/) is all tail, and so is a name with no separator.
  if (/^[A-Za-z]:$/.test(trimmed)) return { head: '', tail: path };
  const at = Math.max(trimmed.lastIndexOf('\\'), trimmed.lastIndexOf('/'));
  if (at <= 0) return { head: '', tail: trimmed || path };
  return { head: trimmed.slice(0, at), tail: trimmed.slice(at) };
}

/** The same folder written two ways (case on Windows, a trailing separator) counts once. */
export function samePath(a: string, b: string): boolean {
  const norm = (value: string) => {
    const trimmed = value.length > 1 ? value.replace(/[\\/]+$/, '') : value;
    return /^[A-Za-z]:/.test(trimmed) || trimmed.includes('\\') ? trimmed.replace(/\//g, '\\').toLowerCase() : trimmed;
  };
  return norm(a) === norm(b);
}

/** Each folder once, in the order given (U-WR-14: the allowed folders listed Downloads twice). */
export function uniquePaths(paths: readonly string[]): string[] {
  const kept: string[] = [];
  for (const path of paths) if (path && !kept.some((other) => samePath(other, path))) kept.push(path);
  return kept;
}
