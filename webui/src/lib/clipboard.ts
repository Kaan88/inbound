// Copying text. The Clipboard API exists only on https and on this computer's own address; a server opened over plain
// http on the network has none, so a hidden text box and the older copy command do it there.

export interface CopyEnv {
  /** navigator.clipboard where the page may use it. */
  clipboard?: { writeText(text: string): Promise<void> } | null;
  /** Copies through a selected, hidden text box; true when the browser says it did. */
  legacyCopy?: (text: string) => boolean;
}

export async function copyText(text: string, env: CopyEnv = browserEnv()): Promise<boolean> {
  if (env.clipboard) {
    try {
      await env.clipboard.writeText(text);
      return true;
    } catch {
      // Refused (no permission, the page not focused): try the other way.
    }
  }
  try {
    return env.legacyCopy?.(text) ?? false;
  } catch {
    return false;
  }
}

function browserEnv(): CopyEnv {
  const secure = typeof isSecureContext !== 'undefined' && isSecureContext;
  return {
    clipboard: secure && typeof navigator !== 'undefined' ? navigator.clipboard ?? null : null,
    legacyCopy: typeof document === 'undefined' ? undefined : legacyCopy,
  };
}

function legacyCopy(text: string): boolean {
  const active = document.activeElement as HTMLElement | null;
  const box = document.createElement('textarea');
  box.value = text;
  box.setAttribute('readonly', '');
  box.setAttribute('aria-hidden', 'true');
  box.style.cssText = 'position:fixed;top:0;left:-9999px;opacity:0;';
  // Inside a dialog, so its focus trap and Escape still see the focus as theirs.
  (active?.closest('[role="dialog"]') ?? document.body).append(box);
  box.select();
  let copied = false;
  try {
    copied = document.execCommand('copy');
  } finally {
    box.remove();
    active?.focus?.({ preventScroll: true });
  }
  return copied;
}
