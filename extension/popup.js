const target = document.querySelector('#state');
chrome.storage.local.get(['connected','lastSeen','lastError']).then(s => {
  target.textContent = s.connected && Date.now() - (s.lastSeen ?? 0) < 8000 ? '● Приложение на связи' : (s.lastError || 'Откройте Family Time и подключите расширение в настройках.');
});
