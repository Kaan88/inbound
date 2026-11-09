// Which words fit the host: the Docker server ("server") or the Windows app's web bridge ("windows"). The kind comes
// from the hello (`hostKind`) and, before sign-in, from /api/auth/status. A host that sends none is an older server.

export type HostKind = 'windows' | 'server';

export function hostKindOf(value: string | null | undefined): HostKind {
  return value === 'windows' ? 'windows' : 'server';
}

export interface HostTexts {
  folderPickerTitle: string;
  folderPlaceholder: string;
  postProgramNote: string;
  updatesNote: string;
  authOffNote: string;
  passwordSourceNote: string;
  queueEndNote: string;
  programPlaceholder: string;
  completionNote: string;
  /** A full-path example for a destination box. */
  destinationExample: string;
  folderListEmpty: string;
  /** The sign-in page. */
  signInHint: string;
  noPasswordYet: string;
  setupCodeHint: string | null;
}

const server: HostTexts = {
  folderPickerTitle: 'Choose a folder on the server',
  folderPlaceholder: 'Type a path, such as /downloads/movies',
  postProgramNote: "This makes the server run a program, so it can't be changed from a browser.",
  updatesNote: "These settings make the server run a program or trust an installer, so they can't be changed from a browser. They are shown as they are. The server is updated by updating its image.",
  authOffNote: 'Sign-in is off on this server (DOWNLOADMANAGER_AUTH=off): anyone who can reach it can use it.',
  passwordSourceNote: 'The password comes from DOWNLOADMANAGER_PASSWORD or DOWNLOADMANAGER_PASSWORD_FILE. Change it there and restart the server.',
  queueEndNote: 'Not available on the server: it has no desktop to open files on, and its life belongs to Docker or its service manager.',
  programPlaceholder: 'Path of an .exe on the server',
  completionNote: 'Completion programs and system actions are not available on the server.',
  destinationExample: '/downloads or D:\\Downloads',
  folderListEmpty: 'The folders this server may use',
  signInHint: 'Enter the password of this server.',
  noPasswordYet: 'This server has no password yet. The one you set here is needed to use it from any browser.',
  setupCodeHint: "This browser isn't on the server's own computer, so the server asks for the code it wrote in its log (docker logs).",
};

const windows: HostTexts = {
  folderPickerTitle: 'Choose a folder on the PC',
  folderPlaceholder: 'Type a path, such as D:\\Downloads\\Movies',
  postProgramNote: 'This makes the PC run a program, so it can only be set in the app on that PC.',
  updatesNote: 'These settings make the PC run a program or trust an installer, so they can only be set in the app on that PC. They are shown as they are. The app is updated there too.',
  authOffNote: 'Sign-in is off on this PC: anyone who can reach it can use it.',
  passwordSourceNote: 'The password is set in the app on the PC: Options, Web UI.',
  queueEndNote: 'Not available from a browser: opening a file, exiting or turning off the computer can only be set in the app on that PC.',
  programPlaceholder: 'Path of an .exe on the PC',
  completionNote: 'Completion programs and system actions can only be set in the app on that PC.',
  destinationExample: 'D:\\Downloads or C:\\Users\\Name\\Downloads',
  folderListEmpty: 'The folders on the PC you may use',
  signInHint: 'Enter the password you set in the app on the PC, under Options, Web UI.',
  noPasswordYet: 'This PC has no web password yet. Set it in the app on the PC, under Options, Web UI.',
  setupCodeHint: null,
};

export function hostTexts(kind: string | null | undefined): HostTexts {
  return hostKindOf(kind) === 'windows' ? windows : server;
}

/**
 * The hello names the Host gives this session; each is true only when the session may use it. Only names that a control
 * here depends on are read: program settings, completion actions (open the file, system actions) and openUi (the
 * desktop's completion window, which a browser session never gets).
 */
export interface Features {
  programSettings: boolean;
  completionActions: boolean;
  openUi: boolean;
}

export function featuresOf(capabilities: readonly string[] | null | undefined): Features {
  const has = (name: string) => capabilities?.includes(name) ?? false;
  return { programSettings: has('programSettings'), completionActions: has('completionActions'), openUi: has('openUi') };
}
