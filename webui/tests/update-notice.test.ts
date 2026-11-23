import { describe, expect, it } from 'vitest';
import { apply, emptyModel } from '../src/lib/model';
import type { Envelope } from '../src/lib/protocol';
import {
  composeCommand,
  dialogSubtitle,
  filterUpdateNotes,
  noticeShown,
  noticeWords,
  runCommand,
} from '../src/lib/updateNotice';

function message(type: string, payload: unknown): Envelope {
  return { v: 1, type, id: 'x', payload };
}

const now = Date.parse('2026-11-23T12:00:00Z');

describe('update notice', () => {
  it('shows only for a newer, known version and an honest dismiss state', () => {
    expect(noticeShown(null, '0.7.4', null, null, now)).toBe(false);
    expect(noticeShown('0.7.2', null, null, null, now)).toBe(false);
    expect(noticeShown('0.7.2', '0.7.2', null, null, now)).toBe(false);
    expect(noticeShown('0.7.4', '0.7.2', null, null, now)).toBe(false);
    expect(noticeShown('0.7.2', '0.7.4', null, null, now)).toBe(true);
  });

  it('respects a day-long hide and a skip', () => {
    const hiddenUntil = '2026-11-24T12:00:00Z';
    expect(noticeShown('0.7.2', '0.7.4', hiddenUntil, null, now)).toBe(false);
    expect(noticeShown('0.7.2', '0.7.4', hiddenUntil, null, Date.parse(hiddenUntil))).toBe(true);
    expect(noticeShown('0.7.2', '0.7.4', null, '0.7.4', now)).toBe(false);
    // A newer version clears the skip.
    expect(noticeShown('0.7.2', '0.7.5', null, '0.7.4', now)).toBe(true);
  });

  it('words the notice and dialog for each kind of install', () => {
    expect(noticeWords('server', '0.7.2', '0.7.4')).toEqual({
      title: 'Inbound 0.7.4 is out for Docker.',
      body: 'This server runs 0.7.2. Pull the new image and restart the container to update.',
    });
    expect(noticeWords('windows', '0.7.2', '0.7.4').title).toBe('Inbound 0.7.4 is out.');
    expect(dialogSubtitle('server', '0.7.2')).toBe('This server runs 0.7.2 in Docker');
    expect(dialogSubtitle('windows', '0.7.2')).toBe('This PC runs 0.7.2');
  });

  it('carries the docker commands the mockup draws', () => {
    expect(composeCommand).toBe('docker compose pull inbound && docker compose up -d inbound');
    expect(runCommand).toBe('docker pull ghcr.io/kaan88/inbound:latest');
  });

  it('folds the notes to this server, hiding torrent and foreign lines', () => {
    const notes = [
      { text: 'A new web UI line.', tags: ['webui'] },
      { text: 'A server line.', tags: ['server'] },
      { text: 'A windows-only line.', tags: ['windows'] },
      { text: 'A torrents line.', tags: ['torrents'] },
      { text: 'A linux-only line.', tags: ['linux'] },
    ];
    const view = filterUpdateNotes(notes, { torrents: false, providers: [] });
    expect(view.here).toEqual(['A new web UI line.', 'A server line.']);
    expect(view.elsewhere).toEqual(['1 change in the Windows app']);
  });

  it('applies the update.notice push to the status', () => {
    const model = emptyModel();
    expect(apply(model, message('update.notice', { hiddenUntil: 'x', skippedVersion: '0.7.4' }))).toBe(false);
    apply(model, message('jobs.snapshot', { jobs: [], settings: { downloadGroups: [] }, status: { hostVersion: '0.7.2' } }));
    expect(apply(model, message('update.notice', { hiddenUntil: 'x', skippedVersion: '0.7.4' }))).toBe(true);
    expect(model.status?.updateNoticeHiddenUntil).toBe('x');
    expect(model.status?.updateNoticeSkippedVersion).toBe('0.7.4');
    expect(apply(model, message('update.notice', { hiddenUntil: 'x', skippedVersion: '0.7.4' }))).toBe(false);
  });
});
