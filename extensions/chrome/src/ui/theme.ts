// The extension pages' theme (EXT.R8): Match the browser (default), Light or Dark. The setting lives in
// chrome.storage.local with the other settings and is mirrored to localStorage["inbound.theme"], the key the web UI's
// theme.js reads, so that file (copied into the build) paints the first frame right; MV3 allows no inline script.

export type ThemeChoice = "system" | "light" | "dark";

export const THEME_KEY = "inbound.theme";

/** Puts a theme on this page and remembers it for the next page's first paint. */
export function applyTheme(choice: ThemeChoice | undefined): void {
  const theme = choice === "light" || choice === "dark" ? choice : "system";
  const root = document.documentElement;
  if (theme === "system") delete root.dataset.theme;
  else root.dataset.theme = theme;
  try {
    if (theme === "system") localStorage.removeItem(THEME_KEY);
    else localStorage.setItem(THEME_KEY, theme);
  } catch { /* storage blocked: this page still has it */ }
}

/** Follows a change made on another page (Options) while this one is open. */
export function followThemeChanges(): void {
  try {
    chrome.storage?.onChanged?.addListener((changes, area) => {
      if (area !== "local" || !changes.settings) return;
      applyTheme((changes.settings.newValue as { theme?: ThemeChoice } | undefined)?.theme);
    });
  } catch { /* a page without storage events keeps its theme */ }
}
