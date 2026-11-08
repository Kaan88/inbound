const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');

const source = path.join(__dirname, '..', 'src', 'fit-window.ts');
const loaded = new Module(source, module);
loaded.filename = source;
loaded.paths = Module._nodeModulePaths(path.dirname(source));
loaded._compile(ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText, source);
const { fitWindowToContent } = loaded.exports;

async function fit({ window, screen, contentHeight, frame = 40 }) {
  const saved = { chrome: global.chrome, screen: global.screen, document: global.document, outerHeight: global.outerHeight, innerHeight: global.innerHeight };
  let update;
  global.chrome = { windows: { async getCurrent() { return window; }, async update(id, value) { update = { id, ...value }; } } };
  global.screen = screen;
  global.document = { documentElement: { scrollHeight: contentHeight } };
  global.outerHeight = 600;
  global.innerHeight = 600 - frame;
  try {
    await fitWindowToContent();
    return update;
  } finally {
    Object.assign(global, saved);
  }
}

test('UI.R4: a window fits its content when the screen has room', async () => {
  const update = await fit({
    window: { id: 3, type: 'popup', width: 560, left: 100, top: 50 },
    screen: { availWidth: 1920, availHeight: 1040, availLeft: 0, availTop: 0 },
    contentHeight: 500,
  });
  assert.deepEqual(update, { id: 3, width: 560, height: 548, left: 100, top: 50 });
});

test('UI.R4: on a small or scaled screen the window stays inside it, and the page scrolls', async () => {
  // 1366x768 at 150%: about 910x485 CSS pixels available.
  const update = await fit({
    window: { id: 4, type: 'popup', width: 560, left: 700, top: 300 },
    screen: { availWidth: 910, availHeight: 485, availLeft: 0, availTop: 0 },
    contentHeight: 900,
  });
  assert.equal(update.height, 485);
  assert.equal(update.top, 0);
  assert.equal(update.left, 350, 'moved left so the right edge is on screen');
  assert.ok(update.left + update.width <= 910);
});

test('UI.R4: a second monitor to the left keeps its own origin', async () => {
  const update = await fit({
    window: { id: 5, type: 'popup', width: 560, left: -2000, top: 10 },
    screen: { availWidth: 1280, availHeight: 1000, availLeft: -1280, availTop: 0 },
    contentHeight: 400,
  });
  assert.equal(update.left, -1280);
});

test('only extension popups are resized, never a tab', async () => {
  assert.equal(await fit({
    window: { id: 6, type: 'normal', width: 1200, left: 0, top: 0 },
    screen: { availWidth: 1920, availHeight: 1040 },
    contentHeight: 400,
  }), undefined);
});
