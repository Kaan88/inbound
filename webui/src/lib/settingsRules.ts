// Rules of the Options screen that do not need a page: what the desktop's Options window checks before it saves
// (MainWindow.xaml.cs, the window.Save handler) and the small conversions it makes.
import type { SiteLogin } from './types';

/**
 * A saved password belongs to a site and user name. When either changed and no new password was typed, the server could
 * not keep the old one, so the form asks for it again (the desktop's wording and rule: site compared without regard to
 * case, user name exactly).
 */
export function loginNeedsPassword(original: Pick<SiteLogin, 'site' | 'username' | 'hasPassword'> | undefined, site: string, username: string, password: string | undefined): boolean {
  if (!original || !original.hasPassword || password) return false;
  return original.site.toLowerCase() !== site.trim().toLowerCase() || original.username !== username.trim();
}

export const loginPasswordMessage = 'Re-enter the password for a login whose site or user name changed.';
