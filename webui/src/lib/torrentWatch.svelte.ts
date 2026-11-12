// T3: torrent.watch from the web UI (docs/ipc-protocol.md, G5). While the page is visible and shows a torrent (on the
// list or in its details), the server is told, so a torrent's progress comes every second; with no watch it comes every
// 5 s. The watch is a lease of 60 s, renewed every 45 s, ended when the page stops showing torrents, and ended by the
// server when the connection goes. Nothing is sent while no torrent shows. Call it while a component is set up.

import { store } from './store.svelte';
import { watchLeaseSeconds, watchRenewMs, watchWanted } from './torrentControls';

export function watchTorrents(shows: () => boolean): void {
  let visible = $state(typeof document === 'undefined' || document.visibilityState !== 'hidden');
  $effect(() => {
    const changed = () => (visible = document.visibilityState !== 'hidden');
    document.addEventListener('visibilitychange', changed);
    return () => document.removeEventListener('visibilitychange', changed);
  });

  const wanted = $derived(watchWanted({ visible, connected: store.connection === 'open', capable: store.has('torrents'), showsTorrents: shows() }));

  $effect(() => {
    if (!wanted) return;
    // Over the page's own connection only: the watch belongs to it, and a one-off request would end it at once.
    const send = (on: boolean) => {
      if (store.connection !== 'open') return;
      void store.raw('torrent.watch', on ? { on: true, leaseSeconds: watchLeaseSeconds } : { on: false }).catch(() => undefined);
    };
    send(true);
    const timer = setInterval(() => send(true), watchRenewMs);
    return () => {
      clearInterval(timer);
      send(false);
    };
  });
}
