// Theme (dark default, remembered per browser) and preview windows. Storage may be unavailable; the UI works without it.
window.c2x = {
  toggleTheme() {
    const next = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem('c2x-theme', next); } catch { }
  },
  openPreview(url) {
    // a named window per preview would pile up; one tab per click is what a user expects from "open in new window"
    const w = window.open(url, '_blank');
    return w !== null; // false: blocked by the browser's pop-up blocker
  },
};
