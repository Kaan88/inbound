const assert = require('node:assert/strict');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const esbuild = require('esbuild');

function load(file) {
  const source = path.join(__dirname, '..', 'src', file);
  const bundled = esbuild.buildSync({
    entryPoints: [source], bundle: true, write: false, platform: 'node', format: 'cjs', target: 'node20',
  }).outputFiles[0].text;
  const loaded = new Module(source, module);
  loaded.filename = source;
  loaded.paths = Module._nodeModulePaths(path.dirname(source));
  loaded._compile(bundled, source);
  return loaded.exports;
}

const rules = load('capture-rules.ts');
const { DEFAULT_SETTINGS, LEGACY_INTERCEPTED_EXTENSIONS, RULES_VERSION, decideCapture, migrateSettings } = rules;
const on = { ...DEFAULT_SETTINGS, autoIntercept: true };
const decide = (fileName, extra = {}, settings = on) =>
  decideCapture({ url: `https://files.test/get?id=1`, fileName, mime: '', ...extra }, settings);

test('EXT.B1: the default rules catch the file types the old allow-list missed', () => {
  for (const name of ['track.flac', 'film.mp4', 'book.pdf', 'novel.epub', 'disk.dmg', 'data.parquet', 'song.opus', 'doc.docx']) {
    assert.deepEqual(decide(name), { capture: true }, name);
  }
  // And a name with no file type that is not a web page.
  assert.deepEqual(decide('download', { mime: 'application/octet-stream' }), { capture: true });
});

test('web pages and images stay in the browser by default, said in the popup only', () => {
  for (const name of ['page.html', 'photo.jpg', 'icon.png', 'saved.mhtml']) {
    const decision = decide(name);
    assert.equal(decision.capture, false, name);
    assert.equal(decision.quiet, true, name);
    assert.match(decision.reason, /never to catch/);
  }
  const page = decide('', { url: 'https://files.test/', mime: 'text/html; charset=utf-8' });
  assert.deepEqual(page, { capture: false, reason: 'It is a web page.', quiet: true });
});

test('a rule the user set is said out loud, with the reason', () => {
  const site = decide('a.zip', { url: 'https://cdn.example.test/a.zip' }, { ...on, excludedHosts: ['example.test'] });
  assert.equal(site.capture, false);
  assert.equal(site.quiet, false);
  assert.match(site.reason, /example\.test is on your list of sites/);

  const type = decide('a.iso', {}, { ...on, excludedExtensions: ['iso'] });
  assert.equal(type.quiet, false);
  assert.match(type.reason, /\.iso files are on your list/);

  const notListed = decide('a.flac', {}, { ...on, interceptedExtensions: ['zip'] });
  assert.equal(notListed.quiet, false);
  assert.match(notListed.reason, /\.flac is not on your list of file types to catch/);
});

test('blob: and data: downloads are left alone without a word', () => {
  for (const url of ['blob:https://page.test/1234', 'data:text/plain;base64,aGk=']) {
    assert.deepEqual(decideCapture({ url, fileName: 'x.zip' }, on), { capture: false, reason: null, quiet: true });
  }
});

test('a redirect to a downloadable file decides by the final address', () => {
  const decision = decideCapture({ url: 'https://site.test/dl?id=4', finalUrl: 'https://store.test/x/track.flac', fileName: 'track.flac' }, on);
  assert.deepEqual(decision, { capture: true });
});

test('a fresh install starts with the new rules and nothing to store', () => {
  const { settings, changed } = migrateSettings(undefined);
  assert.equal(changed, false);
  assert.deepEqual(settings.interceptedExtensions, []);
  assert.ok(settings.excludedExtensions.includes('html'));
  assert.equal(settings.autoIntercept, true);
  assert.equal(settings.destinationMode, 'default');
  assert.equal(settings.rulesVersion, RULES_VERSION);
});

test('a copy that never edited the old list moves to catch-everything, once', () => {
  const stored = { enabled: true, autoIntercept: true, startMode: 'later', interceptedExtensions: [...LEGACY_INTERCEPTED_EXTENSIONS].reverse(),
    excludedExtensions: [], excludedHosts: ['bank.test'], notifications: false };
  const first = migrateSettings(stored);
  assert.equal(first.changed, true);
  assert.deepEqual(first.settings.interceptedExtensions, []);
  assert.deepEqual(first.settings.excludedExtensions, [...DEFAULT_SETTINGS.excludedExtensions]);
  // Everything else is the user's and stays.
  assert.equal(first.settings.startMode, 'later');
  assert.deepEqual(first.settings.excludedHosts, ['bank.test']);
  assert.equal(first.settings.notifications, false);
  assert.equal(first.settings.autoIntercept, true);
  const again = migrateSettings(first.settings);
  assert.equal(again.changed, false);

  // Their own exclude list survives the move, and the new defaults join it: pages and images were
  // never caught under the old allow-list, and one exclude of their own must not change that.
  const withOwnExcludes = migrateSettings({ ...stored, excludedExtensions: ['iso', 'JPG'] });
  assert.deepEqual(withOwnExcludes.settings.excludedExtensions,
    ['iso', 'jpg', ...DEFAULT_SETTINGS.excludedExtensions.filter(item => item !== 'jpg')]);
  assert.deepEqual(withOwnExcludes.settings.interceptedExtensions, []);
  for (const fileName of ['photo.jpg', 'page.html', 'data.json', 'disk.iso']) {
    assert.equal(decideCapture({ url: `https://site.test/${fileName}`, fileName }, withOwnExcludes.settings).capture, false, fileName);
  }
  assert.equal(decideCapture({ url: 'https://site.test/a.zip', fileName: 'a.zip' }, withOwnExcludes.settings).capture, true);
});

test('an edited allow-list is the user\'s choice and stays', () => {
  const edited = migrateSettings({ ...DEFAULT_SETTINGS, rulesVersion: undefined, interceptedExtensions: ['zip', 'rar'], excludedExtensions: [] });
  assert.equal(edited.changed, true);
  assert.deepEqual(edited.settings.interceptedExtensions, ['zip', 'rar']);
  assert.deepEqual(edited.settings.excludedExtensions, []);
  assert.equal(edited.settings.rulesVersion, RULES_VERSION);

  // Someone on the new rules who emptied the exclude list is not given it back.
  const emptied = migrateSettings({ ...DEFAULT_SETTINGS, excludedExtensions: [] });
  assert.equal(emptied.changed, false);
  assert.deepEqual(emptied.settings.excludedExtensions, []);
});

test('an unknown destination choice falls back to the default folder', () => {
  assert.equal(migrateSettings({ ...DEFAULT_SETTINGS, destinationMode: 'server' }).settings.destinationMode, 'default');
  assert.equal(migrateSettings({ ...DEFAULT_SETTINGS, destinationMode: 'ask' }).settings.destinationMode, 'ask');
});

const jobs = load('job-list.ts');

test('EXT.R2: the popup shows the newest 20 downloads of every kind', () => {
  const list = Array.from({ length: 30 }, (_, index) => ({
    id: `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
    fileName: `f${index}`, state: index % 3 ? 'completed' : 'downloading', receivedBytes: 0,
    createdAt: new Date(Date.UTC(2026, 0, 1, 0, index)).toISOString(),
  }));
  // Out of order, as no list should be trusted to be.
  const shown = jobs.latestJobs([...list.filter((_, i) => i % 2), ...list.filter((_, i) => !(i % 2))]);
  assert.equal(shown.length, 20);
  assert.equal(shown[0].fileName, 'f29');
  assert.equal(shown[19].fileName, 'f10');
  assert.deepEqual(jobs.latestJobs([{ fileName: 'no id' }, { id: '' }]), []);
});

test('UI.B19: Delete asks about the file only for a finished download, and always removes partial data', () => {
  assert.equal(jobs.deleteAsksAboutFile({ state: 'completed' }), true);
  assert.equal(jobs.deleteFileFor({ state: 'completed' }, false), false);
  assert.equal(jobs.deleteFileFor({ state: 'completed' }, true), true);
  for (const state of ['downloading', 'paused', 'failed', 'cancelled', 'queued']) {
    assert.equal(jobs.deleteAsksAboutFile({ state }), false, state);
    assert.equal(jobs.deleteFileFor({ state }, false), true, state);
  }
});

test('each download offers only what the app says it allows, and always Delete', () => {
  assert.deepEqual(jobs.actionsFor({ state: 'downloading', canPause: true, canResume: false }), ['pause', 'remove']);
  assert.deepEqual(jobs.actionsFor({ state: 'paused', canPause: false, canResume: true }), ['resume', 'remove']);
  assert.deepEqual(jobs.actionsFor({ state: 'failed', canPause: false, canResume: true }), ['retry', 'remove']);
  assert.deepEqual(jobs.actionsFor({ state: 'completed', canPause: false, canResume: true }), ['remove']);
  assert.deepEqual(jobs.JOB_ACTION_MESSAGES, { start: 'job.resume', pause: 'job.pause', resume: 'job.resume', retry: 'job.retry', remove: 'job.remove' });
});

test('U-EX-8: a "Download later" item, or one waiting for its queue, can be started from the popup', () => {
  // The Host says a queued job can be paused (CanPause includes Queued); one waiting for the user gets Start instead.
  assert.deepEqual(jobs.actionsFor({ state: 'queued', startMode: 'later', canPause: true, canResume: false }), ['start', 'remove']);
  assert.deepEqual(jobs.actionsFor({ state: 'queued', startMode: 'queue', canPause: true, canResume: false }), ['start', 'remove']);
  // Queued to start now: it starts by itself, so it can be stopped.
  assert.deepEqual(jobs.actionsFor({ state: 'queued', startMode: 'startNow', canPause: true, canResume: false }), ['pause', 'remove']);
  assert.equal(jobs.groupLabel('server', 'home-server'), 'home-server (server)');
  assert.equal(jobs.groupLabel('local', 'home-server'), 'This PC');
});
