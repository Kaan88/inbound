// The outline glyphs drawn for Inbound (24-unit grid, 1.75 stroke, round caps), shared by the web UI's Icon.svelte and
// the browser extension (extensions/chrome/src/ui/icon.ts). Colored by the caller with the icon tokens.

export const iconPaths: Record<string, string> = {
    add: 'M12 5v14M5 12h14',
    play: 'M8 5.5v13l10-6.5z',
    pause: 'M9 5.5v13M15 5.5v13',
    stop: 'M7 7h10v10H7z',
    trash: 'M5 7h14M10 7V4.8h4V7M7 7l.8 12.2h8.4L17 7M10.2 10.5v5.5M13.8 10.5v5.5',
    sweep: 'M5 7h14M10 7V4.8h4V7M7 7l.8 12.2h8.4L17 7M9.6 13.6l1.8 1.8 3.2-3.6',
    settings: 'M12 9.2a2.8 2.8 0 1 0 0 5.6 2.8 2.8 0 0 0 0-5.6zM19.4 13.5l1.4 1.1-1.8 3.1-1.7-.6a7 7 0 0 1-1.9 1.1l-.3 1.8h-3.6l-.3-1.8a7 7 0 0 1-1.9-1.1l-1.7.6-1.8-3.1 1.4-1.1a7 7 0 0 1 0-2.2L3.8 9.3l1.8-3.1 1.7.6a7 7 0 0 1 1.9-1.1l.3-1.8h3.6l.3 1.8a7 7 0 0 1 1.9 1.1l1.7-.6 1.8 3.1-1.4 1.1a7 7 0 0 1 0 2.2z',
    calendar: 'M5 6.5h14v12.5H5zM5 10h14M9 4v4M15 4v4',
    chart: 'M5 19V5M5 19h14M9 16v-4M13 16V8M17 16v-6',
    folder: 'M3.5 7V5.5h6l1.8 2H20.5V18.5h-17z',
    'folder-plus': 'M3.5 7V5.5h6l1.8 2H20.5V18.5h-17zM12 10.5v5M9.5 13h5',
    cloud: 'M7.5 18.5h9.5a3.8 3.8 0 0 0 .4-7.6 5.4 5.4 0 0 0-10.4-1A4.3 4.3 0 0 0 7.5 18.5z',
    key: 'M8.5 15.5a3.5 3.5 0 1 1 3-5.3H21v3h-2v2.3h-2.6v-2.3h-4.9a3.5 3.5 0 0 1-3 2.3z',
    'chevron-right': 'M10 6l6 6-6 6',
    'chevron-down': 'M6 10l6 6 6-6',
    'chevron-up': 'M6 14l6-6 6 6',
    'chevron-left': 'M14 6l-6 6 6 6',
    menu: 'M4 7h16M4 12h16M4 17h16',
    close: 'M6 6l12 12M18 6L6 18',
    sun: 'M12 8.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7zM12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M5.6 18.4L7 17M17 7l1.4-1.4',
    moon: 'M19.5 14.5A7.5 7.5 0 0 1 9.5 4.5a7.5 7.5 0 1 0 10 10z',
    monitor: 'M4 5h16v11H4zM9 20h6M12 16v4',
    server: 'M5 4.5h14v6H5zM5 13.5h14v6H5zM8 7.5h.01M8 16.5h.01M12 7.5h4M12 16.5h4',
    logout: 'M14 5H5v14h9M10 12h10M17 8.5l3.5 3.5-3.5 3.5',
    search: 'M10.5 5a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11zM14.5 14.5L20 20',
    link: 'M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1.2 1.2M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1.2-1.2',
    upload: 'M12 16V5M7.5 9.5L12 5l4.5 4.5M5 19h14',
    download: 'M12 5v11M7.5 11.5L12 16l4.5-4.5M5 19h14',
    refresh: 'M19 12a7 7 0 1 1-2.1-5M19 4.5V8h-3.5',
    archive: 'M4 5h16v4H4zM5.5 9v10h13V9M10 13h4',
    queue: 'M5 6.5h14M5 12h14M5 17.5h9',
    plug: 'M9 3.5v4M15 3.5v4M7 7.5h10v3.5a5 5 0 0 1-10 0zM12 16v4.5',
    check: 'M5 12.5l4.5 4.5L19 7.5',
    alert: 'M12 4l9 15.5H3zM12 10v4M12 16.8v.2',
    info: 'M12 3.5a8.5 8.5 0 1 0 0 17 8.5 8.5 0 0 0 0-17zM12 11v5.5M12 7.8v.2',
    lock: 'M6.5 10.5h11v9h-11zM8.5 10.5V8a3.5 3.5 0 0 1 7 0v2.5',
    retry: 'M5 12a7 7 0 1 0 2.1-5M5 4.5V8h3.5',
    hosters: 'M4 6h16v5H4zM4 13h16v5H4zM7 8.5h.2M7 15.5h.2',
    file: 'M6 3.5h8l4 4V20.5H6zM14 3.5V7.5h4',
    more: 'M5 12h.01M12 12h.01M19 12h.01',
    up: 'M12 19V5M6.5 10.5L12 5l5.5 5.5',
  // File kinds, as the desktop list's glyphs group them (JobViewModel.GlyphFor).
  document: 'M6 3.5h8l4 4V20.5H6zM14 3.5V7.5h4M9 12h6M9 15h6M9 18h4',
  music: 'M9 17.5V6l10-2v11.5M9 17.5a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0zM19 15.5a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0z',
  video: 'M3.5 6.5h12v11h-12zM15.5 10.5l5-3v9l-5-3',
  program: 'M3.5 5h17v14h-17zM3.5 8.5h17M7 12.5l2.5 2-2.5 2M12 16.5h4',
  image: 'M4 5h16v14H4zM4 16l4.5-4.5 3.5 3.5 2.5-2.5L20 17.5M15.5 8.5h.01',
  disc: 'M12 3.5a8.5 8.5 0 1 0 0 17 8.5 8.5 0 0 0 0-17zM12 10a2 2 0 1 0 0 4 2 2 0 0 0 0-4z',
  'archive-file': 'M6 3.5h12v17H6zM11 3.5v2h2v2h-2v2h2v2h-2M10.5 14h3v3h-3z',
  // T1: three joined nodes, the glyph of the desktop list's swarm rows. Comments here are bundled into the browser
  // extension, whose build carries no word of what those rows are (rule 5; the extension's guard test checks it).
  swarm: 'M12 3.5a2.3 2.3 0 1 0 0 4.6 2.3 2.3 0 0 0 0-4.6zM5.5 15.4a2.3 2.3 0 1 0 0 4.6 2.3 2.3 0 0 0 0-4.6zM18.5 15.4a2.3 2.3 0 1 0 0 4.6 2.3 2.3 0 0 0 0-4.6zM10.9 8l-4.3 7.5M13.1 8l4.3 7.5M7.8 17.7h8.4',
};

export type IconName = keyof typeof iconPaths;

/** The file-kind glyph for a file name, in the desktop list's groups (JobViewModel.GlyphFor); "file" otherwise. */
export function fileKindIcon(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  const extension = dot >= 0 ? fileName.slice(dot + 1).toLowerCase() : '';
  if (['zip', '7z', 'rar', 'gz', 'gzip', 'bz2', 'tar', 'xz', 'zst'].includes(extension)) return 'archive-file';
  if (['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'txt', 'rtf', 'epub'].includes(extension)) return 'document';
  if (['mp3', 'flac', 'wav', 'm4a', 'aac', 'ogg', 'wma', 'opus'].includes(extension)) return 'music';
  if (['mp4', 'mkv', 'avi', 'mov', 'wmv', 'webm', 'm4v', 'mpg', 'mpeg'].includes(extension)) return 'video';
  if (['exe', 'msi', 'apk', 'msix', 'appx', 'bat', 'cmd'].includes(extension)) return 'program';
  if (['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg', 'ico'].includes(extension)) return 'image';
  if (['iso', 'img'].includes(extension)) return 'disc';
  return 'file';
}
