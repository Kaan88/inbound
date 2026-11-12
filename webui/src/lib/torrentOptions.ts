// Options > Torrents > Network in the web UI: the port the engine uses and how it went, read-only. On the Windows
// app's web UI they are the PC's settings (WebSettingsRules keeps them PC-only); on a server they come from its
// environment (DOWNLOADMANAGER_TORRENT_PORT, DOWNLOADMANAGER_TORRENT_UPNP). Nothing here touches the page.

import type { Settings } from './types';

/** torrent.network's answer (TorrentNetworkResult): what the engine did since the Host started. */
export interface TorrentNetwork {
  ok?: boolean;
  ran?: boolean;
  running?: boolean;
  port?: number;
  listenError?: string | null;
  mappedPort?: number | null;
  incomingSeen?: boolean;
  unusablePort?: number | null;
}

export interface NetworkLine {
  label: string;
  value: string;
  problem?: boolean;
}

export const serverPortVariable = 'DOWNLOADMANAGER_TORRENT_PORT';
export const serverUpnpVariable = 'DOWNLOADMANAGER_TORRENT_UPNP';

/** The rows under Network: the port, router forwarding and what the engine last did. */
export function networkLines(input: { pcOnly: boolean; settings: Settings | null | undefined; network: TorrentNetwork | null | undefined }): NetworkLine[] {
  const { pcOnly, settings, network } = input;
  const lines: NetworkLine[] = [];
  const where = pcOnly ? 'the app' : 'the server';
  const usedPort = network?.ran && (network.port ?? 0) > 0 ? network.port! : 0;
  if (pcOnly) {
    const port = settings?.torrentListenPort ?? 0;
    lines.push({ label: 'Listening port', value: port > 0 ? String(port) : 'Automatic' });
    lines.push({ label: 'Router forwarding', value: settings?.torrentUpnp ? 'Asked for (UPnP and NAT-PMP)' : 'Off' });
  } else {
    lines.push({ label: 'Listening port', value: usedPort > 0 ? String(usedPort) : `Set by ${serverPortVariable}` });
    lines.push({ label: 'Router forwarding', value: network?.mappedPort ? 'On' : `Set by ${serverUpnpVariable} (off unless set)` });
  }
  if (!network || network.ok === false) return lines;
  if (!network.ran) {
    lines.push({ label: 'Status', value: `No torrent has run since ${where} started. Nothing listens while none runs.` });
    return lines;
  }
  if (network.listenError) lines.push({ label: 'Status', value: `The port couldn't be opened: ${network.listenError}`, problem: true });
  else if (network.running) lines.push({ label: 'Status', value: usedPort > 0 ? `Listening on port ${usedPort}` : 'Listening' });
  else lines.push({ label: 'Status', value: usedPort > 0 ? `Listened on port ${usedPort} last time. Nothing listens now.` : 'Nothing listens now.' });
  if (network.unusablePort && usedPort > 0) lines.push({ label: 'Port used', value: `Port ${network.unusablePort} couldn't be opened, so ${usedPort} was used instead.`, problem: true });
  if (network.mappedPort) lines.push({ label: 'Forwarded port', value: String(network.mappedPort) });
  lines.push({
    label: 'Incoming peers',
    value: network.incomingSeen ? 'Other peers connected in.' : 'None yet. Torrents still work, with fewer peers.',
  });
  return lines;
}
