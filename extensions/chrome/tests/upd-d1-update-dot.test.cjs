// UPD.D1 addendum: a small dot on the toolbar icon while the app (or a paired server) says an update is waiting. The
// extension reads one boolean from the status its jobs answer already carries; no polling, and no version or dismiss state.
const assert = require('node:assert/strict');
const test = require('node:test');
const { loadTs } = require('./page-harness.cjs');
const { sendRuntimeMessage, answerNative, settle, withBackground, capturingChrome, catching, ownPage } = require('./harness.cjs');

const status = loadTs('toolbar-status.ts');

test('UPD.D1: the dot shows only when the badge has nothing else to say', () => {
  assert.equal(status.showsUpdateDot('', true), true);
  assert.equal(status.showsUpdateDot('', false), false);
  for (const other of ['OFF', '!', '3', '12']) assert.equal(status.showsUpdateDot(other, true), false, `${other} wins`);
});

test('UPD.D1: only a literal true from a Host says an update waits; an older Host or a bad value says nothing', () => {
  assert.equal(status.readUpdateWaiting({ status: { updateWaiting: true } }), true);
  assert.equal(status.readUpdateWaiting({ status: { updateWaiting: false } }), false);
  assert.equal(status.readUpdateWaiting({ status: { updateWaiting: 'true' } }), false);
  assert.equal(status.readUpdateWaiting({ status: {} }), false);
  assert.equal(status.readUpdateWaiting({ jobs: [] }), null, 'no status: the last answer stays');
  assert.equal(status.readUpdateWaiting(null), null);
});

async function popupJobs(harness, snapshot) {
  const reply = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
  await answerNative(harness, 'jobs.query', snapshot);
  return reply;
}

test('UPD.D1: the badge shows the dot from a jobs answer, clears with the next one, and survives a worker restart', async () => {
  const { harness, log } = capturingChrome(catching);
  await withBackground(harness, async () => {
    await popupJobs(harness, { jobs: [], status: { updateWaiting: true } });
    await settle(() => log.badge.at(-1) === status.UPDATE_DOT);
    assert.equal(log.badge.at(-1), status.UPDATE_DOT);
    assert.deepEqual(harness.session.updateWaiting, { local: true, server: false }, 'kept for a worker restart');

    // An answer with no status keeps what it was; one that says false clears it.
    await popupJobs(harness, { jobs: [] });
    assert.equal(log.badge.at(-1), status.UPDATE_DOT);
    await popupJobs(harness, { jobs: [], status: { updateWaiting: false } });
    await settle(() => log.badge.at(-1) === '');
    assert.equal(log.badge.at(-1), '');
  });
});
