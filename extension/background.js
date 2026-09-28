import { summarize } from './state.js';
const HOST = 'org.familytime.activity';
let port = null, sequence = 0, sending = false, lastError = '', connected = false;
const instanceId = crypto.randomUUID();
const media = new Map();
function connect() {
  if (port) return;
  try {
    port = chrome.runtime.connectNative(HOST);
    port.onMessage.addListener(message => {
      connected = message?.connected === true;
      lastError = connected ? '' : 'Запустите приложение Family Time на этом компьютере.';
      void chrome.storage.local.set({ connected, lastError, lastSeen: Date.now() });
    });
    port.onDisconnect.addListener(() => {
      // Read lastError to avoid leaking unhandled runtime messages. Do not store URLs.
      void chrome.runtime.lastError;
      port = null; connected = false;
      lastError = 'Связь с приложением отсутствует. Проверьте установку и запустите Family Time.';
      void chrome.storage.local.set({ connected, lastError, lastSeen: Date.now() });
      setTimeout(connect, 5000);
    });
  } catch { port = null; connected = false; }
}
async function publish() {
  if (sending) return;
  sending = true;
  try {
    connect();
    const tabs = await chrome.tabs.query({});
    const windows = await chrome.windows.getAll();
    const focused = windows.find(w => w.focused && w.type === 'normal' && !w.incognito);
    const selected = focused ? tabs.find(t => t.active && t.windowId === focused.id)?.id : undefined;
    const state = summarize(tabs, focused?.id, selected, media, Date.now());
    const browser = /Edg\//.test(navigator.userAgent) ? 'msedge' : 'chrome';
    port?.postMessage({ version: 1, browser, instanceId, sequence: ++sequence, ...state });
    const ids = new Set(tabs.map(t => t.id));
    for (const [id, entry] of media) if (!ids.has(id) || Date.now() - entry.received > 30000) media.delete(id);
  } catch { connected = false; }
  finally { sending = false; }
}
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === 'status' && !sender.tab) {
    sendResponse({ connected, lastError }); return;
  }
  if (message?.type !== 'media' || !sender.tab || sender.tab.incognito || sender.frameId !== 0) return;
  try {
    const u = new URL(sender.url);
    if (u.protocol !== 'https:' || !['youtube.com','www.youtube.com','m.youtube.com'].includes(u.hostname)) return;
    if (typeof message.playing !== 'boolean') return;
    media.set(sender.tab.id, { playing: message.playing, received: Date.now() });
    void publish();
  } catch { }
});
chrome.tabs.onActivated.addListener(() => void publish());
chrome.tabs.onUpdated.addListener((id, change) => {
  if (change.url) media.delete(id);
  if (change.url || change.status === 'complete') void publish();
});
chrome.tabs.onRemoved.addListener(id => { media.delete(id); void publish(); });
chrome.windows.onFocusChanged.addListener(() => void publish());
chrome.alarms.onAlarm.addListener(() => { connect(); void publish(); });
chrome.runtime.onStartup.addListener(() => { connect(); void publish(); });
chrome.runtime.onInstalled.addListener(() => { connect(); void publish(); });
chrome.alarms.create('reconnect', { periodInMinutes: 1 });
setInterval(() => void publish(), 2000);
connect(); void publish();
