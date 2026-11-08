import { describe, expect, it } from 'vitest';
import { loginNeedsPassword } from '../src/lib/settingsRules';

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
