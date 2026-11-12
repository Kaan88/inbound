import { describe, expect, it } from 'vitest';
import { captureLabel, heldLeave, loginNeedsPassword } from '../src/lib/settingsRules';

describe('loginNeedsPassword', () => {
  const saved = { site: 'Example.com', username: 'ann', hasPassword: true };

  it('asks again when the site or user name changed and no password was typed', () => {
    expect(loginNeedsPassword(saved, 'other.com', 'ann', '')).toBe(true);
    expect(loginNeedsPassword(saved, 'example.com', 'bob', undefined)).toBe(true);
  });

  it('keeps the saved password when nothing that it belongs to changed (site case and spaces do not count)', () => {
    expect(loginNeedsPassword(saved, ' example.COM ', 'ann', '')).toBe(false);
    expect(loginNeedsPassword(saved, 'example.com', 'Ann', '')).toBe(true);
  });

  it('accepts a change when a password is typed, and for a new login or one without a password', () => {
    expect(loginNeedsPassword(saved, 'other.com', 'bob', 'secret')).toBe(false);
    expect(loginNeedsPassword(undefined, 'a', 'b', '')).toBe(false);
    expect(loginNeedsPassword({ ...saved, hasPassword: false }, 'other.com', 'bob', '')).toBe(false);
  });
});

describe('heldLeave', () => {
  it('holds a move to another screen while the Options draft has changes', () => {
    expect(heldLeave('http://x/#/queues', true, false)).toBe('#/queues');
    expect(heldLeave('http://x/', true, false)).toBe('#/all');
  });

  it('lets everything go when nothing changed, the question is already open, or the address is still Options', () => {
    expect(heldLeave('http://x/#/queues', false, false)).toBeNull();
    expect(heldLeave('http://x/#/queues', true, true)).toBeNull();
    expect(heldLeave('http://x/#/settings', true, false)).toBeNull();
    expect(heldLeave('not an address', true, false)).toBeNull();
  });
});

describe('captureLabel (review 5:H4)', () => {
  it('names the box for paired browsers on a server, and as the desktop does on the Windows app', () => {
    expect(captureLabel('windows')).toBe('Accept downloads sent from your browser');
    expect(captureLabel('linux')).toBe('Accept downloads sent from your browser');
    expect(captureLabel('server')).toBe('Accept downloads from paired browsers');
    expect(captureLabel(undefined)).toBe('Accept downloads from paired browsers');
  });
});
