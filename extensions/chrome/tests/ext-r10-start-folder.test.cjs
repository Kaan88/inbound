// EXT.R10 (GitHub #3): where "Ask where to save" starts (Default folder, Last used folder, Custom folder), the fallback
// to the default for a folder that is gone, and a default folder for browser downloads on This PC.
const assert = require('node:assert/strict');
const test = require('node:test');
const {
  answerNative, capturingChrome, catching, makeChrome, ownPage, sendRuntimeMessage, settle, startDownload, withBackground,
} = require('./harness.cjs');
const { installPage, loadTs, tick } = require('./page-harness.cjs');
const { installDom, flush } = require('./dom-harness.cjs');
const data = require('./fixtures/screens-data.json');

const pending = { id: 'h1', url: 'https://site.test/a.iso', userAgent: 'test', isAutoIntercept: false, createdAt: Date.now() };
const folders = (extra = {}) => ({ ok: true, defaultFolder: 'D:\\Downloads', favorites: [], ...extra });

/** Asks for the folder window's details, answers the Host's folder request, and returns both. */
async function getHandoff(harness, answer = folders(), id = pending.id) {
  const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id }, ownPage('handoff.html'));
  const request = await answerNative(harness, 'settings.downloadFolders', answer);
  return { request, info: await info };
}

/** Sends the pending download from a form to a folder, with the Host's answer. */
async function confirm(harness, destination, accepted = true, id = pending.id) {
  const response = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id, destination, startMode: 'now', target: 'local' },
    ownPage('handoff.html'));
  const request = await answerNative(harness, 'handoff.request', { accepted });
  await response;
  return request;
}

const again = (harness, id) => { harness.session[`handoff.${id}`] = { ...pending, id }; };
const local = info => info.targets.find(target => target.id === 'local');

test('EXT.R10: by default every form starts on the default folder and a one-off folder is not kept (UI.R3)', async () => {
  const harness = makeChrome({ ...catching }, pending);
  await withBackground(harness, async () => {
    const first = await getHandoff(harness);
    assert.deepEqual(first.request.payload, {}, 'nothing to check: no folder names go to the Host');
    assert.equal(local(first.info).startFolder, undefined);
    assert.equal(local(first.info).sendDefault, undefined);
    assert.equal(first.info.defaults.destination, 'D:\\Downloads');

    assert.equal((await confirm(harness, 'E:\\Once')).payload.destinationDirectory, 'E:\\Once');
    assert.equal(harness.values.lastFolders, undefined, 'no folder is remembered');

    again(harness, 'h2');
    const next = await getHandoff(harness, folders(), 'h2');
    assert.equal(local(next.info).startFolder, undefined);
    assert.equal(next.info.defaults.destination, 'D:\\Downloads');
  });
});

test('EXT.R10: Last used folder starts on the folder last sent to, only when the Host says it still exists', async () => {
  const harness = makeChrome({ ...catching, askStartsOn: 'last' }, pending);
  await withBackground(harness, async () => {
    // A refused send is not "used".
    await confirm(harness, 'F:\\Refused', false);
    assert.equal(harness.values.lastFolders, undefined);
    await confirm(harness, 'E:\\Last');
    assert.deepEqual(harness.values.lastFolders, { local: 'E:\\Last' });

    again(harness, 'h2');
    const there = await getHandoff(harness, folders({ existingCandidates: ['E:\\Last'] }), 'h2');
    assert.deepEqual(there.request.payload, { candidates: ['E:\\Last'] });
    assert.equal(local(there.info).startFolder, 'E:\\Last');
    assert.equal(local(there.info).defaultFolder, 'D:\\Downloads', 'the default stays the default');

    // Gone: the default.
    const gone = await getHandoff(harness, folders({ existingCandidates: [] }), 'h2');
    assert.equal(local(gone.info).startFolder, undefined);
    // An app too old to answer: unknown, so the default.
    const old = await getHandoff(harness, folders(), 'h2');
    assert.equal(local(old.info).startFolder, undefined);

    // Sent to the default folder: the next one starts there again.
    await confirm(harness, '', true, 'h2');
    assert.deepEqual(harness.values.lastFolders, {});
  });
});

test('EXT.R10: Download All remembers its folder with Last used folder too', async () => {
  const harness = makeChrome({ ...catching, askStartsOn: 'last' }, { id: 'seed' });
  await withBackground(harness, async () => {
    const response = sendRuntimeMessage(harness, { type: 'dm.confirmBatch', pageUrl: 'https://site.test/', destination: 'E:\\Batch',
      startMode: 'now', target: 'local', urls: [{ url: 'https://site.test/a.zip' }] }, ownPage('batch.html'));
    await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal((await response).accepted, 1);
    assert.deepEqual(harness.values.lastFolders, { local: 'E:\\Batch' });
  });
});

test('EXT.R10: leaving Last used folder forgets the remembered folders', async () => {
  const harness = makeChrome({ ...catching, askStartsOn: 'last' }, { id: 'seed' });
  harness.values.lastFolders = { local: 'E:\\Last', server: '/downloads/tv' };
  await withBackground(harness, async () => {
    const saved = await sendRuntimeMessage(harness, { type: 'dm.saveSettings', changes: { askStartsOn: 'default' } });
    assert.equal(saved.settings.askStartsOn, 'default');
    assert.deepEqual(harness.values.lastFolders, {});
  });
});

test('EXT.R10: Custom folder starts there on This PC when it exists, and on the default when it does not', async () => {
  const harness = makeChrome({ ...catching, askStartsOn: 'custom', askStartFolder: 'E:\\Sorting' }, pending);
  await withBackground(harness, async () => {
    const there = await getHandoff(harness, folders({ existingCandidates: ['E:\\Sorting'] }));
    assert.deepEqual(there.request.payload, { candidates: ['E:\\Sorting'] });
    assert.equal(local(there.info).startFolder, 'E:\\Sorting');
    const gone = await getHandoff(harness, folders({ existingCandidates: [] }));
    assert.equal(local(gone.info).startFolder, undefined);
    // A send there is not remembered: Custom does not change.
    await confirm(harness, 'E:\\Elsewhere');
    assert.equal(harness.values.lastFolders, undefined);
  });
});

test('EXT.R10: the browser\'s default folder goes with automatic sends to This PC, and the forms send it as it is', async () => {
  const { harness } = capturingChrome({ ...catching, browserFolder: 'E:\\Browser' });
  harness.session[`handoff.${pending.id}`] = pending;
  await withBackground(harness, async () => {
    startDownload(harness, { id: 7, url: 'https://host.test/files/a.zip', filename: 'a.zip' });
    const request = await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal(request.payload.destinationDirectory, 'E:\\Browser');

    const { info } = await getHandoff(harness);
    assert.equal(local(info).defaultFolder, 'E:\\Browser', '"Default folder" for This PC is the browser\'s');
    assert.equal(local(info).sendDefault, true, 'the app does not know it, so it is sent as it is');
    assert.equal(info.defaults.destination, 'E:\\Browser');
  });
});

test('EXT.R10: without a browser folder, automatic sends leave the folder to the app as before', async () => {
  const { harness } = capturingChrome({ ...catching });
  await withBackground(harness, async () => {
    startDownload(harness, { id: 8, url: 'https://host.test/files/b.zip', filename: 'b.zip' });
    const request = await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal(request.payload.destinationDirectory, undefined);
  });
});

test('EXT.R10: Settings may open the app\'s folder dialog for its Choose… buttons', async () => {
  const harness = makeChrome({ ...catching }, { id: 'seed' });
  await withBackground(harness, async () => {
    const picked = sendRuntimeMessage(harness, { type: 'dm.pickFolder', initialPath: 'E:\\' }, ownPage('options.html'));
    await settle(() => harness.nativeMessages.some(message => message.type === 'host.pickFolder'));
    const request = harness.nativeMessages.find(message => message.type === 'host.pickFolder');
    assert.deepEqual(request.payload, { initialPath: 'E:\\' });
    for (const listener of harness.nativeOnMessage.listeners) {
      listener({ type: 'host.folderPicked', replyTo: request.id, payload: { ok: true, path: 'E:\\Chosen' } });
    }
    assert.equal((await picked).path, 'E:\\Chosen');
    // Still no web page.
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.pickFolder' }, { url: 'https://evil.test/', tab: { id: 4 } })).ok, false);
  });
});

// ---- The server ---------------------------------------------------------------------------------------------------

const ADDRESS = 'http://192.168.1.20:8080';
const TOKEN = 'ibt_' + 'c'.repeat(43);

function fakeServer(browse) {
  const browsed = [];
  const fetch = async (url, init) => {
    const body = JSON.parse(init.body);
    const reply = { 'settings.downloadFolders': ['settings.downloadFoldersResult', { ok: true, defaultFolder: '/downloads', favorites: [] }],
      'folders.browse': ['folders.browseResult', null] }[body.type];
    let payload = reply?.[1];
    if (body.type === 'folders.browse') { browsed.push(body.payload.path); payload = browse(body.payload.path); }
    return { status: 200, async text() { return JSON.stringify({ v: 1, type: reply?.[0] ?? 'x', replyTo: body.id, payload }); } };
  };
  return { fetch, browsed };
}

for (const [name, browse, expected] of [
  ['lists it', () => ({ ok: true, path: '/downloads/tv', folders: [] }), '/downloads/tv'],
  ['does not list it (gone, or outside its folders)', () => ({ ok: false, error: 'That folder does not exist.' }), undefined],
]) {
  test(`EXT.R10: Last used folder on the server starts there when the server ${name}`, async () => {
    const harness = makeChrome({ ...catching, askStartsOn: 'last' }, pending);
    harness.values.server = { address: ADDRESS, token: TOKEN, id: 'p1', name: 'Chrome', pairedAt: 1 };
    harness.values.lastFolders = { server: '/downloads/tv' };
    const server = fakeServer(browse);
    const oldFetch = global.fetch;
    global.fetch = server.fetch;
    try {
      await withBackground(harness, async () => {
        const { request, info } = await getHandoff(harness);
        assert.deepEqual(request.payload, {}, 'no folder of the server goes to this PC');
        assert.deepEqual(server.browsed, ['/downloads/tv']);
        const target = info.targets.find(each => each.id === 'server');
        assert.equal(target.defaultFolder, '/downloads');
        assert.equal(target.startFolder, expected);
        assert.equal(target.sendDefault, undefined, 'the server keeps its own default');
      });
    } finally { global.fetch = oldFetch; }
  });
}

// ---- The forms ----------------------------------------------------------------------------------------------------

function panelPage(targets) {
  let confirmed;
  const page = installPage('', {
    'dm.askQueue': async () => ({ ok: true, items: confirmed ? [] : [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso' }] }),
    'dm.getHandoff': async () => ({ ok: true, held: false, handoff: { id: 'h1', url: 'https://site.test/a.iso' },
      defaults: { destination: targets[0].defaultFolder, startMode: 'now' }, targets, target: 'local' }),
    'dm.confirmHandoff': async message => { confirmed = message; return { ok: true, ack: { accepted: true } }; },
  });
  const { AskPanel } = loadTs('ask-panel.ts');
  return { page, panel: new AskPanel(() => undefined), sent: () => confirmed };
}

const localTarget = extra => [{ id: 'local', label: 'This PC', chooser: 'dialog', defaultFolder: 'D:\\Downloads', favorites: [], ...extra }];

test('EXT.R10 popup: the form starts on the start folder and sends it; the default untouched is still left to the app', async () => {
  for (const [extra, shown, sent] of [
    [{}, 'D:\\Downloads', ''],
    [{ startFolder: 'E:\\Last' }, 'E:\\Last', 'E:\\Last'],
  ]) {
    const { page, panel, sent: confirmed } = panelPage(localTarget(extra));
    try {
      await panel.refresh();
      assert.equal(page.el('askDestination').value, shown);
      await page.el('askConfirm').click();
      assert.equal(confirmed().destination, sent);
    } finally { page.restore(); }
  }
});

test('EXT.R10 popup: the browser\'s default folder is sent as it is, and an emptied field still means the app\'s default', async () => {
  for (const [typed, sent] of [[undefined, 'E:\\Browser'], ['', '']]) {
    const { page, panel, sent: confirmed } = panelPage(localTarget({ defaultFolder: 'E:\\Browser', sendDefault: true }));
    try {
      await panel.refresh();
      assert.equal(page.el('askDestination').value, 'E:\\Browser');
      assert.equal(page.el('askDestination').placeholder, 'The app\'s default folder');
      if (typed !== undefined) page.el('askDestination').value = typed;
      await page.el('askConfirm').click();
      assert.equal(confirmed().destination, sent);
    } finally { page.restore(); }
  }
});

// ---- Settings -----------------------------------------------------------------------------------------------------

async function openOptions(settings, handlers = {}) {
  const saves = [];
  const page = installDom('options.html', { handlers: {
    'dm.status': async () => ({ ok: true, settings, connected: true, server: null, extensionVersion: '0.6.0' }),
    'dm.saveSettings': async (message) => { saves.push(message); return { ok: true, settings: { ...settings, ...message.changes } }; },
    ...handlers,
  } });
  page.load('options.ts');
  await page.ready();
  await flush(10);
  return { page, saves, doc: page.document };
}

const choose = (page, doc, name, value) => {
  const radio = doc.querySelector(`input[name=${name}][value=${value}]`);
  radio.checked = true;
  radio.dispatchEvent(new page.window.Event('change', { bubbles: true }));
};

test('EXT.R10 Settings: Default folder is chosen at first; the folder fields wait for their choice', async () => {
  const { page, doc } = await openOptions({ ...data.settings });
  try {
    assert.equal(doc.querySelector('input[name=askStartsOn]:checked').value, 'default');
    assert.equal(doc.querySelector('input[name=browserFolderMode]:checked').value, 'app');
    assert.equal(doc.getElementById('browserFolder').disabled, true);
    assert.equal(doc.getElementById('askStartFolder').disabled, true);
    choose(page, doc, 'askStartsOn', 'custom');
    assert.equal(doc.getElementById('askStartFolder').disabled, false);
    assert.equal(doc.getElementById('chooseAskStartFolder').disabled, false);
  } finally { page.restore(); }
});

test('EXT.R10 Settings: This folder with Choose…, and Last used folder, are saved', async () => {
  const { page, saves, doc } = await openOptions({ ...data.settings }, {
    'dm.pickFolder': async () => ({ ok: true, path: 'E:\\Browser' }),
  });
  try {
    choose(page, doc, 'browserFolderMode', 'own');
    doc.getElementById('chooseBrowserFolder').click();
    await flush();
    assert.equal(doc.getElementById('browserFolder').value, 'E:\\Browser');
    choose(page, doc, 'askStartsOn', 'last');
    doc.getElementById('apply').click();
    await flush();
    assert.deepEqual(saves[0].changes, { browserFolder: 'E:\\Browser', askStartsOn: 'last' });
  } finally { page.restore(); }
});

test('EXT.R10 Settings: a missing or relative folder blocks Apply with the reason', async () => {
  for (const [setup, message] of [
    [(page, doc) => choose(page, doc, 'askStartsOn', 'custom'), /Choose the folder Ask where to save starts on/],
    [(page, doc) => { choose(page, doc, 'browserFolderMode', 'own'); const field = doc.getElementById('browserFolder');
      field.value = 'Downloads';
      field.dispatchEvent(new page.window.Event('input', { bubbles: true })); },
      /"Downloads" is not a full path/],
    [(page, doc) => choose(page, doc, 'browserFolderMode', 'own'), /Choose a folder for browser downloads/],
  ]) {
    const { page, saves, doc } = await openOptions({ ...data.settings });
    try {
      setup(page, doc);
      // OK checks too, also when nothing that would be saved has changed yet ("This folder" with no folder).
      doc.getElementById('ok').click();
      await flush();
      assert.equal(saves.length, 0);
      assert.match(doc.getElementById('saveResult').textContent, message);
    } finally { page.restore(); }
  }
});

test('EXT.R10 Settings: the stored folder settings are normalized; unknown start choices are the default', () => {
  const { migrateSettings } = loadTs('capture-rules.ts');
  const { settings } = migrateSettings({ rulesVersion: 2, askStartsOn: 'sometimes', browserFolder: '  E:\\Browser  ', askStartFolder: 7 });
  assert.equal(settings.askStartsOn, 'default');
  assert.equal(settings.browserFolder, 'E:\\Browser');
  assert.equal(settings.askStartFolder, '');
  assert.equal(migrateSettings(undefined).settings.askStartsOn, 'default');
});

test('EXT.R10 Settings: a path from the current drive or a device path is refused on Windows; / paths elsewhere', async () => {
  const page = installDom('options.html');
  const { isFullPath, invalidFolder } = page.load('options.ts');
  await page.ready();
  await flush(10);
  page.restore();
  for (const path of ['D:\\Downloads', 'd:/Downloads', '\\\\pc\\share\\in']) assert.equal(isFullPath(path), true, path);
  for (const path of ['/downloads', '\\Downloads', 'Downloads', 'D:Downloads', '\\\\?\\C:\\x', '\\\\.\\C:\\x', '\\\\\\x']) {
    assert.equal(isFullPath(path), false, path);
  }
  assert.equal(isFullPath('/home/you/Downloads', false), true);
  assert.equal(isFullPath('D:\\Downloads', false), false);
  assert.match(invalidFolder({ browserFolder: '/downloads', askStartsOn: 'default', askStartFolder: '' }, true),
    /"\/downloads" is not a full path\. Use one such as D:\\Downloads/);
  assert.match(invalidFolder({ browserFolder: '', askStartsOn: 'custom', askStartFolder: '\\\\?\\C:\\x' }, false),
    /is not a full path/);
  assert.match(invalidFolder({ browserFolder: 'D:\\x', askStartsOn: 'default', askStartFolder: '' }, true, false),
    /Use one such as \/home\/you\/Downloads/);
  assert.equal(invalidFolder({ browserFolder: '/srv/in', askStartsOn: 'custom', askStartFolder: '/srv/sort' }, true, false), null);
});

test('EXT.R10 Settings: in a browser on Linux a folder from / is saved and a Windows path is refused', async () => {
  for (const [folder, saved] of [['/srv/in', true], ['D:\\Downloads', false]]) {
    const saves = [];
    const page = installDom('options.html', { handlers: {
      'dm.status': async () => ({ ok: true, settings: { ...data.settings }, connected: true, server: null, extensionVersion: '0.6.0' }),
      'dm.saveSettings': async (message) => { saves.push(message); return { ok: true, settings: { ...data.settings, ...message.changes } }; },
    } });
    page.window.chrome.runtime.getPlatformInfo = async () => ({ os: 'linux', arch: 'x86-64' });
    try {
      page.load('options.ts');
      await page.ready();
      await flush(10);
      const doc = page.document;
      choose(page, doc, 'browserFolderMode', 'own');
      const field = doc.getElementById('browserFolder');
      field.value = folder;
      field.dispatchEvent(new page.window.Event('input', { bubbles: true }));
      doc.getElementById('apply').click();
      await flush();
      assert.equal(saves.length, saved ? 1 : 0, folder);
      if (!saved) assert.match(doc.getElementById('saveResult').textContent, /\/home\/you\/Downloads/);
    } finally { page.restore(); }
  }
});

// ---- Popup to folder window (review) ------------------------------------------------------------------------------

test('EXT.R10 popup: Choose... moves the download to the window with the folder as shown, an emptied field as ""', async () => {
  for (const [extra, typed, moved] of [
    [{ startFolder: 'E:\\Last' }, undefined, 'E:\\Last'],
    [{}, undefined, 'D:\\Downloads'],
    [{ startFolder: 'E:\\Last' }, '', ''],
  ]) {
    let message;
    const page = installPage('', {
      'dm.askQueue': async () => ({ ok: true, items: [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso' }] }),
      'dm.getHandoff': async () => ({ ok: true, held: false, handoff: { id: 'h1', url: 'https://site.test/a.iso' },
        defaults: { destination: 'D:\\Downloads', startMode: 'now' }, targets: localTarget(extra), target: 'local' }),
      'dm.pickFolder': async () => assert.fail('the popup never opens the dialog itself'),
      'dm.askInWindow': async sent => { message = sent; return { ok: true }; },
    });
    try {
      const { AskPanel } = loadTs('ask-panel.ts');
      const panel = new AskPanel(() => undefined);
      await panel.refresh();
      if (typed !== undefined) page.el('askDestination').value = typed;
      await page.el('askBrowse').click();
      assert.equal(message.draft.destination, moved);
      assert.equal(page.closed, true);
    } finally { page.restore(); }
  }
});

test('EXT.R10 folder window: a draft folder from the popup is shown and sent as it is; "" stays the app\'s default', async () => {
  for (const [destination, sent] of [['E:\\Last', 'E:\\Last'], ['', ''], ['D:\\Downloads', '']]) {
    let confirmed;
    const page = installPage('?id=h1', {
      'dm.getHandoff': async () => ({ ok: true, held: false, handoff: { id: 'h1', url: 'https://site.test/a.iso' },
        defaults: { destination: 'D:\\Downloads', startMode: 'now' }, targets: localTarget({ startFolder: 'F:\\Other' }), target: 'local',
        draft: { target: 'local', fileName: 'a.iso', destination } }),
      'dm.confirmHandoff': async message => { confirmed = message; return { ok: true, ack: { accepted: true } }; },
    });
    try {
      loadTs('handoff.ts');
      await page.domReady();
      await tick();
      assert.equal(page.el('destination').value, destination, 'the popup\'s folder, not the window\'s own start folder');
      await page.el('confirm').click();
      await tick();
      assert.equal(confirmed.destination, sent);
    } finally { page.restore(); }
  }
});
