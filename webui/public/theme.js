// Applies the theme chosen on this browser before the page draws, so a dark page doesn't flash light.
// A file of its own because the server's content policy allows no inline script.
try {
  var theme = localStorage.getItem('inbound.theme');
  if (theme === 'dark' || theme === 'light') document.documentElement.dataset.theme = theme;
} catch (e) {
  // Storage blocked: follow the system.
}
