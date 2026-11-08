// What the add dialog shows and ticks, in the desktop's words and with its defaults (AddLines.cs, AddLinksDialog.cs):
// extraction of links from text, the ticks, the summary line, each line's words, and the file choice. No DOM here.

import { removeInputs, routeOf, type AddLine } from './addlines';

export { removeInputs };
import type { ClassifyResult, RemoteFile } from './types';

const TRAILING = /[.,;!?]+$/;
const BRACKETS: Record<string, string> = { ')': '(', ']': '[', '}': '{' };

/** Trailing punctuation goes; a closing bracket goes only when nothing in the link opened it (a wiki address ends in one). */
export function trimLink(link: string): string {
  let out = link;
  for (;;) {
    const before = out;
    out = out.replace(TRAILING, '');
    const last = out.slice(-1);
    const open = BRACKETS[last];
    if (open && out.split(last).length - 1 > out.split(open).length - 1) out = out.slice(0, -1);
    if (out === before) return out;
  }
}

/**
 * Links and magnets anywhere in pasted text, in the order they appear, trailing punctuation trimmed and each once
 * (case counts: a hoster link's key is case-sensitive). Plain text that is not a link is ignored. At most 1,000.
 */
export function extractInputs(text: string | null | undefined): string[] {
  if (!text || !text.trim()) return [];
  const seen = new Set<string>();
  const found: string[] = [];
  for (const match of text.matchAll(/(?:(?:https?|ftp):\/\/|magnet:)[^\s"'<>`]+/gi)) {
    const input = trimLink(match[0]);
    if (!/^(?:(?:https?|ftp):\/\/.|magnet:.)/i.test(input) || seen.has(input)) continue;
    seen.add(input);
    found.push(input);
    if (found.length >= 1000) break;
  }
  return found;
}

export function isMagnet(input: string): boolean {
  return /^magnet:/i.test(input);
}

/** A reference to a file uploaded to the server (see /api/upload/torrent). */
export function isUpload(input: string): boolean {
  return input.startsWith('upload:');
}

export function newLine(input: string, position: number, listed = false): AddLine {
  return { input, position, result: null, error: null, direct: false, listed, selected: true, touched: false };
}

/** The provider says the site is down, or has no links left for it today. Adding it would most likely just fail. */
export function hosterLooksUnusable(line: AddLine): boolean {
  const r = line.result;
  return r?.kind === 'hosterLink' && (r.hosterAvailable === false || r.hosterDailyLinksLeft === 0 || r.hosterDailyBytesLeft === 0);
}

export function canSelect(line: AddLine): boolean {
  return routeOf(line) !== 'none';
}

/** The desktop's defaults: unusable hosters and listed direct links start unticked; checking and folder links ticked. */
export function defaultSelected(line: AddLine, skipListed: boolean): boolean {
  switch (routeOf(line)) {
    case 'checking': return true;
    case 'provider': return line.result?.kind === 'folderLink' ? true : !hosterLooksUnusable(line);
    case 'direct': return !(line.listed && skipListed);
    default: return false;
  }
}

/** Applies the defaults to lines the user has not touched; a touched line that can no longer be added goes off. */
export function withDefaults(lines: AddLine[], skipListed: boolean): AddLine[] {
  return lines.map((line) => {
    const selected = !line.touched ? defaultSelected(line, skipListed) : canSelect(line) ? !!line.selected : false;
    return selected === line.selected ? line : { ...line, selected };
  });
}

/** The lines that will be added: ticked, and known to go somewhere. */
export function chosen(lines: AddLine[]): AddLine[] {
  return lines.filter((line) => line.selected && routeOf(line) !== 'checking' && routeOf(line) !== 'none');
}

/** At least one ticked line goes somewhere, and none ticked is still being checked. */
export function canAdd(lines: AddLine[]): boolean {
  return lines.some((line) => line.selected && (routeOf(line) === 'direct' || routeOf(line) === 'provider'))
    && !lines.some((line) => line.selected && routeOf(line) === 'checking');
}

/** The Select all box: true when every selectable line is ticked, false when none is, null (mixed) otherwise. */
export function selectAllState(lines: AddLine[]): boolean | null {
  const selectable = lines.filter(canSelect);
  const ticked = selectable.filter((line) => line.selected).length;
  if (ticked === 0) return false;
  return ticked === selectable.length ? true : null;
}

/** What a click on Select all does: everything goes off only when everything is on. From mixed, it all goes on. */
export function toggleAll(lines: AddLine[]): AddLine[] {
  const on = selectAllState(lines) !== true;
  return lines.map((line) => ({ ...line, touched: true, selected: on && canSelect(line) }));
}

/** The status line above the list. */
export function addSummary(lines: AddLine[], hasText: boolean, magnetsOnly = false): { text: string; problem: boolean } {
  if (lines.length === 0) {
    return { text: hasText ? (magnetsOnly ? 'No magnet was found in this text.' : 'No links were found in this text.') : '', problem: hasText };
  }

  const checking = lines.filter((line) => routeOf(line) === 'checking').length;
  if (checking > 0) return { text: `Checking ${checking} of ${lines.length}…`, problem: false };
  const ticked = lines.filter((line) => line.selected).length;
  const stuck = lines.filter((line) => routeOf(line) === 'none').length;
  return {
    text: stuck === 0 ? `${ticked} of ${lines.length} selected.` : `${ticked} of ${lines.length} selected. ${stuck} can't be added as ${stuck === 1 ? 'it is' : 'they are'}.`,
    problem: false,
  };
}

/** Addresses in a note are taken out, as everywhere in the app: only the site stays. */
export function hideAddresses(text: string | null | undefined): string {
  if (!text) return '';
  return text.replace(/\b(?:https?|ftp):\/\/[^\s"'<>]+/gi, (url) => {
    try { return new URL(url).hostname; } catch { return 'an address'; }
  });
}

export interface LineWords {
  name: string;
  word: string;
  detail: string;
  problem: boolean;
  canSelect: boolean;
  canRetry: boolean;
  /** A link nobody could classify may be downloaded directly, but only if the user says so. */
  canChooseDirect: boolean;
  /** Goes to a site through the provider, and such a site can want a password for the link. */
  canTakeLinkPassword: boolean;
}

/** Sizes the way the desktop's folder line writes them. */
export function spaceSize(value: number): string {
  const kb = 1024, mb = kb * 1024, gb = mb * 1024, tb = gb * 1024;
  const fixed = (n: number, digits: number) => n.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });
  return value >= tb ? `${fixed(value / tb, 1)} TB` : value >= gb ? `${fixed(value / gb, 1)} GB` : value >= mb ? `${fixed(value / mb, 0)} MB` : `${fixed(value / kb, 0)} KB`;
}

function kindSingular(itemKind: string | undefined): string {
  switch (itemKind) {
    case 'torrents': return 'Torrent';
    case 'usenet': return 'Usenet download';
    case 'webdl': return 'Web download';
    default: return 'Item';
  }
}

function hosterDetails(r: ClassifyResult, provider: string): string[] {
  const out: string[] = [];
  if (r.hosterAvailable === false) out.push(`${provider} says this site isn't available right now, so adding it will probably fail.`);
  else if (r.hosterAvailable === true) out.push(`${provider} says this site is available.`);
  if (r.hosterNote?.trim()) {
    const note = hideAddresses(r.hosterNote).trimEnd();
    out.push(/[.!?]$/.test(note) ? note : `${provider} notes: ${note}.`);
  }

  const limits: string[] = [];
  if (r.hosterDailyLinksLeft != null) limits.push(r.hosterDailyLinksLeft === 0 ? 'no links left today' : r.hosterDailyLinksLeft === 1 ? '1 link left today' : `${r.hosterDailyLinksLeft} links left today`);
  if (r.hosterDailyBytesLeft != null) limits.push(r.hosterDailyBytesLeft > 0 ? `${spaceSize(r.hosterDailyBytesLeft)} left today` : 'no data left today');
  if (r.hosterLargestFile != null) limits.push(`largest file ${spaceSize(r.hosterLargestFile)}`);
  if (limits.length) out.push(`Limits ${provider} gives for this site: ${limits.join(', ')}.`);
  return out;
}

/** The name a line shows: host names and file names only. The rest of an address can be a key and is never shown. */
export function lineName(line: AddLine, many = true): string {
  const input = line.input;
  const r = line.result;
  if (isMagnet(input)) return r?.name?.trim() || 'Magnet link';
  if (isUpload(input)) return line.label?.trim() || r?.name?.trim() || 'Torrent file';
  let host = 'This link';
  let segment = '';
  try {
    const url = new URL(input);
    host = url.hostname;
    segment = decodeURIComponent(url.pathname.split('/').filter(Boolean).pop() ?? '');
  } catch { /* not an address */ }
  if (r?.kind === 'ordinaryLink' && host !== 'This link') return segment ? hideAddresses(segment) : host;
  if (r?.name?.trim()) return hideAddresses(r.name);
  if (r?.kind === 'folderLink' && r.itemId) return `${kindSingular(r.itemKind)} ${r.itemId}`;
  const shown = r?.displayHost?.trim() || host;
  return many && line.position > 0 ? `${shown} (link ${line.position})` : shown;
}

/** What a line of the add dialog says about itself, in the desktop's words. */
export function lineWords(line: AddLine, fallbackProvider: string, many = true): LineWords {
  const r = line.result;
  const provider = r?.providerName?.trim() || fallbackProvider;
  const name = lineName(line, many);
  const details: string[] = [];
  const note = () => { if (r?.note?.trim()) details.push(hideAddresses(r.note)); };
  let word = 'Checking…';
  let problem = false;
  let retry = false;
  let direct = false;
  let password = false;
  const cantTell = !!r && (r.ok === false || r.kind === 'undetermined');
  if (cantTell && r) {
    word = "Can't tell";
    details.push(hideAddresses(r.ok === false ? r.error : r.reason) || "The server couldn't tell what this is.");
    note();
    direct = /^https?:\/\//i.test(line.input);
    if (direct) details.push("So it isn't treated as an ordinary link unless you say so.");
    problem = true;
    retry = true;
  } else if (r) {
    switch (r.kind) {
      case 'folderLink': word = 'Folder link'; details.push(`Refers to an item on your ${provider} account.`); note(); break;
      case 'magnet': word = 'Magnet'; details.push(`Goes through ${provider}.`); note(); break;
      case 'torrentFile': word = 'Torrent file'; details.push(`Goes through ${provider}.`); note(); break;
      case 'hosterLink':
        word = `Hoster through ${provider}`;
        details.push(...hosterDetails(r, provider));
        note();
        problem = hosterLooksUnusable(line);
        password = true;
        break;
      case 'providerDirectLink': word = 'Direct provider link'; note(); break;
      case 'ordinaryLink':
        word = 'Ordinary link';
        if (line.listed) details.push('Already in the list');
        note();
        break;
      default:
        word = 'Unsupported';
        details.push(r.reason?.trim() ? hideAddresses(r.reason) : "This app can't use this.");
        problem = true;
        break;
    }
  }

  if (line.direct && cantTell) {
    details.length = 0;
    details.push(`It will download directly, not through ${provider}, because you chose that.`);
    problem = false;
  }

  return { name, word, detail: details.join(' '), problem, canSelect: canSelect(line), canRetry: retry, canChooseDirect: direct, canTakeLinkPassword: password };
}

/** The files a magnet or .torrent file lists, to choose from straight after Add: known now, with more than one that can be chosen. */
export function filesToChoose(line: AddLine): RemoteFile[] | null {
  const r = line.result;
  if (routeOf(line) !== 'provider' || !r || (r.kind !== 'magnet' && r.kind !== 'torrentFile')) return null;
  const files = r.files ?? [];
  return files.length > 1 && files.filter((file) => file.selectable !== false).length > 1 ? files : null;
}

/** Where "Let me choose files when … is done" means something: a ticked magnet or torrent whose files aren't known and that isn't known to be cached. */
export function chooseLaterApplies(line: AddLine): boolean {
  const r = line.result;
  return !!line.selected && routeOf(line) === 'provider' && !!r && (r.kind === 'magnet' || r.kind === 'torrentFile') && !r.files && r.cached !== true;
}

/** The selection a provider add is sent with, and the files that go with it. Null leaves it to the server's rule. */
export interface FileChoice {
  selection: 'ask' | 'all' | 'chosen';
  fileIds?: string[];
  filePaths?: { path: string; size: number }[];
}

/** From the file choice dialog: a magnet's files are the provider's and go by id; a torrent file's go by path and size. */
export function fileChoiceFrom(line: AddLine, chosenIds: Set<string>): FileChoice {
  const files = line.result?.files ?? [];
  const choosable = files.filter((file) => file.selectable !== false);
  if (chosenIds.size === choosable.length) return { selection: 'all' };
  if (line.result?.kind !== 'torrentFile') return { selection: 'chosen', fileIds: [...chosenIds] };
  const picked = files.filter((file) => chosenIds.has(file.fileId));
  // A torrent file goes by path and size; if the server gave a path to none or only some, the ids carry it instead.
  if (picked.some((file) => !file.relativePath)) return { selection: 'chosen', fileIds: picked.map((file) => file.fileId) };
  return { selection: 'chosen', filePaths: picked.map((file) => ({ path: file.relativePath!, size: file.size })) };
}

// ---- The words after an add ----

/** The words after an add that went through, empty when nothing new was added. */
export function addedWords(downloads: number, items: number, provider: string): string {
  const parts: string[] = [];
  if (downloads > 0) parts.push(downloads === 1 ? '1 download' : `${downloads} downloads`);
  if (items > 0) parts.push(items === 1 ? `1 item for ${provider}` : `${items} items for ${provider}`);
  return parts.length === 0 ? '' : `Added ${parts.join(' and ')}.`;
}

/** Everything that went wrong or was there already, each on its own line and none left out, after what was added. */
export function addReport(added: string, messages: string[], problems: string[]): string {
  const lines = [...messages];
  if (added) lines.push(added);
  lines.push(...problems);
  return lines.join('\n');
}

/** A line of an add that was refused, with the reason. */
export function refused(name: string, error: string | null | undefined, provider?: string): string {
  const reason = hideAddresses(error) || `${provider ?? 'The server'} couldn't take this.`;
  return `${name}: ${reason}`;
}

/** For something added again. Never "added": it was on the list already, and the note says what of the request it didn't use. */
export function alreadyListed(name: string, note?: string | null): string {
  const said = note?.trim() ? hideAddresses(note).trim() : null;
  return said === null || !/already on the list/i.test(said) ? `${name}: already on the list.${said === null ? '' : ` ${said}`}` : `${name}: ${said}`;
}

/** The destination for a provider's work has to be a full path on the server. */
export function isFullPath(folder: string): boolean {
  return /^(?:[a-z]:[\\/]|\\\\|\/)/i.test(folder.trim());
}
