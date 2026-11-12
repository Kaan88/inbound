// EXT.R7 and EXT.B7 on the page side: the toolbar popup's ask form (ask-panel.ts), and the folder window's
// folders and free space, which must come from the place the download goes to.
const assert = require('node:assert/strict');
const test = require('node:test');
const { deferred, installPage, loadTs, tick } = require('./page-harness.cjs');

const BOTH = [
  { id: 'local', label: 'This PC', chooser: 'dialog', defaultFolder: 'D:\\Downloads', favorites: [{ name: 'Games', path: 'D:\\Games' }] },
  { id: 'server', label: 'Server', chooser: 'list', address: 'http://192.168.1.20:8080', defaultFolder: '/downloads',
    favorites: [{ name: 'Movies', path: '/downloads/movies' }] },
];

// held: false, so the folder window keeps no port (and no ping timer) open in these tests.
const handoff = (target = 'server', targets = BOTH) => async () => ({ ok: true, held: false, handoff: { id: 'h1', url: 'https://site.test/a.iso' },
  defaults: { destination: targets.find(t => t.id === target).defaultFolder, startMode: 'now' }, targets, target });

const serverListing = async message => ({ ok: true, path: message.path || null, parent: null, roots: ['/downloads'],
  folders: message.path ? [{ name: 'movies', path: '/downloads/movies' }] : [], freeBytes: 5 * 1024 ** 3 });

const radios = page => page.el('askTargetChoice').children.flatMap(label => label.children).filter(node => node.type === 'radio');
const favorites = page => page.el('askFavorites').children.map(option => option.value);
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

function panelPage(handlers) {
  const waiting = [];
  const page = installPage('', handlers);
  const { AskPanel } = loadTs('ask-panel.ts');
  const panel = new AskPanel(value => waiting.push(value));
  return { page, panel, waiting };
}

test('EXT.B7 popup: in server mode the ask form shows the server\'s folder, favorites and free space, and Choose... lists the server\'s folders', async () => {
  const browsed = [];
  let confirmed;
  const { page, panel, waiting } = panelPage({
    'dm.askQueue': async () => ({ ok: true, items: confirmed ? [] : [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso', totalBytes: 3 * 1024 ** 2 }] }),
    'dm.getHandoff': handoff('server'),
    'dm.pickFolder': async () => assert.fail('this PC\'s dialog must not open for the server'),
    'dm.browseServerFolders': async message => { browsed.push(message.path); return serverListing(message); },
    'dm.confirmHandoff': async message => { confirmed = message; return { ok: true, ack: { accepted: true } }; },
  });
  try {
    await panel.refresh();
    assert.deepEqual(waiting, [true]);
    assert.equal(page.el('askCard').classList.contains('hidden'), false);
    assert.equal(page.el('askFileName').value, 'a.iso');
    assert.equal(page.el('askSource').textContent, '3.00 MB · site.test', 'size first, then the site, in the web UI\'s numbers (EXT.R8)');
    assert.equal(page.el('askDestination').value, '/downloads');
    assert.equal(page.el('askDestination').placeholder, 'The server\'s default folder');
    assert.deepEqual(favorites(page), ['', '/downloads/movies'], 'the server\'s favorites, not this PC\'s');
    assert.equal(page.el('askServerHint').classList.contains('hidden'), false);
    assert.equal(page.el('askLocalHint').classList.contains('hidden'), true);
    assert.deepEqual(radios(page).map(radio => `${radio.value}${radio.checked ? '*' : ''}`), ['local', 'server*']);
    await wait(300);
    assert.equal(page.el('askFreeSpace').textContent, '5.00 GB free on the server');
    assert.equal(browsed.at(-1), '/downloads');

    await page.el('askBrowse').click();
    await tick();
    assert.equal(page.el('askServerFolders').classList.contains('hidden'), false);
    assert.ok(page.sent.includes('dm.browseServerFolders'));

    await page.el('askConfirm').click();
    assert.equal(confirmed.target, 'server');
    assert.equal(confirmed.destination, '', 'an untouched default is left to the server');
    assert.equal(confirmed.id, 'h1');
    // Sent: nothing waits any more, so the popup goes back to its list.
    assert.deepEqual(waiting, [true, false]);
    assert.equal(page.el('askCard').classList.contains('hidden'), true);
  } finally {
    page.restore();
  }
});

test('EXT.B7 popup: switching target shows that place\'s folder and favorites; This PC shows no server free space', async () => {
  let confirmed;
  const { page, panel } = panelPage({
    'dm.askQueue': async () => ({ ok: true, items: [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso' }] }),
    'dm.getHandoff': handoff('server'),
    'dm.browseServerFolders': serverListing,
    'dm.confirmHandoff': async message => { confirmed = message; return { ok: true, ack: { accepted: false, error: 'No.' } }; },
  });
  try {
    await panel.refresh();
    const local = radios(page).find(radio => radio.value === 'local');
    local.checked = true;
    await local.dispatch('change');
    assert.equal(page.el('askDestination').value, 'D:\\Downloads');
    assert.deepEqual(favorites(page), ['', 'D:\\Games']);
    await wait(300);
    assert.equal(page.el('askFreeSpace').textContent, '');
    assert.equal(page.el('askLocalHint').classList.contains('hidden'), false);

    page.el('askDestination').value = 'E:\\Typed';
    await page.el('askConfirm').click();
    assert.equal(confirmed.target, 'local');
    assert.equal(confirmed.destination, 'E:\\Typed');
    // A refusal stays on screen with the download, so it can be corrected.
    assert.equal(page.el('askError').textContent, 'No.');
    assert.equal(page.el('askCard').classList.contains('hidden'), false);
  } finally {
    page.restore();
  }
});

test('EXT.R7 popup: This PC\'s Choose... hands the download to a window with its dialog, since the dialog would close the popup', async () => {
  let moved;
  const { page, panel } = panelPage({
    'dm.askQueue': async () => ({ ok: true, items: [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso' }] }),
    'dm.getHandoff': handoff('local'),
    'dm.pickFolder': async () => assert.fail('the popup never opens the dialog itself'),
    'dm.askInWindow': async message => { moved = message; return { ok: true }; },
  });
  try {
    await panel.refresh();
    await page.el('askBrowse').click();
    assert.deepEqual({ id: moved.id, pick: moved.pick }, { id: 'h1', pick: true });
    assert.equal(page.closed, true);
  } finally {
    page.restore();
  }
});

test('EXT.R7 popup (review 1): Choose... for This PC, switched from the server, sends the popup\'s target, name, folder and start mode', async () => {
  let moved;
  const { page, panel } = panelPage({
    'dm.askQueue': async () => ({ ok: true, items: [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso' }] }),
    'dm.getHandoff': handoff('server'),
    'dm.browseServerFolders': serverListing,
    'dm.askInWindow': async message => { moved = message; return { ok: true }; },
  });
  try {
    await panel.refresh();
    const local = radios(page).find(radio => radio.value === 'local');
    local.checked = true;
    await local.dispatch('change');
    page.el('askFileName').value = 'renamed.iso';
    page.el('askDestination').value = 'D:\\Games';
    await page.el('askBrowse').click();
    // Decision 1 (owner, 2026-11-09): no Start choice is carried; the window's buttons follow Options.
    assert.deepEqual(moved.draft, { target: 'local', fileName: 'renamed.iso', destination: 'D:\\Games' });
    assert.equal(page.closed, true);
  } finally {
    page.restore();
  }
});

test('EXT.R7 popup: Download and Choose... wait for the request; a queue shows its count and the next one after Keep in browser', async () => {
  const request = deferred();
  let items = [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso' }, { id: 'h2', fileName: 'b.iso', url: 'https://site.test/b.iso' }];
  const kept = [];
  const { page, panel, waiting } = panelPage({
    'dm.askQueue': async () => ({ ok: true, items }),
    'dm.getHandoff': message => message.id === 'h1' ? request.promise : handoff('local')(),
    'dm.keepInBrowser': async message => { kept.push(message.id); items = items.filter(item => item.id !== message.id); return { ok: true }; },
    'dm.confirmHandoff': async () => assert.fail('nothing is sent before the request has loaded'),
  });
  try {
    const loading = panel.refresh();
    await tick();
    assert.equal(page.el('askConfirm').disabled, true);
    assert.equal(page.el('askBrowse').disabled, true);
    assert.equal(await page.el('askConfirm').click(), 'ignored');
    assert.equal(page.el('askCount').textContent, '1 of 2');
    assert.equal(page.el('askOthers').textContent, 'Also waiting: b.iso');
    request.resolve(await handoff('local')());
    await loading;
    assert.equal(page.el('askConfirm').disabled, false);

    await page.el('askKeep').click();
    assert.deepEqual(kept, ['h1']);
    assert.equal(page.el('askFileName').value, 'b.iso');
    assert.equal(page.el('askCount').textContent, '');
    assert.equal(page.el('askOthers').classList.contains('hidden'), true);
    assert.deepEqual(waiting, [true]);
  } finally {
    page.restore();
  }
});

test('EXT.R7 popup: an expired request leaves only Keep in browser', async () => {
  const { page, panel } = panelPage({
    'dm.askQueue': async () => ({ ok: true, items: [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso' }] }),
    'dm.getHandoff': async () => ({ ok: false, error: 'This download request has expired.' }),
  });
  try {
    await panel.refresh();
    assert.equal(page.el('askConfirm').disabled, true);
    assert.equal(page.el('askBrowse').disabled, true);
    assert.equal(page.el('askKeep').disabled, false);
    assert.equal(page.el('askError').textContent, 'This download request has expired.');
  } finally {
    page.restore();
  }
});

test('EXT.B7 folder window: the server\'s free space is shown for the server\'s folder, and nothing for This PC', async () => {
  const page = installPage('?id=h1', { 'dm.getHandoff': handoff('server'), 'dm.browseServerFolders': serverListing });
  try {
    loadTs('handoff.ts');
    await page.domReady();
    assert.equal(page.el('destination').value, '/downloads');
    await wait(300);
    assert.equal(page.el('freeSpace').textContent, '5.00 GB free on the server');
    const local = page.el('targetChoice').children.flatMap(label => label.children).find(node => node.value === 'local');
    local.checked = true;
    await local.dispatch('change');
    await wait(300);
    assert.equal(page.el('destination').value, 'D:\\Downloads');
    assert.equal(page.el('freeSpace').textContent, '');
  } finally {
    page.restore();
  }
});

test('EXT.R7 folder window: opened from the popup\'s Choose... (pick=1) it opens the app\'s folder dialog at once', async () => {
  let picked;
  const page = installPage('?id=h1&pick=1', {
    'dm.getHandoff': handoff('local'),
    'dm.pickFolder': async message => { picked = message; return { ok: true, path: 'E:\\Chosen' }; },
  });
  try {
    loadTs('handoff.ts');
    await page.domReady();
    await tick();
    assert.equal(picked.initialPath, 'D:\\Downloads');
    assert.equal(page.el('destination').value, 'E:\\Chosen');
  } finally {
    page.restore();
  }
});

test('EXT.R7 folder window (review 1): opened from the popup\'s Choose..., it starts from the popup\'s target, name and start mode, and the dialog opens', async () => {
  let picked;
  const page = installPage('?id=h1&pick=1', {
    // The default target is the server; the user switched to This PC in the popup.
    'dm.getHandoff': async () => ({ ...await handoff('server')(), draft: { target: 'local', fileName: 'renamed.iso', startMode: 'later' } }),
    'dm.browseServerFolders': serverListing,
    'dm.pickFolder': async message => { picked = message; return { ok: true, path: 'E:\\Chosen' }; },
  });
  try {
    loadTs('handoff.ts');
    await page.domReady();
    await tick();
    const checked = page.el('targetChoice').children.flatMap(label => label.children).find(node => node.type === 'radio' && node.checked);
    assert.equal(checked.value, 'local');
    assert.equal(page.el('fileName').value, 'renamed.iso');
    // Options > Default start ('now' here) picks the primary button, whatever an older draft says.
    assert.equal(page.el('confirm').className, 'btn-primary');
    assert.equal(page.el('later').className, 'btn');
    assert.equal(picked.initialPath, 'D:\\Downloads');
    assert.equal(page.el('destination').value, 'E:\\Chosen');
  } finally {
    page.restore();
  }
});

test('folder window: without pick=1 no dialog opens by itself', async () => {
  const page = installPage('?id=h1', { 'dm.getHandoff': handoff('local'), 'dm.pickFolder': async () => assert.fail('no dialog by itself') });
  try {
    loadTs('handoff.ts');
    await page.domReady();
    await tick();
  } finally {
    page.restore();
  }
});

test('Audit report 1 contracts: a refusal\'s message is shown only when the ack is a refusal (ok: false), through refusalText', async () => {
  let ack = { accepted: false, ok: false, message: 'The bridge refused it.' };
  const { page, panel } = panelPage({
    'dm.askQueue': async () => ({ ok: true, items: [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso' }] }),
    'dm.getHandoff': handoff('local'),
    'dm.confirmHandoff': async () => ({ ok: true, ack }),
  });
  try {
    await panel.refresh();
    await page.el('askConfirm').click();
    assert.equal(page.el('askError').textContent, 'The bridge refused it.');
    // An ack's own text that is not a refusal is not shown as the reason.
    ack = { accepted: false, message: 'Queued.' };
    await page.el('askConfirm').click();
    assert.equal(page.el('askError').textContent, 'Inbound Download Manager rejected this request.');
  } finally {
    page.restore();
  }
});
