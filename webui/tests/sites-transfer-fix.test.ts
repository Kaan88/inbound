import { describe, expect, it } from 'vitest';
import { problems } from '../src/lib/sites';
import { fixButtonText } from '../src/lib/sitesBrowse';
import { fixableProblem } from '../src/lib/sitesTransfers';
import type { SiteTransfer } from '../src/lib/sites';

// SITES.10 (6): a transfer the Host stopped because the remote refuses the sign-in or its certificate is not trusted now
// carries the same problem a listing does, so the row offers the same button the pane offers for that code.
const t = (over: Partial<SiteTransfer> = {}): SiteTransfer => ({
  id: 'a', siteId: 's', siteName: 'Box', direction: 'download', remoteFolder: '/x', localFolder: 'D:\\in', conflict: 'skip', state: 'failed',
  files: 2, folders: 0, filesDone: 0, filesSkipped: 0, filesFailed: 0, filesRunning: 0, bytes: 10, bytesDone: 0, speed: 0,
  createdAt: '2026-11-17T10:00:00Z', ...over,
} as SiteTransfer);

describe('a stopped transfer’s fix (SITES.10)', () => {
  it('offers Sign in for a refused sign-in and the certificate for one this PC does not trust', () => {
    const signIn = t({ problem: problems.signInNeeded });
    const certificate = t({ problem: problems.certificateNotTrusted });
    expect(fixableProblem(signIn)).toBe(problems.signInNeeded);
    expect(fixableProblem(certificate)).toBe(problems.certificateNotTrusted);
    // The same words the pane's button has for those codes.
    expect(fixButtonText(fixableProblem(signIn)!)).toBe('Sign in');
    expect(fixButtonText(fixableProblem(certificate)!)).toBe('Check the certificate');
  });

  it('offers nothing for any other failure, or on a batch that did not stop', () => {
    expect(fixableProblem(t({ problem: null }))).toBeNull();
    expect(fixableProblem(t({ problem: 'cantReach' }))).toBeNull();
    expect(fixableProblem(t({ state: 'running', problem: problems.signInNeeded }))).toBeNull();
    expect(fixableProblem(t({ state: 'paused', problem: problems.signInNeeded }))).toBeNull();
    expect(fixableProblem(t({ state: 'done', problem: null }))).toBeNull();
  });
});
