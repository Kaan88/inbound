// The sign-in page's wording, kept out of the page so it is tested.

/**
 * The server's refusal without its own "Wait N s" sentence when it also says how long to wait: the page counts that
 * down itself, in the same line, so two numbers never disagree (U-WU-13).
 */
export function refusalText(error: string, retryAfterSeconds: number | null | undefined): string {
  const text = error.trim();
  if (!retryAfterSeconds) return text;
  return text.replace(/\s*Wait \d+ ?s(econds)? (and|before) try(ing)? again\.?$/i, '').trim();
}

/** The one line shown after a refused try: the reason, then the live wait while there is one. */
export function refusalLine(reason: string, waitLeft: number): string {
  if (waitLeft <= 0) return reason;
  const wait = `Wait ${waitLeft} s before trying again.`;
  return reason ? `${reason} ${wait}` : wait;
}
