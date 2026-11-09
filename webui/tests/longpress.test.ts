import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LongPress } from '../src/lib/longpress';

describe('row long press (review of D2d, defect 6 follow-up)', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function fired() {
    const press = new LongPress();
    const fire = vi.fn();
    press.down('touch', 10, 10, fire);
    vi.advanceTimersByTime(LongPress.holdMs);
    expect(fire).toHaveBeenCalledOnce();
    return press;
  }

  it('swallows the click that ends the press, and only that one', () => {
    const press = fired();
    vi.advanceTimersByTime(5000); // a long hold: the swallow does not run out while the finger is down
    press.up();
    expect(press.takeClick()).toBe(true);
    expect(press.takeClick()).toBe(false);
  });

  it('a mouse press after a long press whose click never came is not swallowed', () => {
    const press = fired();
    press.up();
    // No click event arrived. The next press is a mouse one on a touch laptop.
    press.down('mouse', 50, 50, vi.fn());
    expect(press.takeClick()).toBe(false);
  });

  it('a click long after the finger lifted is not swallowed', () => {
    const press = fired();
    press.up();
    vi.advanceTimersByTime(LongPress.clickWindowMs + 1);
    expect(press.takeClick()).toBe(false);
  });

  it('a cancelled touch swallows nothing', () => {
    const press = fired();
    press.cancelled();
    expect(press.takeClick()).toBe(false);
  });

  it('a short tap or a moved finger does not open the menu', () => {
    const press = new LongPress();
    const fire = vi.fn();
    press.down('touch', 0, 0, fire);
    vi.advanceTimersByTime(LongPress.holdMs - 1);
    press.up();
    vi.advanceTimersByTime(1000);
    press.down('touch', 0, 0, fire);
    press.move(0, LongPress.slopPx + 1);
    vi.advanceTimersByTime(1000);
    expect(fire).not.toHaveBeenCalled();
    expect(press.takeClick()).toBe(false);
  });
});
