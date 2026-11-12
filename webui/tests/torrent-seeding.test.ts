import jobDetailsSource from '../src/screens/JobDetails.svelte?raw';
import listScreenSource from '../src/screens/ListScreen.svelte?raw';
import swarmPanelSource from '../src/components/TorrentSwarmPanel.svelte?raw';
import { describe, expect, it } from 'vitest';
import { detailRows } from '../src/lib/details';
import { jobView, statusWords } from '../src/lib/list';
import { applyForm, formFrom, friendlyRefusal, seedingFrom, seedingProblem, uploadLimitBytes, validateAll, type SeedingForm } from '../src/lib/settingsRules';
import {
  addSpeedPoint, canStartSeeding, canStopSeeding, cellNote, ipFamily, mayRun, mostSpeedPoints, peerFlagsText, peerRows, pieceCells, piecesHad, piecesSummary,
  ratioText, seedGoalText, seedSaid, seedTargets, shouldAsk, speedLine, torrentTabs, trackerRows, trackerShown, trackerStatusText, uploadRateText,
} from '../src/lib/torrentDetails';
import { networkLines } from '../src/lib/torrentOptions';
import type { Job, Settings, Torrent } from '../src/lib/types';

const hash = '7acf8fb590b2060dd9c3146ef770169d593433b0';

function torrent(extra: Partial<Torrent> = {}): Torrent {
  return { infoHash: hash, name: 'Big Buck Bunny', hasMetadata: true, awaitingFileChoice: false, fileCount: 1, wantedCount: 1, totalBytes: 2000, wantedBytes: 2000, peers: 3, seeds: 1, ...extra };
}

function tjob(id: string, state: string, t: Partial<Torrent> = {}, extra: Partial<Job> = {}): Job {
  return {
    id, url: `magnet:?xt=urn:btih:${hash}`, fileName: 'bbb.mkv', destinationDirectory: '/d', finalFilePath: '/d/bbb.mkv', receivedBytes: 2000, totalBytes: 2000,
    state, createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', queueId: 0, torrent: torrent(t), ...extra,
  };
}

function settings(extra: Partial<Settings> = {}): Settings {
  return {
    defaultDownloadDirectory: '/data/downloads', maxConcurrentDownloads: 3, defaultQueueId: 0, queues: [], schedules: [],
    defaultConnections: 8, useCategoryFolders: true, maxConnectionsPerHost: 8, connectionProfiles: [],
    proxy: { mode: 'none', hasPassword: false }, siteLogins: [], allowLocalNetwork: true, autoResumeUnfinishedOnStartup: true,
    browserIntegrationEnabled: true, allowSystemCompletionActions: false, favoriteFolders: [], recentDestinationDirectories: [], downloadGroups: [],
    excludedFileExtensions: [], excludedHosts: [], torrentsEnabled: true, torrentSeedAfterDownload: true, torrentSeedRatio: 1, torrentSeedHours: 48, torrentUploadLimit: 0,
    ...extra,
  };
}

describe('a seeding row (T2)', () => {
  it('says Seeding with its ratio, and its rate is the upload rate', () => {
    const job = tjob('a', 'completed', { seeding: true, ratio: 0.714, uploaded: 1428, uploadRate: 655360 });
    expect(statusWords(job)).toBe('Seeding · ratio 0.71');
    const view = jobView(job);
    expect(view.statusTone).toBe('active');
    expect(view.rate).toBe(655360);
    expect(view.rateText).toBe('↑ 640 KB/s');
  });

  it('shows no rate while a seeding torrent sends nothing, and a finished one that stopped is just complete', () => {
    expect(jobView(tjob('a', 'completed', { seeding: true, ratio: 0, uploadRate: 0 })).rateText).toBe('');
    expect(statusWords(tjob('a', 'completed', { seeding: false, ratio: 1.2 }))).toBe('Completed');
    expect(uploadRateText(0)).toBe('');
    expect(ratioText(undefined)).toBe('0.00');
    expect(ratioText(1)).toBe('1.00');
  });

  it('the details add what it uploaded and since when it seeds', () => {
    const rows = detailRows(tjob('a', 'completed', { seeding: true, ratio: 0.5, uploaded: 1000, uploadRate: 2048, seedingSince: '2026-11-11T14:02:00Z' }));
    const labels = rows.map((row) => row.label);
    expect(labels).toEqual(['Status', 'Size', 'Downloaded', 'Transfer rate', 'Time left', 'Peers', 'Uploaded', 'Seeding', 'Files', 'Info hash']);
    expect(rows.find((row) => row.label === 'Uploaded')!.value).toBe('1000 B · ratio 0.50');
    expect(rows.find((row) => row.label === 'Seeding')!.value).toMatch(/^since .+ · ↑ 2\.00 KB\/s$/);
    // An older server sends none of it: nothing new shows.
    expect(detailRows(tjob('a', 'downloading')).map((row) => row.label)).not.toContain('Uploaded');
  });
});

describe('Start seeding and Stop seeding', () => {
  const done = tjob('done', 'completed');
  const seeding = tjob('seed', 'completed', { seeding: true });
  const running = tjob('run', 'downloading');
  const plain: Job = { ...tjob('file', 'completed'), torrent: undefined };

  it('start applies to a finished torrent that does not seed, stop to one that does', () => {
    expect([canStartSeeding(done), canStartSeeding(seeding), canStartSeeding(running), canStartSeeding(plain)]).toEqual([true, false, false, false]);
    expect([canStopSeeding(done), canStopSeeding(seeding), canStopSeeding(plain)]).toEqual([false, true, false]);
  });

  it('acts on every selected torrent it applies to', () => {
    const selection = [done, seeding, running, plain, tjob('done2', 'completed')];
    expect(seedTargets(selection, true).map((job) => job.id)).toEqual(['done', 'done2']);
    expect(seedTargets(selection, false).map((job) => job.id)).toEqual(['seed']);
    expect(seedSaid(true, 1)).toBe('Seeding started.');
    expect(seedSaid(false, 3)).toBe('Seeding stopped for 3 torrents.');
  });

  it('the row menu and the details offer both and send torrent.seed', () => {
    const list = listScreenSource;
    expect(list).toContain("label: 'Start Seeding'");
    expect(list).toContain("label: 'Stop Seeding'");
    expect(list).toContain("store.attempt('torrent.seed', { jobId: job.id, on })");
    const details = jobDetailsSource;
    expect(details).toContain("run('torrent.seed', { on })");
  });

  it('words the seeding goal from the settings', () => {
    expect(seedGoalText(1, 48)).toBe('Ratio 1.0 or 48 h, whichever comes first');
    expect(seedGoalText(1.25, 0)).toBe('Ratio 1.25');
    expect(seedGoalText(0, 12)).toBe('12 h');
  });
});

describe('the details tabs', () => {
  it('a torrent has Overview, Files, Peers, Trackers, Pieces & Speed and its own Options, finished or not; no speed limiter (T3)', () => {
    expect(torrentTabs(false).map(([key]) => key)).toEqual(['status', 'files', 'peers', 'trackers', 'pieces', 'options', 'completion']);
    expect(torrentTabs(true).map(([, label]) => label)).toEqual(['Overview', 'Files', 'Peers', 'Trackers', 'Pieces & Speed', 'Options']);
  });

  it('asks only while the tab is shown, the page visible, the server reachable and the torrent may run', () => {
    expect(mayRun(tjob('a', 'downloading'))).toBe(true);
    expect(mayRun(tjob('a', 'completed', { seeding: true }))).toBe(true);
    expect(mayRun(tjob('a', 'completed'))).toBe(false);
    expect(mayRun(tjob('a', 'paused'))).toBe(false);
    const all = { visible: true, connected: true, running: true, busy: false };
    expect(shouldAsk(all)).toBe(true);
    expect(shouldAsk({ ...all, visible: false })).toBe(false);
    expect(shouldAsk({ ...all, connected: false })).toBe(false);
    expect(shouldAsk({ ...all, running: false })).toBe(false);
    expect(shouldAsk({ ...all, busy: true })).toBe(false);
  });

  it('the panel times its asks every 2 s only while the page is visible, and only while it is shown', () => {
    const panel = swarmPanelSource;
    expect(panel).toContain("document.visibilityState !== 'hidden'");
    expect(panel).toContain('if (!visible || !connected || !running) return;');
    expect(panel).toContain('setInterval(() => void read(), swarmPollMs)');
    expect(panel).toContain('clearInterval(timer)');
  });

  it('each tab gets its own panel, and a reply that comes back after the tab changed is dropped', () => {
    const details = jobDetailsSource;
    expect(details).toContain('{#key tab}<TorrentSwarmPanel {job} kind={tab} />{/key}');
    const panel = swarmPanelSource;
    expect(panel.match(/if \(askedKind !== kind \|\| askedJob !== job\.id\) return;/g)?.length).toBe(2);
  });
});

describe('peers', () => {
  it('lists the fastest first, by number, the address only on hover', () => {
    const rows = peerRows([
      { address: '203.0.113.5', port: 51413, client: 'Transmission 4.1', flags: ['outgoing'], downloadRate: 1024, uploadRate: 0, progress: 0.41 },
      { address: '2001:db8::1', port: 6881, client: '', flags: ['utp', 'encrypted', 'seed', 'incoming'], downloadRate: 4096, uploadRate: 10, progress: 1 },
    ]);
    expect(rows.map((row) => row.label)).toEqual(['Peer 1 · IPv6', 'Peer 2 · IPv4']);
    expect(rows[0]).toMatchObject({ address: '[2001:db8::1]:6881', client: 'Unknown', flags: 'uTP · encrypted · incoming · seed', has: '100%', down: '4.00 KB/s' });
    expect(rows[1]).toMatchObject({ address: '203.0.113.5:51413', flags: 'TCP', has: '41%', up: '0' });
    expect(ipFamily('10.0.0.1')).toBe('IPv4');
    expect(peerFlagsText(undefined)).toBe('TCP');
  });
});

describe('trackers', () => {
  it('never shows a passkey or a query', () => {
    expect(trackerShown('http://bttracker.debian.org:6969/announce')).toBe('http://bttracker.debian.org:6969/announce');
    expect(trackerShown('https://tracker.example/a1b2c3d4e5f6a7b8c9d0e1f2/announce')).toBe('https://tracker.example/…');
    expect(trackerShown('https://tracker.example/announce?passkey=secret')).toBe('https://tracker.example/announce/…');
    expect(trackerShown('udp://tracker.example.org:1337')).toBe('udp://tracker.example.org:1337');
    expect(trackerShown('not a url')).toBe('A tracker');
  });

  it('words the status and the counts, in tier order', () => {
    expect(trackerStatusText('notContacted')).toBe('Not contacted');
    expect(trackerStatusText('error', 'timed out')).toBe('Error: timed out');
    const rows = trackerRows([
      { url: 'udp://b.example:1', tier: 1, status: 'error', message: 'refused', seeds: -1, peers: -1 },
      { url: 'http://a.example/announce', tier: 0, status: 'working', seeds: 1198, peers: 27 },
    ]);
    expect(rows.map((row) => [row.url, row.status, row.seeds, row.peers, row.failed])).toEqual([
      ['http://a.example/announce', 'Working', '1,198', '27', false],
      ['udp://b.example:1', 'Error: refused', '–', '–', true],
    ]);
  });
});

describe('pieces', () => {
  it('reads the runs and the bitmap alike', () => {
    expect([...piecesHad({ count: 10, ranges: [[0, 2], [7, 9]] })]).toEqual([1, 1, 1, 0, 0, 0, 0, 1, 1, 1]);
    // 0b11100000 0b01000000: pieces 0, 1, 2 and 9.
    expect([...piecesHad({ count: 10, bitmap: btoa(String.fromCharCode(0xe0, 0x40)) })]).toEqual([1, 1, 1, 0, 0, 0, 0, 0, 0, 1]);
    expect([...piecesHad({ count: 3, bitmap: '***' })]).toEqual([0, 0, 0]);
    expect(piecesHad({ count: 3, ranges: [[2, 99]] })[2]).toBe(1);
  });

  it('puts many pieces into at most so many cells, each the share it has', () => {
    const had = piecesHad({ count: 8, ranges: [[0, 4]] });
    expect(pieceCells(had, 4)).toEqual([1, 1, 0.5, 0]);
    expect(pieceCells(had, 100)).toHaveLength(8);
    expect(pieceCells(new Uint8Array(0), 10)).toEqual([]);
    expect(piecesSummary({ count: 15820, have: 10124, pieceLength: 262144 })).toBe('10,124 of 15,820 pieces · 256 KB each');
    expect(cellNote(8000, 200)).toBe('Each cell is about 40 pieces');
    expect(cellNote(100, 200)).toBe('');
  });

  it('keeps two minutes of speed samples for the small graph', () => {
    let points: { down: number; up: number }[] = [];
    for (let i = 0; i < mostSpeedPoints + 5; i++) points = addSpeedPoint(points, { down: i, up: -1 });
    expect(points).toHaveLength(mostSpeedPoints);
    expect(points[0].up).toBe(0);
    expect(speedLine([{ down: 0, up: 0 }, { down: 10, up: 5 }], (p) => p.down, 300, 60).split(' ')).toHaveLength(2);
  });
});

describe('Options > Torrents: seeding', () => {
  it('reads the four settings, and none from a server that has no seeding', () => {
    const form = formFrom(settings({ torrentUploadLimit: 1536 }));
    expect(form.seeding).toEqual({ afterDownload: true, ratio: 1, hours: 48, uploadKb: 1, uploadBytesAtOpen: 1536, uploadKbAtOpen: 1 });
    expect(seedingFrom(settings({ torrentSeedRatio: undefined }))).toBeNull();
    const older = settings({ torrentSeedRatio: undefined, torrentSeedHours: undefined, torrentSeedAfterDownload: undefined, torrentUploadLimit: undefined });
    const copy = { ...older };
    applyForm(copy, formFrom(older));
    expect(copy.torrentSeedRatio).toBeUndefined();
    expect(copy.torrentUploadLimit).toBeUndefined();
  });

  it("checks them with the Host's own rules and words", () => {
    const base: SeedingForm = { afterDownload: true, ratio: 1, hours: 48, uploadKb: null, uploadBytesAtOpen: 0, uploadKbAtOpen: null };
    expect(seedingProblem(base)).toBeNull();
    expect(seedingProblem({ ...base, ratio: 101 })?.message).toBe('Enter a seeding ratio from 0 to 100 (0 is no ratio limit).');
    expect(seedingProblem({ ...base, ratio: null })?.field).toBe('seed-ratio');
    expect(seedingProblem({ ...base, hours: 9000 })?.message).toBe('Enter seeding hours from 0 to 8760 (0 is no time limit).');
    expect(seedingProblem({ ...base, ratio: 0, hours: 0 })?.message).toBe("Seeding needs a ratio or a time limit; both can't be 0.");
    expect(seedingProblem({ ...base, ratio: 0, hours: 1 })).toBeNull();
    expect(seedingProblem({ ...base, uploadKb: -1 })).toEqual({ message: "The torrent upload limit can't be below 0.", field: 'seed-upload' });
    expect(seedingProblem({ ...base, uploadKb: 0 })).toBeNull();
    expect(seedingProblem({ ...base, uploadKb: 10000000 })).toBeNull();
    expect(seedingProblem({ ...base, uploadKb: 10000001 })).toEqual({ message: 'The torrent upload limit can be at most 10000000 KB/s. 0 or empty is none.', field: 'seed-upload' });
    const form = formFrom(settings());
    form.seeding!.hours = -1;
    expect(validateAll(form)).toEqual([{ message: 'Enter seeding hours from 0 to 8760 (0 is no time limit).', field: 'seed-hours', page: 'torrents' }]);
  });

  it('writes them back, an untouched upload limit as it was stored', () => {
    const stored = settings({ torrentUploadLimit: 1536 });
    const form = formFrom(stored);
    expect(uploadLimitBytes(form.seeding!)).toBe(1536);
    form.seeding!.afterDownload = false;
    form.seeding!.ratio = 2.5;
    form.seeding!.hours = 0;
    form.seeding!.uploadKb = 500;
    const copy = { ...stored };
    applyForm(copy, form);
    expect([copy.torrentSeedAfterDownload, copy.torrentSeedRatio, copy.torrentSeedHours, copy.torrentUploadLimit]).toEqual([false, 2.5, 0, 512000]);
    form.seeding!.uploadKb = null;
    applyForm(copy, form);
    expect(copy.torrentUploadLimit).toBe(0);
  });

  it("names the PC-only torrent settings in a refusal the way Options does", () => {
    expect(friendlyRefusal('These settings can only be changed in the app on this PC: torrentListenPort, torrentUpnp. Nothing was saved.'))
      .toBe('These settings can only be changed in the app on this PC: Listening port, Ask my router to forward the port. Nothing was saved.');
  });
});

describe('Options > Torrents: network', () => {
  it("shows the PC's port and router setting on the Windows app's web UI", () => {
    const lines = networkLines({ pcOnly: true, settings: settings({ torrentListenPort: 51413, torrentUpnp: true }), network: { ok: true, ran: false } });
    expect(lines).toEqual([
      { label: 'Listening port', value: '51413' },
      { label: 'Router forwarding', value: 'Asked for (UPnP and NAT-PMP)' },
      { label: 'Status', value: 'No torrent has run since the app started. Nothing listens while none runs.' },
    ]);
    expect(networkLines({ pcOnly: true, settings: settings({ torrentListenPort: 0 }), network: null })[0].value).toBe('Chosen when torrents are turned on');
  });

  it("names the server's environment for its port until a torrent ran", () => {
    expect(networkLines({ pcOnly: false, settings: settings({ torrentListenPort: 33333 }), network: { ok: true, ran: false } }).slice(0, 2)).toEqual([
      { label: 'Listening port', value: 'Set by DOWNLOADMANAGER_TORRENT_PORT' },
      { label: 'Router forwarding', value: 'Set by DOWNLOADMANAGER_TORRENT_UPNP (off unless set)' },
    ]);
    const ran = networkLines({ pcOnly: false, settings: null, network: { ok: true, ran: true, running: true, port: 6881, mappedPort: 6881, incomingSeen: true } });
    expect(ran).toEqual([
      { label: 'Listening port', value: '6881' },
      { label: 'Router forwarding', value: 'On' },
      { label: 'Status', value: 'Listening on port 6881' },
      { label: 'Forwarded port', value: '6881' },
      { label: 'Incoming peers', value: 'Other peers connected in.' },
    ]);
  });

  it('says when the port could not be opened', () => {
    const lines = networkLines({ pcOnly: true, settings: settings({ torrentListenPort: 50000 }), network: { ok: true, ran: true, running: false, port: 50123, unusablePort: 50000 } });
    expect(lines).toContainEqual({ label: 'Port used', value: "Port 50000 couldn't be opened, so 50123 was used instead.", problem: true });
    expect(lines).toContainEqual({ label: 'Status', value: 'Listened on port 50123 last time. Nothing listens now.' });
    const failed = networkLines({ pcOnly: false, settings: null, network: { ok: true, ran: true, listenError: 'Address in use' } });
    expect(failed).toContainEqual({ label: 'Status', value: "The port couldn't be opened: Address in use", problem: true });
  });
});
