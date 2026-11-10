(() => {
  const i18n = window.monitorI18n;
  if (window.chrome?.webview) {
    i18n.register(multiMessages);
    return;
  }
  const t = (key, params) => i18n.t(key, params);
  const stage = document.querySelector('#arrangement-stage');
  const identify = document.querySelector('#identify');
  const mode = document.querySelector('#display-mode');
  const primary = document.querySelector('#primary-display');
  const position = document.querySelector('#display-position');
  const positionLabel = document.querySelector('#position-label');
  const arrangementHelp = document.querySelector('#arrangement-help');
  const arrangementNote = document.querySelector('#arrangement-note');
  const modeNote = document.querySelector('#mode-note');
  const primaryNote = document.querySelector('#primary-note');
  const status = document.querySelector('#arrangement-status');
  const devices = [...stage.querySelectorAll('.arrangement-device')];
  const opposites = { left: 'right', right: 'left', top: 'bottom', bottom: 'top' };
  const arrowPositions = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'top', ArrowDown: 'bottom' };
  const singleDisplay = { builtin: '1', external: '2' };
  let builtInPosition = 'left';
  let selected = '1';
  let identifyTimer;
  let drag;
  let announcement;

  const deviceName = id => t(id === '1' ? 'builtin' : 'external');
  const deviceLabel = id => `${id} · ${deviceName(id)}`;
  const otherDevice = id => id === '1' ? '2' : '1';
  const relativePosition = id => id === '1' ? builtInPosition : opposites[builtInPosition];

  function describePlacement(id, direction) {
    return t(`multi.placement.${direction}`, { name: deviceName(id), other: deviceName(otherDevice(id)) });
  }

  function announce(message) {
    announcement = message;
    status.textContent = message();
  }

  function updateArrangementNote() {
    arrangementNote.toggleAttribute('data-preview', Boolean(drag?.moved));
    if (drag?.moved) {
      arrangementNote.textContent = drag.direction
        ? t('multi.preview', { placement: describePlacement(drag.id, drag.direction) })
        : t('multi.moveToSide');
      return;
    }
    arrangementNote.textContent = mode.value === 'extend'
      ? describePlacement(selected, relativePosition(selected))
      : t(`multi.detail.${mode.value}`);
  }

  function render() {
    const isExtended = mode.value === 'extend';
    const single = singleDisplay[mode.value];
    const activeSelection = single || selected;
    stage.dataset.mode = mode.value;
    stage.dataset.position = builtInPosition;
    position.disabled = !isExtended;
    position.value = relativePosition(activeSelection);
    positionLabel.textContent = t('multi.positionLabel', { name: deviceLabel(activeSelection) });
    primary.disabled = !isExtended;
    arrangementHelp.textContent = t(isExtended ? 'multi.dragHelp' : 'multi.arrangementUnavailable');
    modeNote.textContent = t(`multi.detail.${mode.value}`);
    primaryNote.textContent = t(isExtended ? 'multi.primaryHelp' : 'multi.primaryUnavailable');
    updateArrangementNote();

    for (const device of devices) {
      const id = device.dataset.device;
      const button = device.querySelector('button');
      const orientation = document.querySelector(id === '1' ? '#builtin-orientation' : '#external-orientation');
      const orientationNote = document.getElementById(orientation.getAttribute('aria-describedby'));
      const isPrimary = isExtended && primary.value === id;
      const isInactive = Boolean(single && single !== id);
      device.hidden = isInactive;
      button.setAttribute('aria-pressed', String(activeSelection === id));
      button.setAttribute('aria-label', isPrimary ? t('multi.primaryAria', { name: deviceLabel(id) }) : deviceLabel(id));
      if (isExtended) button.setAttribute('aria-keyshortcuts', 'ArrowUp ArrowDown ArrowLeft ArrowRight');
      else button.removeAttribute('aria-keyshortcuts');
      device.querySelector('.device-primary').hidden = !isPrimary;
      primary.querySelector(`[value="${id}"]`).textContent = deviceLabel(id);
      orientation.labels[0].textContent = deviceLabel(id);
      orientation.disabled = isInactive;
      orientationNote.hidden = !isInactive;
      orientationNote.textContent = isInactive ? t('multi.inactive') : '';
      device.dataset.orientation = orientation.value;
    }

    if (announcement) status.textContent = announcement();
  }

  function placeDevice(id, direction) {
    selected = id;
    builtInPosition = id === '1' ? direction : opposites[direction];
    render();
    announce(() => describePlacement(id, direction));
  }

  function beginDrag(event) {
    if (drag || mode.value !== 'extend' || event.button !== 0 || !event.isPrimary) return;
    const button = event.currentTarget;
    const source = button.closest('.arrangement-device');
    const id = source.dataset.device;
    const target = devices.find(device => device !== source);
    drag = {
      pointerId: event.pointerId,
      id,
      button,
      source,
      target,
      bounds: stage.getBoundingClientRect(),
      sourceBounds: source.getBoundingClientRect(),
      screen: source.querySelector('.device-screen').getBoundingClientRect(),
      targetScreen: target.querySelector('.device-screen').getBoundingClientRect(),
      startX: event.clientX,
      startY: event.clientY,
      moved: false,
      direction: null
    };
    button.setPointerCapture(event.pointerId);
  }

  function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
  }

  function moveDrag(event) {
    if (drag?.pointerId !== event.pointerId) return;
    const { bounds, sourceBounds, screen, targetScreen, source, target, startX, startY } = drag;
    const deltaX = event.clientX - startX;
    const deltaY = event.clientY - startY;
    if (!drag.moved && Math.hypot(deltaX, deltaY) < 5) return;
    if (!drag.moved) {
      drag.moved = true;
      selected = drag.id;
      stage.setAttribute('data-dragging', '');
      source.setAttribute('data-dragging', '');
      render();
    }

    const x = clamp(deltaX, bounds.left + 5 - sourceBounds.left, bounds.right - 5 - sourceBounds.right);
    const y = clamp(deltaY, bounds.top + 5 - sourceBounds.top, bounds.bottom - 5 - sourceBounds.bottom);
    source.style.setProperty('--drag-x', `${x}px`);
    source.style.setProperty('--drag-y', `${y}px`);

    const offsetX = screen.left + screen.width / 2 + deltaX - (targetScreen.left + targetScreen.width / 2);
    const offsetY = screen.top + screen.height / 2 + deltaY - (targetScreen.top + targetScreen.height / 2);
    const outside = event.clientX < bounds.left || event.clientX > bounds.right ||
      event.clientY < bounds.top || event.clientY > bounds.bottom;

    let direction = null;
    if (!outside && Math.hypot(offsetX, offsetY) >= 10) {
      if (Math.abs(offsetX) >= Math.abs(offsetY)) direction = offsetX < 0 ? 'left' : 'right';
      else direction = offsetY < 0 ? 'top' : 'bottom';
    }
    drag.direction = direction;
    if (direction) target.dataset.snap = direction;
    else target.removeAttribute('data-snap');
    updateArrangementNote();
  }

  function endDrag(cancel = false) {
    if (!drag) return;
    const { pointerId, id, button, source, target, moved, direction } = drag;
    drag = undefined;
    if (button.hasPointerCapture(pointerId)) button.releasePointerCapture(pointerId);
    if (!moved) return;
    stage.removeAttribute('data-dragging');
    source.removeAttribute('data-dragging');
    source.style.removeProperty('--drag-x');
    source.style.removeProperty('--drag-y');
    target.removeAttribute('data-snap');
    if (direction && !cancel) placeDevice(id, direction);
    else {
      render();
      announce(() => t('multi.dragCancelled'));
    }
  }

  for (const device of devices) {
    const id = device.dataset.device;
    const button = device.querySelector('button');
    const orientation = document.querySelector(id === '1' ? '#builtin-orientation' : '#external-orientation');
    button.addEventListener('click', () => {
      if (singleDisplay[mode.value]) return;
      selected = id;
      render();
    });
    button.addEventListener('keydown', event => {
      const direction = arrowPositions[event.key];
      if (!direction || mode.value !== 'extend') return;
      event.preventDefault();
      endDrag(true);
      placeDevice(id, direction);
    });
    button.addEventListener('pointerdown', beginDrag);
    button.addEventListener('pointermove', moveDrag);
    button.addEventListener('pointerup', event => {
      if (drag?.pointerId !== event.pointerId) return;
      if (drag.moved) moveDrag(event);
      endDrag();
    });
    for (const type of ['pointercancel', 'lostpointercapture']) {
      button.addEventListener(type, event => {
        if (drag?.pointerId === event.pointerId) endDrag(true);
      });
    }
    orientation.addEventListener('change', () => {
      render();
      announce(() => t('multi.orientationChanged', { name: deviceName(id), direction: t(`multi.${orientation.value}`) }));
    });
  }

  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && drag) {
      event.preventDefault();
      endDrag(true);
    }
  });
  for (const type of ['blur', 'resize']) window.addEventListener(type, () => endDrag(true));

  identify.addEventListener('click', () => {
    window.clearTimeout(identifyTimer);
    stage.setAttribute('data-identifying', '');
    announce(() => {
      const single = singleDisplay[mode.value];
      if (single) return t('multi.identifyOne', { name: deviceLabel(single) });
      return t('multi.identifyBoth', { builtin: deviceLabel('1'), external: deviceLabel('2') });
    });
    identifyTimer = window.setTimeout(() => stage.removeAttribute('data-identifying'), 2500);
  });

  position.addEventListener('change', () => placeDevice(selected, position.value));
  mode.addEventListener('change', () => {
    endDrag(true);
    render();
    announce(() => t(`multi.detail.${mode.value}`));
  });
  primary.addEventListener('change', () => {
    render();
    announce(() => t('multi.primaryChanged', { name: deviceName(primary.value) }));
  });

  i18n.register(multiMessages, render);
})();
