// A clock for a countdown on screen (a sign-in wait, a pairing code's time left) that runs only while it counts
// down: nothing wakes the page when there is nothing to count, the owner ranks idle wake-ups above memory.

/**
 * Calls `tick` with the time now, then every `everyMs`, until `until` (a Date.now() value) has passed; the last
 * call is at or after `until`, so a countdown reaches 0. Nothing at all runs when `until` has passed already.
 * Returns the function that stops it early, for a Svelte `$effect` to return.
 */
export function tickUntil(until: number, everyMs: number, tick: (now: number) => void): (() => void) | undefined {
  if (!(until > Date.now())) return undefined;
  tick(Date.now());
  const timer = setInterval(() => {
    const now = Date.now();
    tick(now);
    if (now >= until) clearInterval(timer);
  }, everyMs);
  return () => clearInterval(timer);
}
