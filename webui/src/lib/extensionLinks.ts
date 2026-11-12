// UI.R41 (review 5:F1): where each browser gets the Inbound extension, as the desktop's Options and Help show it
// (BrowserBridge.ChromeStoreUrl and the rest). Per browser the web UI can't say which extension reached the server (a
// paired extension's token names no browser), so it shows the links and, from the paired apps, one line overall.

import { dateOnly } from './format';

export type ExtensionLink = { browser: 'chrome' | 'edge' | 'firefox'; name: string; from: string; url: string };

export const chromeStoreUrl = 'https://chromewebstore.google.com/detail/olhbcdbjdnfoiinllnlpandggadfabfm';
export const edgeStoreUrl = 'https://microsoftedge.microsoft.com/addons/detail/ifoagddcenmjpbklkhghbdmgplldcdci';
/** Firefox's add-on is signed by Mozilla but not listed on Firefox Add-ons; the .xpi is on each GitHub release. */
export const firefoxReleaseUrl = 'https://github.com/Kaan88/inbound/releases/latest';

export const extensionLinks: readonly ExtensionLink[] = [
  { browser: 'chrome', name: 'Chrome', from: 'Chrome Web Store', url: chromeStoreUrl },
  { browser: 'edge', name: 'Edge', from: 'Microsoft Edge Add-ons', url: edgeStoreUrl },
  { browser: 'firefox', name: 'Firefox', from: 'The latest release on GitHub: drag the .xpi file onto Firefox', url: firefoxReleaseUrl },
];

/**
 * The line over the links. The web UI is always a browser, often on another device, where the extension reaches the
 * app only by pairing; on the PC that runs the app (hostKind windows or linux) it also connects by itself.
 */
export function extensionLinksHint(hostKind: string | null | undefined): string {
  return hostKind === 'windows' || hostKind === 'linux'
    ? 'The extension sends downloads you start in your browser to Inbound. Get it for each browser you use. On the PC that runs Inbound it connects by itself; on another device, pair it with this PC under Sign-in and browsers.'
    : 'The extension sends downloads you start in your browser here. Get it, then pair it with this server under Sign-in and browsers.';
}

export type PairedApp = { role?: string; lastUsedAt?: string };

/**
 * One line from the paired apps (GET /api/auth/tokens): how many browser extensions are paired and when one was last
 * used. Null when none is, so nothing is said. Not per browser: a pairing names no browser.
 */
export function pairedExtensionsLine(tokens: readonly PairedApp[]): string | null {
  const paired = tokens.filter((token) => token.role === 'extension');
  if (paired.length === 0) return null;
  const count = paired.length === 1 ? '1 browser extension' : `${paired.length} browser extensions`;
  const last = paired
    .map((token) => (token.lastUsedAt ? Date.parse(token.lastUsedAt) : NaN))
    .filter((time) => !Number.isNaN(time))
    .reduce((newest, time) => Math.max(newest, time), Number.NEGATIVE_INFINITY);
  return Number.isFinite(last) ? `Paired: ${count} · last used ${dateOnly(last)}` : `Paired: ${count} · not used yet`;
}
