import { describe, expect, it } from 'vitest';
import { copyText } from '../src/lib/clipboard';
import { DialogStack, wrapTarget, type HistoryLike } from '../src/lib/dialogs';
import { fileInfoProblem } from '../src/lib/addflow';

describe('wrapTarget (Tab inside a dialog)', () => {
  const list = ['a', 'b', 'c'];
  it('wraps at both ends and leaves the middle to the browser', () => {
    expect(wrapTarget(list, 2, false)).toBe('a');
    expect(wrapTarget(list, 0, true)).toBe('c');
    expect(wrapTarget(list, 1, false)).toBeNull();
    expect(wrapTarget(list, 1, true)).toBeNull();
  });
  it('treats the panel itself (or anything not in the list) as outside: Shift+Tab goes to the last control', () => {
    // Review of D2d, defect 2: the panel had the focus because the first control was disabled.
    expect(wrapTarget(list, -1, true)).toBe('c');
    expect(wrapTarget(list, -1, false)).toBe('a');
  });
  it('has nowhere to go with no controls', () => {
    expect(wrapTarget([], -1, false)).toBeNull();
  });
});

class FakeHistory implements HistoryLike {
  entries: unknown[] = [null];
  index = 0;
  pendingBacks = 0;
  get state() { return this.entries[this.index]; }
  pushState(data: unknown) {
    this.entries = this.entries.slice(0, this.index + 1);
    this.entries.push(data);
    this.index++;
  }
  back() { this.pendingBacks++; }
  /** The browser carries out one queued Back and fires popstate. */
  land(stack: DialogStack) {
    this.pendingBacks--;
    this.index--;
    stack.popped();
  }
  /** The user presses Back. */
  userBack(stack: DialogStack) {
    this.index--;
    stack.popped();
  }
}

describe('DialogStack (Back closes the dialog, U-WU-7)', () => {
  it("Back closes the top dialog and leaves the page's own entry alone", () => {
    const history = new FakeHistory();
    const stack = new DialogStack(() => history);
    let closed = 0;
    const id = stack.opened(() => { closed++; stack.closed(id); });
    expect(history.entries).toHaveLength(2);
    history.userBack(stack);
    expect(closed).toBe(1);
    expect(history.index).toBe(0);
    expect(history.pendingBacks).toBe(0);
    expect(stack.count).toBe(0);
  });

  it('a dialog closed by its own button takes its entry back out, and that Back closes nothing else', () => {
    const history = new FakeHistory();
    const stack = new DialogStack(() => history);
    let outerClosed = 0;
    const outer = stack.opened(() => { outerClosed++; stack.closed(outer); });
    const inner = stack.opened(() => stack.closed(inner));
    stack.closed(inner);
    expect(history.pendingBacks).toBe(1);
    history.land(stack);
    expect(outerClosed).toBe(0);
    expect(stack.isTop(outer)).toBe(true);
    history.userBack(stack);
    expect(outerClosed).toBe(1);
  });

  it('a dialog that replaces another waits for that Back before adding its entry', () => {
    const history = new FakeHistory();
    const stack = new DialogStack(() => history);
    const first = stack.opened(() => stack.closed(first));
    stack.closed(first);
    const second = stack.opened(() => stack.closed(second));
    expect(history.entries.length).toBe(2); // not pushed yet
    history.land(stack);
    expect(history.index).toBe(1);
    expect(history.state).toEqual({ inboundDialog: second });
    stack.closed(second);
    expect(history.pendingBacks).toBe(1);
  });

  it('a dialog closed after the address changed (a link inside it) does not go back', () => {
    const history = new FakeHistory();
    const stack = new DialogStack(() => history);
    const id = stack.opened(() => stack.closed(id));
    history.pushState(null); // location.hash = '#/settings'
    stack.closed(id);
    expect(history.pendingBacks).toBe(0);
  });

  it('works with no history at all', () => {
    const stack = new DialogStack(() => null);
    const id = stack.opened(() => undefined);
    expect(stack.isTop(id)).toBe(true);
    stack.closed(id);
    expect(stack.count).toBe(0);
  });
});

describe('copyText (Copy over plain http, review of D2d defect 5)', () => {
  it('uses the Clipboard API where there is one', async () => {
    let written = '';
    expect(await copyText('a', { clipboard: { writeText: async (t) => { written = t; } }, legacyCopy: () => false })).toBe(true);
    expect(written).toBe('a');
  });
  it('falls back to the copy command when there is no Clipboard API or it refuses', async () => {
    let copied = '';
    expect(await copyText('b', { clipboard: null, legacyCopy: (t) => { copied = t; return true; } })).toBe(true);
    expect(copied).toBe('b');
    expect(await copyText('c', { clipboard: { writeText: async () => { throw new Error('no'); } }, legacyCopy: () => true })).toBe(true);
  });
  it('says false when neither works', async () => {
    expect(await copyText('d', { clipboard: null, legacyCopy: () => { throw new Error('x'); } })).toBe(false);
    expect(await copyText('d', {})).toBe(false);
  });
});

describe('fileInfoProblem (U-WU-16)', () => {
  it("turns .NET's status text into words", () => {
    expect(fileInfoProblem('Response status code does not indicate success: 404 (Not Found).')).toBe("The server says this file doesn't exist (404).");
    expect(fileInfoProblem('Response status code does not indicate success: 503 (Service Unavailable).')).toBe('The server had a problem answering (503).');
    expect(fileInfoProblem('Response status code does not indicate success: 418 (I am a teapot).')).toBe('The server answered 418.');
  });
  it('keeps any other message', () => {
    expect(fileInfoProblem('The host name could not be found.')).toBe('The host name could not be found.');
  });
});
