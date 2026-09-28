// Only playback signals are sent: no titles, searches, transcript or full URL.
(() => {
  let lastVideo = null, lastPosition = 0, lastAt = performance.now();
  function sample() {
    const now = performance.now();
    const video = document.querySelector('video.html5-main-video') ?? document.querySelector('video');
    let playing = false;
    if (video && video === lastVideo && !video.paused && !video.ended && !video.seeking && video.readyState >= 2) {
      const wall = Math.max(0.001, (now - lastAt) / 1000);
      const delta = video.currentTime - lastPosition;
      // Jumping the seek bar is not elapsed watch time. A still/buffering player is not playing.
      playing = delta > 0.01 && delta <= wall * Math.max(1, video.playbackRate) + 1.5;
    }
    lastVideo = video; lastAt = now; lastPosition = video?.currentTime ?? 0;
    try { chrome.runtime.sendMessage({ type: 'media', playing }).catch(() => {}); } catch { }
  }
  setInterval(sample, 2000);
  document.addEventListener('pause', sample, true);
  document.addEventListener('ended', sample, true);
  document.addEventListener('seeking', sample, true);
  window.addEventListener('pagehide', () => { try { chrome.runtime.sendMessage({type:'media',playing:false}).catch(()=>{}); } catch {} });
  sample();
})();
