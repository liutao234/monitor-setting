(() => {
  if (CSS.supports('corner-shape: squircle') ||
      !CSS.supports("clip-path: path('M 0 0 L 1 1 Z')") ||
      typeof ResizeObserver === 'undefined') return;

  const selector = [
    '.topbar-inner', '.panel', '.tabs', '.tabs a', '.brand-mark', '.language-picker select', '#ui-theme',
    '.button', '.field select', '.bezel', '.screen-stage', '.device-icon', '.selected-label',
    '.connection-state', '.brightness-label output', '.arrangement-stage', '.device-screen',
    '.device-select', '.config-row select', '.position-control select', '.device-primary', '.device-number',
    '.color-devices', '.color-device', '.color-device-id', '.color-frame', '.sample-board', '.temperature-preset',
    '.color-value', '.temperature-number', '.support-label', '.hdr-switch', '.color-select', '.motion-track',
    '.motion-marker', '.native-monitor', '.native-temperature-option', '.adaptive-sections button', '.adaptive-device-picker',
    '.startup-monitor-bezel', '.startup-monitor-screen',
  ].join(',');
  const cornerProperties = [
    'borderTopLeftRadius', 'borderTopRightRadius', 'borderBottomRightRadius', 'borderBottomLeftRadius',
  ];
  const diagonal = 1 - 2 ** (-1 / 4);

  function length(value, reference) {
    if (value.endsWith('%')) return Number.parseFloat(value) * reference / 100;
    if (value.endsWith('px') || value === '0') return Number.parseFloat(value);
    return NaN;
  }

  function radiiFor(element, width, height) {
    element.removeAttribute('data-smooth-corner');
    const style = getComputedStyle(element);
    const radii = cornerProperties.map((property) => {
      const [horizontal, vertical = horizontal] = style[property].split(/\s+/);
      const x = length(horizontal, width);
      const y = length(vertical, height);
      return x === 0 || y === 0 ? [0, 0] : [x, y];
    });
    const [topLeft, topRight, bottomRight, bottomLeft] = radii;
    const scale = Math.min(
      1,
      width / (topLeft[0] + topRight[0]),
      width / (bottomLeft[0] + bottomRight[0]),
      height / (topLeft[1] + bottomLeft[1]),
      height / (topRight[1] + bottomRight[1]),
    );
    if (!Number.isFinite(scale) || !radii.some(([x, y]) => x && y)) return null;
    return radii.map(([x, y]) => [x * scale, y * scale]);
  }

  function smoothPath(width, height, radii) {
    const [[tlX, tlY], [trX, trY], [brX, brY], [blX, blY]] = radii;
    const corners = [
      [width, 0, -trX, 0, 0, trY],
      [width, height, 0, -brY, -brX, 0],
      [0, height, blX, 0, 0, -blY],
      [0, 0, 0, tlY, tlX, 0],
    ];
    const path = [`M ${tlX} 0`];
    for (const [x, y, startX, startY, endX, endY] of corners) {
      const point = (start, end) => {
        const xPosition = x + startX * start + endX * end;
        const yPosition = y + startY * start + endY * end;
        return `${Number(xPosition.toFixed(4))} ${Number(yPosition.toFixed(4))}`;
      };
      path.push(`L ${point(1, 0)}`);
      if (!(startX || startY) || !(endX || endY)) continue;

      // Collinear controls give zero endpoint curvature. The mirrored segments match
      // velocity and acceleration where they meet on the superellipse diagonal.
      path.push(`C ${point(4 * diagonal, 0)} ${point(2 * diagonal, 0)} ${point(diagonal, diagonal)}`);
      path.push(`C ${point(0, 2 * diagonal)} ${point(0, 4 * diagonal)} ${point(0, 1)}`);
    }
    return `path('${path.join(' ')} Z')`;
  }

  function update(element, width, height) {
    if (width <= 0 || height <= 0) return;
    const radii = radiiFor(element, width, height);
    if (!radii) {
      element.style.removeProperty('--smooth-corner-path');
      return;
    }
    element.style.setProperty('--smooth-corner-path', smoothPath(width, height, radii));
    element.setAttribute('data-smooth-corner', '');
  }

  function initialize() {
    const elements = new Set();
    const sizes = new WeakMap();
    const observer = new ResizeObserver((entries) => {
      for (const entry of entries) {
        const element = entry.target;
        const size = entry.borderBoxSize?.[0] ?? entry.borderBoxSize;
        const width = size?.inlineSize ?? element.offsetWidth;
        const height = size?.blockSize ?? element.offsetHeight;
        sizes.set(element, [width, height]);
        update(element, width, height);
      }
    });
    function observe(element) {
      if (elements.has(element)) return;
      elements.add(element);
      observer.observe(element, { box: 'border-box' });
    }
    for (const element of document.querySelectorAll(selector)) observe(element);
    if (document.documentElement.hasAttribute('data-desktop')) {
      new MutationObserver((records) => {
        for (const { addedNodes } of records) {
          for (const element of addedNodes) {
            if (!(element instanceof Element) || !element.isConnected) continue;
            if (element.matches(selector)) observe(element);
            for (const child of element.querySelectorAll(selector)) observe(child);
          }
        }
        for (const element of elements) {
          if (element.isConnected) continue;
          observer.unobserve(element);
          elements.delete(element);
        }
      }).observe(document.body, { childList: true, subtree: true });
    }

    let resizeFrame = 0;
    window.addEventListener('resize', () => {
      cancelAnimationFrame(resizeFrame);
      resizeFrame = requestAnimationFrame(() => {
        for (const element of elements) {
          const [width, height] = sizes.get(element) ?? [element.offsetWidth, element.offsetHeight];
          update(element, width, height);
        }
      });
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initialize, { once: true });
  } else {
    initialize();
  }
})();
