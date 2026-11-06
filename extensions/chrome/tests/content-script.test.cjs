const assert = require('node:assert/strict');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const esbuild = require('esbuild');

const source = path.join(__dirname, '..', 'src', 'content.ts');
const bundled = esbuild.buildSync({
  entryPoints: [source], bundle: true, write: false, platform: 'node', format: 'cjs', target: 'node20',
}).outputFiles[0].text;

class FakeAnchor {
  constructor(href) { this.href = href; }
}

// Loads a fresh copy of the page script. It has no createElement to reach for: it must not add to the page.
async function withPage(firstRead, elements, run) {
  const names = ['document', 'window', 'location', 'chrome', 'HTMLAnchorElement'];
  const saved = names.map(name => Object.getOwnPropertyDescriptor(global, name));
  const listeners = new Map();
  const sent = [];
  let onMessage;
  const on = (name, listener) => listeners.set(name, listener);
  const off = name => listeners.delete(name);
  const globals = {
    document: { addEventListener: on, removeEventListener: off, hidden: false, querySelectorAll: selector => elements[selector] ?? [] },
    window: { addEventListener: on, removeEventListener: off },
    location: { href: 'https://page.test/list' },
    HTMLAnchorElement: FakeAnchor,
    chrome: { runtime: {
      onMessage: { addListener(listener) { onMessage = listener; } },
      sendMessage(message) { sent.push(message); return firstRead; },
    } },
  };
  for (const name of names) {
    Object.defineProperty(global, name, { value: globals[name], configurable: true, writable: true });
  }
  try {
    const loaded = new Module(source, module);
    loaded.filename = source;
    loaded._compile(bundled, source);
    await new Promise(resolve => setImmediate(resolve));
    const tell = message => new Promise(resolve => onMessage(message, {}, resolve));
    await run({ listeners, sent, tell });
  } finally {
    names.forEach((name, index) => {
      if (saved[index]) Object.defineProperty(global, name, saved[index]);
      else delete global[name];
    });
  }
}

test('the page script asks only about automatic sending, and listens for Delete only while it is on', async () => {
  await withPage(Promise.resolve({ ok: true, autoSend: false }), {}, async ({ listeners, sent, tell }) => {
    assert.deepEqual(sent, [{ type: 'dm.getAutoSend' }]);
    assert.equal(listeners.size, 0);

    await tell({ type: 'dm.autoSendChanged', autoSend: true });
    assert.ok(listeners.has('keydown') && listeners.has('click') && listeners.has('blur'));

    await tell({ type: 'dm.autoSendChanged', autoSend: false });
    assert.equal(listeners.size, 0);
  });
});

test('a live change is not undone by a slower first read', async () => {
  let answerFirstRead;
  const firstRead = new Promise(resolve => { answerFirstRead = resolve; });
  await withPage(firstRead, {}, async ({ listeners, tell }) => {
    await tell({ type: 'dm.autoSendChanged', autoSend: true });
    answerFirstRead({ ok: true, autoSend: false });
    await new Promise(resolve => setImmediate(resolve));
    assert.ok(listeners.has('keydown'));
  });
});

test('holding Delete while clicking a link keeps that one download in Chrome', async () => {
  const url = 'https://file.test/a.zip';
  await withPage(Promise.resolve({ ok: true, autoSend: true }), {}, async ({ listeners, tell }) => {
    listeners.get('keydown')({ isTrusted: true, key: 'Delete' });
    listeners.get('click')({ isTrusted: true, composedPath: () => [new FakeAnchor(url)] });
    assert.deepEqual(await tell({ type: 'dm.consumeKeepInBrowser', url }), { keep: true });
    assert.deepEqual(await tell({ type: 'dm.consumeKeepInBrowser', url }), { keep: false });

    listeners.get('keyup')({ isTrusted: true, key: 'Delete' });
    listeners.get('click')({ isTrusted: true, composedPath: () => [new FakeAnchor(url)] });
    assert.deepEqual(await tell({ type: 'dm.consumeKeepInBrowser', url }), { keep: false });
  });
});

test('Download All still finds links, images, video and audio', async () => {
  const element = (tagName, src) => ({ tagName, textContent: '', getAttribute: name => (name === 'href' || name === 'src' ? src : null) });
  const elements = {
    'a[href]': [element('A', '/a.zip')],
    'img[src]': [element('IMG', '/b.png')],
    'video[src], audio[src], video source[src], audio source[src]': [element('VIDEO', '/c.mp4'), element('AUDIO', '/d.mp3')],
  };
  await withPage(Promise.resolve({ ok: true, autoSend: false }), elements, async ({ tell }) => {
    const response = await tell({ type: 'dm.collectLinks' });
    assert.equal(response.pageUrl, 'https://page.test/list');
    assert.deepEqual(response.links.map(link => [link.url, link.kind]), [
      ['https://page.test/a.zip', 'link'],
      ['https://page.test/b.png', 'image'],
      ['https://page.test/c.mp4', 'video'],
      ['https://page.test/d.mp3', 'audio'],
    ]);
  });
});
