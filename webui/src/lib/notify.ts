// "Show a notification when a download finishes", a choice of this browser (the desktop's setting is the server PC's
// own notification, which a browser can't show). Kept in this browser's storage; the browser's permission is asked
// for when it is turned on.

const key = 'inbound.notify';

export function notifySupported(): boolean {
  return typeof Notification !== 'undefined';
}

/** Whether this browser was asked to notify, and is allowed to. */
export function notifyEnabled(): boolean {
  try {
    return notifySupported() && localStorage.getItem(key) === '1' && Notification.permission === 'granted';
  } catch {
    return false;
  }
}

export function storeNotify(on: boolean): void {
  try {
    if (on) localStorage.setItem(key, '1');
    else localStorage.removeItem(key);
  } catch {
    // A private window: the choice lasts until the page closes.
  }
}

/** Asks the browser for permission; true when notifications are allowed. */
export async function askNotifyPermission(): Promise<boolean> {
  if (!notifySupported()) return false;
  if (Notification.permission === 'granted') return true;
  if (Notification.permission === 'denied') return false;
  try {
    return (await Notification.requestPermission()) === 'granted';
  } catch {
    return false;
  }
}

/** A download that was in another state and is now completed (not one first seen as completed). */
export function justCompleted(before: string | undefined, after: string | undefined): boolean {
  return after === 'completed' && before !== undefined && before !== 'completed';
}

export function showCompleted(fileName: string): void {
  try {
    new Notification('Download complete', { body: fileName, tag: `inbound-${fileName}` });
  } catch {
    // The browser refused (some phones need a service worker); the list still shows it.
  }
}
