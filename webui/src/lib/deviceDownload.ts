// "To this device" in the Sites screens (SITES.11 R6a): the one place a file is handed to the browser to save. A link
// with the download attribute keeps the tab where it is even when the server answers with an error, which the browser
// then shows as a failed download. Called only from the Download dialog's device choice (tests/sites-browse.test.ts).
export function startDeviceDownload(url: string, name: string): void {
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.rel = 'noopener';
  document.body.appendChild(link);
  link.click();
  link.remove();
}
