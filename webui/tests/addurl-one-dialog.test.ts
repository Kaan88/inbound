import { describe, expect, it } from 'vitest';
import addUrlSource from '../src/screens/AddUrl.svelte?raw';
import appSource from '../src/App.svelte?raw';
import {
  addressCheckDelay, checkWithCredentialsWords, credentialsClearedWords, handoffWords, leavesSite, noTorrentsWords, notALinkWords, oneLine, providerWords,
  readAddress, siteOfAddress,
} from '../src/lib/addflow';

// UI.R38: Add URL is one dialog, as on the desktop; what it makes of the text in its address box.
const magnet = 'magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Sample';

describe('Add URL reads its address box', () => {
  it('nothing typed is empty', () => {
    for (const text of [null, undefined, '', '   ']) expect(readAddress(text, false).kind).toBe('empty');
  });

  it('one address is added here', () => {
    for (const text of ['https://example.com/file.zip', '  http://192.168.1.20/share/file.iso  ', 'https://example.com/some.torrent']) {
      const input = readAddress(text, false);
      expect(input.kind).toBe('oneLink');
      expect(input.address).toBe(text.trim());
    }
  });

  it('one link in a sentence is that link', () => {
    const input = readAddress('Here it is: https://example.com/file.zip Thanks!', false);
    expect(input.kind).toBe('oneLink');
    expect(input.address).toBe('https://example.com/file.zip');
  });

  it('a numbered address goes to Add links as each of its numbers', () => {
    const input = readAddress('https://example.com/part[1-3].rar', false);
    expect(input.kind).toBe('severalLinks');
    expect(input.links).toEqual(['https://example.com/part1.rar', 'https://example.com/part2.rar', 'https://example.com/part3.rar']);
    expect(handoffWords(input, false)).toMatch(/^3 links\. Continue opens Add links/);
  });

  it('pasted text with several links goes to Add links, magnets too with a provider', () => {
    const text = `https://example.com/a.zip\nhttps://example.com/b.zip and ${magnet}`;
    expect(readAddress(text, false).links).toEqual(['https://example.com/a.zip', 'https://example.com/b.zip']);
    const withProvider = readAddress(text, true);
    expect(withProvider.kind).toBe('severalLinks');
    expect(withProvider.links).toContain(magnet);
  });

  it('a magnet is a torrent, for the torrent dialog', () => {
    const input = readAddress(magnet, false);
    expect(input.kind).toBe('torrent');
    expect(handoffWords(input, true)).toBe('A torrent. Continue opens the torrent dialog.');
  });

  it('ftp says it is not supported, and anything else asks for an address', () => {
    expect(readAddress('ftp://example.com/file.zip', false).kind).toBe('ftp');
    for (const text of ['example.com/file.zip', 'just some words']) expect(readAddress(text, false).kind).toBe('notALink');
    expect(notALinkWords(false)).toBe('Enter an absolute http or https URL.');
    expect(notALinkWords(true)).toContain('magnet');
  });

  it('says where a provider link goes', () => {
    expect(providerWords('hosterLink', ' Rapid Site ', 'TorBox')).toBe('Rapid Site goes through TorBox. Continue opens the add dialog for it.');
    expect(providerWords('folderLink', null, 'TorBox')).toBe('A link to an item on your TorBox account. Continue opens the add dialog for it.');
  });

  it('a pasted list stays several links in the one-line box', () => {
    // The browser would glue the lines into one broken address; they go in side by side instead.
    for (const pasted of ['https://example.com/a.zip\r\nhttps://example.com/b.zip\r\n', 'https://example.com/a.zip\nhttps://example.com/b.zip', 'https://example.com/a.zip \n\n\thttps://example.com/b.zip']) {
      const line = oneLine(pasted);
      expect(line).not.toMatch(/[\r\n]/);
      const input = readAddress(line, false);
      expect(input.kind).toBe('severalLinks');
      expect(input.links).toEqual(['https://example.com/a.zip', 'https://example.com/b.zip']);
    }
    expect(oneLine('https://example.com/a b.zip')).toBe('https://example.com/a b.zip');
    const other = 'magnet:?xt=urn:btih:89abcdef0123456789abcdef0123456789abcdef&dn=Other';
    expect(readAddress(oneLine(`${magnet}\n${other}`), true).links).toEqual([magnet, other]);
  });

  it('waits for the address to stop changing before checking', () => {
    expect(addressCheckDelay).toBeGreaterThanOrEqual(250);
    expect(addressCheckDelay).toBeLessThanOrEqual(800);
  });
});

describe('Add URL is one dialog', () => {
  it('has no Next step, and keeps its options', () => {
    expect(addUrlSource).not.toMatch(/>\s*Next\s*</);
    expect(addUrlSource).toContain("title={step === 'duplicate' ? 'Download already exists' : 'Add download'}");
    for (const words of ['Download later', 'Start download', 'Download directly, not through', 'Use this folder next time too', 'Authentication (optional)', 'Queue and file conflicts', 'Create a category'])
      expect(addUrlSource).toContain(words);
    expect(addUrlSource).toContain('setTimeout(');
    expect(addUrlSource).toContain('addressCheckDelay');
  });

  it('catches a paste before the browser glues its lines together', () => {
    expect(addUrlSource).toContain('onpaste={paste}');
    expect(addUrlSource).toMatch(/function paste\(event: ClipboardEvent\)[\s\S]*?event\.preventDefault\(\)[\s\S]*?oneLine\(text\)/);
  });

  it('an edit after Enter neither starts the edited text nor leaves an unfinished check behind', () => {
    // schedule(): a new text drops the waiting Enter and what counts as checked.
    expect(addUrlSource).toMatch(/function schedule\(\) \{[\s\S]*?evaluated = null;[\s\S]*?goWhenReady = null;[\s\S]*?\n  \}/);
    expect(addUrlSource).toMatch(/function evaluate\(text: string\)[\s\S]*?goWhenReady = null;/);
  });

  // Review of 0.7.2, ux-3: Download later pressed while the address is still being checked goes on, as Start does.
  it('remembers which button was pressed before the answer came', () => {
    expect(addUrlSource).toMatch(/goWhenReady = later \? 'later' : 'start';/);
    expect(addUrlSource).toMatch(/else if \(ready\) void start\(wanted === 'later'\);/);
  });

  // Review of 0.7.2, ux-4: torrents off and no provider account: say so, not 'enter an http address'.
  it('says the server does not download torrents for a magnet it cannot take', () => {
    expect(noTorrentsWords).toBe("This server doesn't download torrents.");
    expect(addUrlSource).toMatch(/if \(!providerAdd\) \{\s*\/\/[^\n]*\n\s*showNoLink\(noTorrentsWords\);/);
  });

  // Review of 0.7.2, ux-5: the Paste button flattens a list as a keyboard paste does.
  it('the Paste button puts a list in side by side', () => {
    expect(addUrlSource).toContain('const clip = oneLine((await navigator.clipboard.readText()).trim());');
  });

  // Review of 0.7.2, security-1: credentials typed for one site never go to another by themselves.
  it('a user name and password stay with the site they were typed for', () => {
    expect(leavesSite(siteOfAddress('https://files.corp.example/a'), 'https://files.corp.example/b?x=1')).toBe(false);
    expect(leavesSite(siteOfAddress('https://files.corp.example/a'), 'https://FILES.corp.example:443/b')).toBe(false);
    for (const other of ['http://files.corp.example/a', 'https://files.corp.example:8443/a', 'https://files.co', 'http://mirror.other.example/b'])
      expect(leavesSite(siteOfAddress('https://files.corp.example/a'), other)).toBe(true);
    expect(leavesSite(null, 'https://example.com/a')).toBe(false);
    expect(siteOfAddress('not an address')).toBeNull();
  });

  it('the check that runs by itself never sends the credentials, and another site clears them', () => {
    expect(addUrlSource).toMatch(/async function previewMetadata\(automatic = false\) \{\s*if \(!url\) return;\s*if \(automatic && useAuth\) \{[\s\S]*?info = checkWithCredentialsWords;\s*return;/);
    expect(addUrlSource).toContain('void previewMetadata(true);');
    expect(addUrlSource).toMatch(/if \(changed && useAuth && leavesSite\(credentialsSite, text\)\) \{\s*password = '';\s*useAuth = false;/);
    expect(credentialsClearedWords).toContain('cleared');
    expect(checkWithCredentialsWords).toContain('Check again');
  });

  it('shows only the site of a link that holds an account key', () => {
    expect(addUrlSource).toMatch(/answer\?\.kind === 'providerDirectLink'\) showInBox\(answer\.displayHost/);
    expect(addUrlSource).toContain('{addressLabel}</label>');
  });

  it('sends several links to Add links', () => {
    expect(appSource).toMatch(/onlinks=\{\(links\) => \(adding = \{ kind: 'many', mode: 'links', inputs: links\.map/);
  });
});
