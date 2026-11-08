// Only the newest of overlapping requests may apply its reply. Requests sent as separate POSTs (while the WebSocket
// reconnects) can finish in any order, so an older reply arriving last must be dropped.

export interface Latest {
  /** Starts a request; the returned function says whether it is still the newest one. */
  begin(): () => boolean;
}

export function latest(): Latest {
  let seq = 0;
  return {
    begin() {
      const mine = ++seq;
      return () => mine === seq;
    },
  };
}
