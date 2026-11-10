(() => {
  if (!window.chrome?.webview) return;
  const root = document.documentElement;
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  const activeAnimations = new Set();
  let scene;
  let settled = false;
  let revealCount = 0;
  root.dataset.startupPhase = 'waiting';

  function host(method) {
    try {
      window.chrome.webview.postMessage({
        id: 'startup-' + method, method, deadlineUtcMs: Date.now() + 90000,
        params: { preference: root.dataset.themePreference || 'system', locale: root.lang || 'zh-CN' },
      });
    } catch { /* The local interface remains usable if the host closes first. */ }
  }

  function animate(element, frames, options) {
    if (!element?.isConnected || !element.animate) return;
    const animation = element.animate(frames, options);
    activeAnimations.add(animation);
    animation.finished.catch(() => {}).finally(() => activeAnimations.delete(animation));
  }

  function reveal(content) {
    revealCount++;
    // Hardware is already rendered and operable. Motion never gates readiness,
    // capability reads, focus, or a user's first interaction.
    requestAnimationFrame(() => {
      if (!content?.isConnected || document.hidden) return;
      const surface = content.querySelector('.screen-options, .native-map, .color-devices');
      const picker = content.querySelector('.adaptive-device-picker');
      const visible = element => element && element.getClientRects().length && getComputedStyle(element).display !== 'none';
      const anchor = visible(surface) ? surface : visible(picker) ? picker : content;
      if (reducedMotion.matches) {
        animate(anchor, [{ opacity: .72 }, { opacity: 1 }], { duration: 100, easing: 'ease-out' });
        return;
      }
      // The stable list container keeps the entrance continuous if hardware
      // updates replace individual cards. Children never become invisible.
      animate(anchor, [{ opacity: .55, transform: 'translateY(6px)' }, { opacity: 1, transform: 'translateY(0)' }],
        { duration: 340, easing: 'cubic-bezier(.16,1,.3,1)' });
      const cards = [...anchor.querySelectorAll('[data-native-device], .native-monitor')];
      cards.forEach((card, index) => animate(card,
        [{ opacity: .7, transform: 'scale(.975)' }, { opacity: 1, transform: 'scale(1)' }],
        { duration: 260, delay: Math.min(index * 35, 105), easing: 'cubic-bezier(.16,1,.3,1)' }));
    });
  }

  function render({ hasSnapshot, hasDisplays, error, content }) {
    if (!hasSnapshot && !error) return;
    root.dataset.startupPhase = error ? 'error' : hasDisplays ? 'ready' : 'empty';
    if (settled) return;
    settled = true;
    scene?.remove();
    scene = null;
    if (hasDisplays && !error) reveal(content);
  }

  function stopAnimations() {
    for (const animation of activeAnimations) animation.cancel();
    activeAnimations.clear();
  }
  function visibility() {
    root.toggleAttribute('data-startup-paused', document.hidden);
    if (document.hidden) stopAnimations();
  }

  window.monitorStartup = { render, get revealCount() { return revealCount; } };
  document.addEventListener('DOMContentLoaded', () => {
    if (!settled) {
      scene = document.createElement('div');
      scene.id = 'startup-scene';
      scene.setAttribute('aria-hidden', 'true');
      scene.innerHTML = '<div class="startup-monitor"><div class="startup-monitor-bezel"><div class="startup-monitor-screen"><span class="startup-monitor-scan"></span></div></div><div class="startup-monitor-neck"></div><div class="startup-monitor-foot"></div></div>';
      document.querySelector('.page')?.append(scene);
    }
    visibility();
    // Let the themed HTML shell paint before retiring the native startup art.
    requestAnimationFrame(() => requestAnimationFrame(() => host('shellReady')));
  }, { once: true });
  document.addEventListener('change', event => { if (['ui-theme', 'ui-language'].includes(event.target?.id)) host('appearance'); });
  document.addEventListener('visibilitychange', visibility);
  reducedMotion.addEventListener('change', () => { if (reducedMotion.matches) stopAnimations(); });
})();
