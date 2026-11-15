// Sites in the web UI (SITES.7): the shapes of sites.* (docs/ipc-protocol.md, "Sites") and the plain helpers the screen
// uses, so the tests can feed them without a page. Paths on a site are absolute and use "/".

export interface SitesStatus {
  enabled: boolean;
  ownRclonePath?: string | null;
  rclone: string;
  rcloneVersion?: string | null;
  error?: string | null;
  rcloneSource?: string | null;
  rcloneInstalled?: string | null;
  autoUpdateRclone: boolean;
  rcloneInstalling: boolean;
  rcloneUpdate?: { version: string; state: string; note: string } | null;
}

export interface SiteHostKey {
  type: string;
  fingerprint: string;
  entry: string;
}

export interface Site {
  id: string;
  name: string;
  kind: string;
  host: string;
  port: number;
  user: string;
  authMethod: string;
  hasPassword: boolean;
  hasPrivateKey: boolean;
  hasKeyPassphrase: boolean;
  hostKeys: SiteHostKey[];
  remoteFolder: string;
  localFolder?: string | null;
  stars: string[];
  tls?: string | null;
  url?: string | null;
  vendor?: string | null;
  provider?: string | null;
  endpoint?: string | null;
  region?: string | null;
  capabilities: string[];
  encrypted: boolean;
  signedIn: boolean;
  clientId?: string | null;
  hasClientSecret: boolean;
  signingIn: boolean;
  trustedCertificates: { fingerprint: string; subject: string; notAfter: string }[];
}

export interface SiteKind {
  kind: string;
  title: string;
  defaultPort: number;
  authMethods: string[];
  fields: string[];
  choices: Record<string, { value: string; title: string }[]>;
  capabilities: string[];
  userLabel?: string | null;
  passwordLabel?: string | null;
  note?: string | null;
}

/** The answer to most site requests: a problem names what the user can do about it (a host key, a certificate, a sign-in). */
export interface SiteResult {
  ok: boolean;
  message?: string | null;
  problem?: string | null;
  offeredFingerprint?: string | null;
  acceptedFingerprints?: string[];
  site?: Site | null;
}

export interface SiteItem {
  name: string;
  isFolder: boolean;
  size: number;
  modified?: string | null;
}

export interface SiteBrowseResult extends SiteResult {
  path: string;
  items: SiteItem[];
  total: number;
  offset: number;
  listing?: string | null;
}

export interface SiteCertificate {
  subject: string;
  issuer: string;
  notBefore: string;
  notAfter: string;
  fingerprint: string;
  names: string[];
  selfSigned: boolean;
  trustedBySystem: boolean;
  cannotTrust?: string | null;
}

export interface SiteTransfer {
  id: string;
  siteId: string;
  siteName: string;
  direction: string;
  remoteFolder: string;
  localFolder: string;
  conflict: string;
  state: string;
  problem?: string | null;
  message?: string | null;
  files: number;
  folders: number;
  filesDone: number;
  filesSkipped: number;
  filesFailed: number;
  filesRunning: number;
  bytes: number;
  bytesDone: number;
  speed: number;
  createdAt: string;
  finishedAt?: string | null;
}

/** What the Add a site form holds; secrets are blank unless typed (a blank one keeps the saved one). */
export interface SiteForm {
  id?: string;
  name: string;
  kind: string;
  host: string;
  port: number;
  user: string;
  authMethod: string;
  password: string;
  privateKey: string;
  keyPassphrase: string;
  remoteFolder: string;
  localFolder: string;
  tls: string;
  url: string;
  vendor: string;
  provider: string;
  endpoint: string;
  region: string;
  clientId: string;
  clientSecret: string;
}

export const problems = {
  hostKeyChanged: 'hostKeyChanged',
  hostKeyNotAccepted: 'hostKeyNotAccepted',
  certificateNotTrusted: 'certificateNotTrusted',
  signInNeeded: 'signInNeeded',
  exists: 'exists',
  off: 'off',
  listingChanged: 'listingChanged',
} as const;

export function newForm(kind: SiteKind | undefined, existing?: Site): SiteForm {
  const choice = (field: string) => kind?.choices?.[field]?.[0]?.value ?? '';
  if (existing) {
    return {
      id: existing.id,
      name: existing.name,
      kind: existing.kind,
      host: existing.host,
      port: existing.port,
      user: existing.user,
      authMethod: existing.authMethod,
      password: '',
      privateKey: '',
      keyPassphrase: '',
      remoteFolder: existing.remoteFolder,
      localFolder: existing.localFolder ?? '',
      tls: existing.tls ?? choice('tls'),
      url: existing.url ?? '',
      vendor: existing.vendor ?? choice('vendor'),
      provider: existing.provider ?? choice('provider'),
      endpoint: existing.endpoint ?? '',
      region: existing.region ?? '',
      clientId: existing.clientId ?? '',
      clientSecret: '',
    };
  }
  return {
    name: '',
    kind: kind?.kind ?? 'sftp',
    host: '',
    port: kind?.defaultPort ?? 0,
    user: '',
    authMethod: kind?.authMethods?.[0] ?? 'password',
    password: '',
    privateKey: '',
    keyPassphrase: '',
    remoteFolder: '',
    localFolder: '',
    tls: choice('tls'),
    url: '',
    vendor: choice('vendor'),
    provider: choice('provider'),
    endpoint: '',
    region: '',
    clientId: '',
    clientSecret: '',
  };
}

/** The sites.save request for a form: only the kind's own fields, and a secret only when one was typed. */
export function saveRequest(form: SiteForm, kind: SiteKind | undefined): Record<string, unknown> {
  const fields = new Set(kind?.fields ?? []);
  const text = (value: string) => value.trim();
  const secret = (value: string) => (value ? value : undefined);
  const request: Record<string, unknown> = {
    id: form.id,
    name: text(form.name),
    kind: form.kind,
    remoteFolder: text(form.remoteFolder),
    localFolder: text(form.localFolder) || undefined,
    authMethod: form.authMethod,
  };
  if (fields.has('host')) request.host = text(form.host);
  if (fields.has('port')) request.port = Number(form.port) || kind?.defaultPort || 0;
  if (fields.has('user')) request.user = text(form.user);
  if (fields.has('password')) request.password = secret(form.password);
  if (fields.has('key') && form.authMethod === 'key') {
    request.privateKey = secret(form.privateKey);
    request.keyPassphrase = secret(form.keyPassphrase);
  }
  if (fields.has('tls')) request.tls = form.tls;
  if (fields.has('url')) request.url = text(form.url);
  if (fields.has('vendor')) request.vendor = form.vendor;
  if (fields.has('provider')) request.provider = form.provider;
  if (fields.has('endpoint')) request.endpoint = text(form.endpoint) || undefined;
  if (fields.has('region')) request.region = text(form.region) || undefined;
  if (fields.has('signIn')) {
    request.clientId = text(form.clientId) || undefined;
    request.clientSecret = secret(form.clientSecret);
  }
  return request;
}

/** What a site's row says under its name: where it is, without secrets. */
export function siteSummary(site: Site, kinds: SiteKind[]): string {
  const title = kinds.find((kind) => kind.kind === site.kind)?.title ?? site.kind.toUpperCase();
  if (site.kind === 'webdav') return `${title} · ${site.url ?? ''}`;
  if (site.kind === 's3') return `${title} · ${site.endpoint || site.provider || ''}`.replace(/ · $/, '');
  if (site.capabilities.includes('signIn')) return `${title} · ${site.signedIn ? 'signed in' : 'not signed in yet'}`;
  if (site.kind === 'b2') return title;
  const who = site.user ? `${site.user}@` : '';
  return `${title} · ${who}${site.host}${site.port ? `:${site.port}` : ''}`;
}

export function joinPath(folder: string, name: string): string {
  const base = folder.endsWith('/') ? folder : `${folder}/`;
  return `${base}${name}`;
}

export function parentPath(path: string): string {
  const trimmed = path.replace(/\/+$/, '');
  const at = trimmed.lastIndexOf('/');
  return at <= 0 ? '/' : trimmed.slice(0, at);
}

/** The folders from the top down to path, each with its own path, for the breadcrumb bar. */
export function crumbs(path: string): { name: string; path: string }[] {
  const parts = path.split('/').filter(Boolean);
  const result = [{ name: '/', path: '/' }];
  let at = '';
  for (const part of parts) {
    at += `/${part}`;
    result.push({ name: part, path: at });
  }
  return result;
}

/** The address that sends one of a site's files to this browser as a download. */
export function deviceDownloadUrl(siteId: string, path: string): string {
  return `/api/sites/device?id=${encodeURIComponent(siteId)}&path=${encodeURIComponent(path)}`;
}

export function deviceUploadUrl(siteId: string, folder: string, replace: boolean): string {
  return `/api/sites/device?id=${encodeURIComponent(siteId)}&folder=${encodeURIComponent(folder)}${replace ? '&replace=1' : ''}`;
}

/** A name a site can take: not empty, no slash, not "." or "..". Null when fine, else what is wrong. */
export function nameProblem(name: string): string | null {
  const trimmed = name.trim();
  if (!trimmed) return 'Type a name.';
  if (trimmed.includes('/') || trimmed.includes('\\')) return 'A name cannot have a slash in it.';
  if (trimmed === '.' || trimmed === '..') return 'That name is not allowed.';
  return null;
}

export type UploadOutcome = { ok: true } | { ok: false; status: number; error: string; problem?: string | null };

export interface UploadLike {
  open(method: string, url: string): void;
  setRequestHeader(name: string, value: string): void;
  send(body: Blob): void;
  abort(): void;
  status: number;
  responseText: string;
  upload: { onprogress: ((event: { loaded: number; total: number; lengthComputable: boolean }) => void) | null };
  onload: (() => void) | null;
  onerror: (() => void) | null;
  onabort: (() => void) | null;
}

/**
 * Sends one file from this device to a site folder (POST /api/sites/device). The name goes in X-File-Name, percent-
 * encoded; the server answers 409 when the name is taken and replace was not asked for.
 */
export function uploadToSite(
  siteId: string,
  folder: string,
  file: Blob & { name: string },
  replace: boolean,
  onProgress: (sent: number) => void,
  make: () => UploadLike = () => new XMLHttpRequest() as unknown as UploadLike,
): { done: Promise<UploadOutcome>; abort: () => void } {
  const request = make();
  const done = new Promise<UploadOutcome>((resolve) => {
    request.upload.onprogress = (event) => onProgress(event.loaded);
    request.onload = () => {
      if (request.status >= 200 && request.status < 300) {
        resolve({ ok: true });
        return;
      }
      let error = `The server answered ${request.status}.`;
      let problem: string | null = null;
      try {
        const body = JSON.parse(request.responseText) as { error?: string; problem?: string };
        if (body.error) error = body.error;
        problem = body.problem ?? null;
      } catch {
        // Not JSON: the status says enough.
      }
      if (request.status === 401) error = 'You are signed out. Sign in again and send it once more.';
      resolve({ ok: false, status: request.status, error, problem });
    };
    request.onerror = () => resolve({ ok: false, status: 0, error: 'The connection to Inbound was lost.' });
    request.onabort = () => resolve({ ok: false, status: 0, error: 'Stopped.' });
  });
  request.open('POST', deviceUploadUrl(siteId, folder, replace));
  request.setRequestHeader('X-File-Name', encodeURIComponent(file.name));
  request.send(file);
  return { done, abort: () => request.abort() };
}

/** A transfer batch's progress line, in the desktop's words. */
export function transferLine(transfer: SiteTransfer): string {
  const files = `${transfer.filesDone + transfer.filesSkipped} of ${transfer.files} file${transfer.files === 1 ? '' : 's'}`;
  const state: Record<string, string> = {
    running: 'Moving',
    paused: 'Paused',
    done: 'Done',
    failed: 'Failed',
    canceled: 'Cancelled',
  };
  const parts = [state[transfer.state] ?? transfer.state, files];
  if (transfer.filesSkipped) parts.push(`${transfer.filesSkipped} skipped`);
  if (transfer.filesFailed) parts.push(`${transfer.filesFailed} failed`);
  return parts.join(' · ');
}

export function transferFraction(transfer: SiteTransfer): number | null {
  if (transfer.bytes > 0) return Math.min(1, transfer.bytesDone / transfer.bytes);
  if (transfer.files > 0) return Math.min(1, (transfer.filesDone + transfer.filesSkipped) / transfer.files);
  return null;
}

/** What the rclone line under the switch says. */
export function rcloneLine(status: SitesStatus): string {
  if (status.rcloneInstalling) return 'Downloading rclone…';
  switch (status.rclone) {
    case 'missing':
      return 'rclone is not on this computer yet.';
    case 'starting':
      return 'Starting rclone…';
    case 'running':
      return `rclone ${status.rcloneVersion ?? ''} is running.`.replace('  ', ' ');
    case 'failed':
      return status.error ? `rclone stopped: ${status.error}` : 'rclone stopped.';
    case 'waiting':
      return 'rclone starts when a site is opened.';
    default:
      // On, not started yet: it starts with the first site used.
      return status.enabled ? 'rclone starts when a site is used.' : '';
  }
}
