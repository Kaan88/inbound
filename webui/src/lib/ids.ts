// Ids the server parses as a Guid (a schedule's id, for one). crypto.randomUUID exists only in a secure context, so a
// page opened over plain http from another computer has none; crypto.getRandomValues exists there too.

type CryptoLike = Pick<Crypto, 'getRandomValues'> & { randomUUID?: () => string };

/** A random version-4 UUID, also on a page that is not a secure context. */
export function newUuid(crypto: CryptoLike = globalThis.crypto): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}
