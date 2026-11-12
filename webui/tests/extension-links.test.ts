// @ts-ignore: the project has no Node types; vitest runs on Node.
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { chromeStoreUrl, edgeStoreUrl, extensionLinks, extensionLinksHint, firefoxReleaseUrl, pairedExtensionsLine } from '../src/lib/extensionLinks';

// UI.R41: the web UI links where the desktop does (BrowserBridge.cs), to the real store pages only.
describe('extension links', () => {
  const bridge = readFileSync(new URL('../../src/DownloadManager.Contracts/BrowserBridge.cs', import.meta.url), 'utf8');
  const constant = (name: string) => bridge.match(new RegExp(`const string ${name} = "([^"]+)"`))?.[1];

  it('uses the IDs and addresses the desktop uses', () => {
    expect(chromeStoreUrl).toBe(`https://chromewebstore.google.com/detail/${constant('StoreExtensionId')}`);
    expect(edgeStoreUrl).toBe(`https://microsoftedge.microsoft.com/addons/detail/${constant('EdgeStoreExtensionId')}`);
    expect(firefoxReleaseUrl).toBe(constant('FirefoxReleaseUrl'));
  });

  it('offers the three browsers in the desktop order, each over https', () => {
    expect(extensionLinks.map((link) => link.name)).toEqual(['Chrome', 'Edge', 'Firefox']);
    for (const link of extensionLinks) expect(link.url.startsWith('https://')).toBe(true);
  });

  it('tells every web UI user to pair, naming this PC on the Windows app', () => {
    expect(extensionLinksHint('server')).toContain('pair it with this server under Sign-in and browsers');
    // The web UI of the Windows app is often used from another device, where only pairing reaches the PC.
    expect(extensionLinksHint('windows')).toContain('pair it with this PC under Sign-in and browsers');
    expect(extensionLinksHint('linux')).toContain('pair it with this PC');
    expect(extensionLinksHint(undefined)).toContain('pair');
  });

  it('says how many extensions are paired and when one was last used, or nothing', () => {
    expect(pairedExtensionsLine([])).toBeNull();
    expect(pairedExtensionsLine([{ role: 'remoteClient', lastUsedAt: '2026-11-10T10:00:00Z' }])).toBeNull();
    expect(pairedExtensionsLine([{ role: 'extension' }])).toBe('Paired: 1 browser extension · not used yet');
    const line = pairedExtensionsLine([
      { role: 'extension', lastUsedAt: '2026-11-01T12:00:00Z' },
      { role: 'extension', lastUsedAt: '2026-11-11T12:00:00Z' },
      { role: 'extension' },
    ]);
    expect(line).toMatch(/^Paired: 3 browser extensions · last used Nov 1[01] 2026$/);
  });
});
