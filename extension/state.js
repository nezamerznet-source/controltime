export function normalizeDomain(raw) {
  try { const u = new URL(raw); return ['http:', 'https:'].includes(u.protocol) ? u.hostname.toLowerCase().replace(/^www\./, '') : ''; }
  catch { return ''; }
}
export function playingNow(sample, now) {
  return !!sample && sample.playing === true && now - sample.received >= 0 && now - sample.received < 6500;
}
export function summarize(tabs, focusedWindow, selected, media, now) {
  // Incognito and inaccessible tabs are deliberately omitted.
  const visible = tabs.filter(t => !t.incognito);
  const foreground = visible.find(t => t.id === selected && t.windowId === focusedWindow);
  return {
    focused: !!foreground,
    domain: foreground ? normalizeDomain(foreground.url ?? '') : '',
    foregroundPlaying: !!foreground && playingNow(media.get(foreground.id), now),
    backgroundPlaying: visible.some(t => t.id !== foreground?.id && playingNow(media.get(t.id), now))
  };
}
