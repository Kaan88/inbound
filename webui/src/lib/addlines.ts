// The add dialog's lines: what the server said each one is, and where it may go (docs/design-guidelines.md, "In the
// add dialogs"). A hoster, folder or magnet link only ever goes to the provider; a link nobody could classify
// goes directly only when its own box is ticked.

import type { ClassifyResult } from './types';

export interface AddLine {
  input: string;
  position: number;
  result: ClassifyResult | null;
  error: string | null;
  /** The user ticked "Download directly" for a line nobody could classify. */
  direct: boolean;
  /** Ticked to be added. Starts from the desktop's defaults ({@link defaultSelected}) until the user says otherwise. */
  selected?: boolean;
  /** The user ticked or unticked it, so a later answer from the server doesn't change it. */
  touched?: boolean;
  /** A download with this address is already on the list. */
  listed?: boolean;
  /** For an uploaded .torrent file: its name, because the input is only a reference to the upload. */
  label?: string;
}

export type Route = 'checking' | 'direct' | 'provider' | 'none';

/** The non-empty lines, trimmed, each once, at most 1,000. */
export function linesOf(text: string): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || seen.has(line)) continue;
    seen.add(line);
    result.push(line);
    if (result.length >= 1000) break;
  }
  return result;
}

function isWebLink(input: string): boolean {
  return /^https?:\/\//i.test(input);
}

export function cantTell(line: AddLine): boolean {
  return !!line.result && (line.result.ok === false || line.result.kind === 'undetermined');
}

export function routeOf(line: AddLine): Route {
  const result = line.result;
  if (!result) return 'checking';
  if (cantTell(line)) return line.direct && isWebLink(line.input) ? 'direct' : 'none';
  switch (result.kind) {
    case 'ordinaryLink':
    case 'providerDirectLink':
      return 'direct';
    case 'magnet':
    case 'torrentFile':
    case 'hosterLink':
      return 'provider';
    case 'folderLink':
      // Only a folder link that names its provider, item kind and item can be added.
      return result.itemId && result.itemKind && result.provider ? 'provider' : 'none';
    default:
      // Includes any kind this build has never heard of.
      return 'none';
  }
}

export function kindWord(line: AddLine, provider: string): string {
  const result = line.result;
  if (!result) return 'Checking…';
  if (cantTell(line)) return line.direct ? 'Direct, as chosen' : "Can't tell";
  switch (result.kind) {
    case 'folderLink': return 'Folder link';
    case 'magnet': return 'Magnet';
    case 'torrentFile': return 'Torrent file';
    case 'hosterLink': return `Hoster through ${provider}`;
    case 'providerDirectLink': return 'Direct provider link';
    case 'ordinaryLink': return 'Ordinary link';
    default: return 'Unsupported';
  }
}

/** Only an ordinary link shows its address; anything that can hold a secret shows its site and position alone. */
export function showsAddress(line: AddLine): boolean {
  return line.result?.kind === 'ordinaryLink' && line.result.ok !== false;
}

/**
 * The dialog after a partly failed add: the lines that were added are taken out of both the list and the text, so
 * pressing Add again sends only the ones that failed. Other text lines are kept as typed.
 */
export function withoutAdded(lines: AddLine[], text: string, added: Iterable<string>): { lines: AddLine[]; text: string } {
  const gone = new Set(added);
  if (gone.size === 0) return { lines, text };
  return {
    lines: lines.filter((line) => !gone.has(line.input)).map((line, index) => ({ ...line, position: index + 1 })),
    text: removeInputs(text, gone),
  };
}

/**
 * The pasted text without the links that were handled, so adding again sends only the rest. A link goes only as a whole
 * word (between spaces or line ends), so one that starts like a failed one cannot cut it. Other text is kept as typed.
 */
export function removeInputs(text: string, handled: Iterable<string>): string {
  const gone = [...handled].filter((input) => input);
  const kept: string[] = [];
  for (const original of text.split(/\r?\n/)) {
    let line = original;
    for (const input of gone) {
      const escaped = input.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      line = line.replace(new RegExp(`(^|\\s)${escaped}(?=$|\\s)`, 'g'), '$1');
    }

    // A line that held only handled links disappears; a blank line the user typed stays.
    if (original.trim() && !line.trim()) continue;
    kept.push(line.trimEnd());
  }

  return kept.join('\n');
}
