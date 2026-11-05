type BrowserDownloads = Pick<typeof chrome.downloads, "cancel" | "erase" | "search">;

/** Called only after the native Host durably accepts the transfer. */
export async function removeAcceptedBrowserDownload(
  downloads: BrowserDownloads,
  id: number,
  settle: () => Promise<void> = () => new Promise(resolve => setTimeout(resolve, 100)),
): Promise<boolean> {
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      // Never erase an in-progress entry if cancellation failed: erasing history
      // does not cancel the underlying transfer.
      await downloads.cancel(id);
      const remaining = await downloads.search({ id });
      if (remaining.length === 0) return true;
      if (remaining.some(item => item.state === "in_progress")) {
        await settle();
        continue;
      }
      await downloads.erase({ id });
      if ((await downloads.search({ id })).length === 0) return true;
    } catch {
      // Transient browser failures are retried; never resume after native ACK.
    }
    if (attempt < 2) await settle();
  }
  return false;
}
