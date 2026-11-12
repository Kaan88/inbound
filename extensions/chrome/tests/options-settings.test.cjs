// EXT.R8 §8 and the Options findings of the usability test (U-EX-5, U-EX-6, U-EX-13, U-EX-17), in a real DOM.
const assert = require('node:assert/strict');
const test = require('node:test');
const { installDom, flush } = require('./dom-harness.cjs');
const { sendRuntimeMessage, withBackground, makeChrome, catching, ownPage } = require('./harness.cjs');
const data = require('./fixtures/screens-data.json');

const stored = (overrides = {}) => ({ ...data.settings, ...overrides });

async function openOptions({ settings = stored(), hash = '', server = null, save } = {}) {
  const saves = [];
  const page = installDom('options.html', { hash, handlers: {
    'dm.status': async () => ({ ok: true, settings, connected: true, server, extensionVersion: '0.6.0' }),
    'dm.saveSettings': async (message) => {
      saves.push(message);
      if (save) return save(message);
      return { ok: true, settings: { ...settings, ...message.changes } };
    },
  } });
  page.load('options.ts');
  await page.ready();
  await flush(10);
  return { page, saves, doc: page.document };
}

test('U-EX-5: one box for automatic sending: on only with both keys on, and changing it writes both', async () => {
  const { page, saves, doc } = await openOptions({ settings: stored({ enabled: true, autoIntercept: false }) });
  try {
    const box = doc.getElementById('autoSend');
    assert.equal(box.checked, false, 'off while either key is off');
    assert.equal(doc.querySelectorAll('input[type=checkbox]').length, 3, "automatic sending, notifications and the app's windows only");
    box.checked = true;
    box.dispatchEvent(new page.window.Event('change', { bubbles: true }));
    doc.getElementById('apply').click();
    await flush();
    // Only what was off is sent; the other key was on already. Both are on afterwards.
    assert.deepEqual(saves[0].changes, { autoIntercept: true });
  } finally { page.restore(); }
});

test('the #hash picks the page; #privacy is About', async () => {
  for (const [hash, page] of [['#catching', 'catching'], ['#server', 'server'], ['#privacy', 'about'], ['#nonsense', 'general']]) {
    const { page: opened, doc } = await openOptions({ hash });
    try {
      const shown = [...doc.querySelectorAll('.settings-page')].filter((section) => !section.classList.contains('hidden')).map((s) => s.dataset.page);
      assert.deepEqual(shown, [page], hash);
      assert.equal(doc.querySelector('.page-link[aria-current=page]').dataset.page, page);
    } finally { opened.restore(); }
  }
});

test('U-EX-6: Apply sends only what changed here, so an older tab never puts back what was saved elsewhere', async () => {
  const { page, saves, doc } = await openOptions();
  try {
    // Saved elsewhere meanwhile (the popup's This PC / Server switch, or another Options tab).
    page.storeElsewhere(stored({ destinationMode: 'ask', startMode: 'later' }));
    await flush();
    assert.equal(doc.querySelector('input[name=destinationMode]:checked').value, 'ask', 'an untouched field shows the new value');
    assert.equal(doc.getElementById('elsewhereNote').classList.contains('hidden'), false);
    doc.getElementById('notifications').checked = false;
    doc.getElementById('notifications').dispatchEvent(new page.window.Event('change', { bubbles: true }));
    doc.getElementById('apply').click();
    await flush();
    assert.deepEqual(saves.at(-1).changes, { notifications: false });
    assert.equal('settings' in saves.at(-1), false, 'never the whole, older settings object');
  } finally { page.restore(); }
});

test('U-EX-6: in the background, a save of changes is laid over the settings stored now', async () => {
  const harness = makeChrome({ ...catching, destinationMode: 'default', startMode: 'now' }, { id: 'seed' });
  await withBackground(harness, async () => {
    await sendRuntimeMessage(harness, { type: 'dm.saveSettings', changes: { destinationMode: 'ask' } }, ownPage('options.html'));
    const reply = await sendRuntimeMessage(harness, { type: 'dm.saveSettings', changes: { notifications: false, bogus: 1 } }, ownPage('options.html'));
    assert.equal(reply.settings.destinationMode, 'ask', 'the first save survives the second');
    assert.equal(reply.settings.notifications, false);
    assert.equal('bogus' in reply.settings, false);
    assert.equal(harness.values.settings.destinationMode, 'ask');
  });
});

test('U-EX-6: leaving with unsaved changes asks; Cancel discards and closes the tab without saving', async () => {
  const { page, saves, doc } = await openOptions();
  try {
    doc.getElementById('excludedHosts').value = 'example.com';
    doc.getElementById('excludedHosts').dispatchEvent(new page.window.Event('input', { bubbles: true }));
    assert.equal(doc.getElementById('apply').disabled, false);
    const leaving = new page.window.Event('beforeunload', { cancelable: true });
    page.window.dispatchEvent(leaving);
    assert.equal(leaving.defaultPrevented, true, 'the browser asks before the changes are lost');
    doc.getElementById('cancel').click();
    await flush();
    assert.deepEqual(saves, []);
    assert.deepEqual(page.removedTabs, [7]);
    const again = new page.window.Event('beforeunload', { cancelable: true });
    page.window.dispatchEvent(again);
    assert.equal(again.defaultPrevented, false, 'nothing left to lose');
  } finally { page.restore(); }
});

test('an invalid entry selects its page and blocks OK', async () => {
  const { page, saves, doc } = await openOptions();
  try {
    doc.getElementById('excludedExtensions').value = 'zip, tar.gz';
    doc.getElementById('excludedExtensions').dispatchEvent(new page.window.Event('input', { bubbles: true }));
    doc.getElementById('ok').click();
    await flush();
    assert.deepEqual(saves, []);
    assert.deepEqual(page.removedTabs, []);
    assert.equal(doc.getElementById('page-catching').classList.contains('hidden'), false);
    assert.match(doc.getElementById('saveResult').textContent, /"tar\.gz" in Never catch these file types/);
  } finally { page.restore(); }
});

test('OK saves and closes; the Theme setting is stored and put on the page and in localStorage for theme.js', async () => {
  const { page, saves, doc } = await openOptions();
  try {
    doc.getElementById('theme').value = 'dark';
    doc.getElementById('theme').dispatchEvent(new page.window.Event('change', { bubbles: true }));
    doc.getElementById('ok').click();
    await flush();
    assert.deepEqual(saves[0].changes, { theme: 'dark' });
    assert.equal(doc.documentElement.dataset.theme, 'dark');
    assert.equal(page.window.localStorage.getItem('inbound.theme'), 'dark');
    assert.deepEqual(page.removedTabs, [7]);
  } finally { page.restore(); }
});

test('U-EX-13: Unpair asks first, and Cancel leaves the pairing alone', async () => {
  const { page, doc } = await openOptions({ server: { ...data.server } });
  try {
    doc.getElementById('unpairServer').click();
    await flush();
    const dialog = doc.querySelector('.dialog');
    assert.equal(dialog.querySelector('.dialog-header').textContent, 'Unpair the server?');
    dialog.querySelector('.btn').click();
    await flush();
    assert.equal(page.sent.some((message) => message.type === 'dm.unpairServer'), false);
    doc.getElementById('unpairServer').click();
    await flush();
    doc.querySelector('.dialog .btn-danger').click();
    await flush();
    assert.equal(page.sent.some((message) => message.type === 'dm.unpairServer'), true);
  } finally { page.restore(); }
});

test('U-EX-17: a save that is never answered says so instead of staying on "Saving…"', async () => {
  const { page, doc } = await openOptions({ save: () => new Promise(() => undefined) });
  try {
    // The 10 s wait, made short for the test.
    global.__inboundSaveTimeoutMs = 5;
    doc.getElementById('notifications').checked = false;
    doc.getElementById('notifications').dispatchEvent(new page.window.Event('change', { bubbles: true }));
    doc.getElementById('apply').click();
    await new Promise((resolve) => setTimeout(resolve, 30));
    await flush();
    assert.match(doc.getElementById('saveResult').textContent, /did not answer/);
    assert.equal(doc.getElementById('apply').disabled, false, 'Apply can be tried again');
  } finally { delete global.__inboundSaveTimeoutMs; page.restore(); }
});
