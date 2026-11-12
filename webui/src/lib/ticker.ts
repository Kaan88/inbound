// A clock for a countdown on screen (a sign-in wait, a pairing code's time left) that runs only while it counts
// down: nothing wakes the page when there is nothing to count, the owner ranks idle wake-ups above memory.

/** What everyWhileVisible needs of the page: `document`, or a stand-in in a test. */
export interface VisibilitySource {
  readonly visibilityState: string;
  addEventListener(type: 'visibilitychange', listener: () => void): void;
  removeEventListener(type: 'visibilitychange', listener: () => void): void;
}

/**
 * Calls `run` every `everyMs` while the page is showing, and not at all while its tab is hidden; once more at once when
 * it shows again, so what was missed is read (audit 4 M11). Returns the function that stops it, for a Svelte `$effect`.
 */
export function everyWhileVisible(everyMs: number, run: () => void, page: VisibilitySource = document): () => void {
  let timer: ReturnType<typeof setInterval> | null = null;
  const start = () => {
    if (timer === null) timer = setInterval(run, everyMs);
  };
  const stop = () => {
    if (timer !== null) clearInterval(timer);
    timer = null;
  };
  const changed = () => {
    if (page.visibilityState === 'hidden') {
      stop();
    } else if (timer === null) {
      run();
      start();
    }
  };
  if (page.visibilityState !== 'hidden') start();
  page.addEventListener('visibilitychange', changed);
  return () => {
    stop();
    page.removeEventListener('visibilitychange', changed);
  };
}

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
