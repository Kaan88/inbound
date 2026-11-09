// Numbered addresses (U-WU-11), as the desktop's batch dialog and the CLI expand them (BatchUrlExpander):
// `img[1-3].jpg` is img1..img3, `[01-10]` keeps the zeros, `[a-c]` runs over letters of one case, `[x,y,z]` is a list,
// and several groups multiply. Only brackets after the host count, so an IPv6 address like http://[::1]/ is left alone,
// and a bracket pair that is neither a range nor a list (`?a[]=1`) stays as it is.

export const batchLimit = 1000;

export type Expanded = { ok: true; urls: string[] } | { ok: false; error: string };

/** Null when the address has no range or list to expand. */
export function expandNumbered(input: string, limit = batchLimit): Expanded | null {
  const start = afterHost(input);
  if (start < 0) return null;
  const groups: { at: number; length: number; values: string[] | string }[] = [];
  const pattern = /\[([^[\]]*)\]/g;
  pattern.lastIndex = start;
  for (let match = pattern.exec(input); match; match = pattern.exec(input)) {
    if (match.index < start) continue;
    const token = match[1];
    if (!token.includes('-') && !token.includes(',')) continue;
    groups.push({ at: match.index, length: match[0].length, values: valuesOf(token) });
  }
  if (groups.length === 0) return null;
  const problem = groups.find((group) => typeof group.values === 'string');
  if (problem) return { ok: false, error: problem.values as string };

  let urls = [''];
  let from = 0;
  for (const group of groups) {
    const before = input.slice(from, group.at);
    const values = group.values as string[];
    if (urls.length * values.length > limit) return { ok: false, error: `This pattern makes more than ${limit} addresses.` };
    urls = urls.flatMap((url) => values.map((value) => url + before + value));
    from = group.at + group.length;
  }
  const rest = input.slice(from);
  return { ok: true, urls: urls.map((url) => url + rest) };
}

function afterHost(input: string): number {
  const scheme = /^(?:https?|ftp):\/\//i.exec(input);
  if (!scheme) return -1;
  const slash = input.indexOf('/', scheme[0].length);
  const query = input.indexOf('?', scheme[0].length);
  const ends = [slash, query].filter((index) => index >= 0);
  return ends.length ? Math.min(...ends) : input.length;
}

function valuesOf(token: string): string[] | string {
  if (token.includes(',')) {
    const items = token.split(',').map((item) => item.trim());
    return items.some((item) => !item) ? `The list [${token}] has an empty item.` : items;
  }
  const parts = token.split('-');
  if (parts.length !== 2 || !parts[0].trim() || !parts[1].trim()) return `The range [${token}] must be two numbers or two letters, such as [1-10] or [a-f].`;
  const [first, last] = parts.map((part) => part.trim());
  if (/^\d+$/.test(first) && /^\d+$/.test(last)) {
    const a = Number(first);
    const b = Number(last);
    const pad = (first.length > 1 && first.startsWith('0')) || (last.length > 1 && last.startsWith('0')) ? Math.max(first.length, last.length) : 0;
    if (Math.abs(b - a) >= batchLimit) return `The range [${token}] makes more than ${batchLimit} addresses.`;
    const step = a <= b ? 1 : -1;
    const values: string[] = [];
    for (let n = a; ; n += step) {
      values.push(pad ? String(n).padStart(pad, '0') : String(n));
      if (n === b) break;
    }
    return values;
  }
  if (/^[a-z]$/.test(first) === /^[a-z]$/.test(last) && /^[a-zA-Z]$/.test(first) && /^[a-zA-Z]$/.test(last)) {
    const a = first.charCodeAt(0);
    const b = last.charCodeAt(0);
    const step = a <= b ? 1 : -1;
    const values: string[] = [];
    for (let c = a; ; c += step) {
      values.push(String.fromCharCode(c));
      if (c === b) break;
    }
    return values;
  }
  return `The range [${token}] must use numbers, or single letters of the same case.`;
}
