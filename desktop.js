(() => {
  const bridge = window.chrome?.webview;
  if (!bridge) return;

  const root = document.documentElement;
  let route = root.dataset.desktopRoute;
  root.dataset.desktop = '';
  window.desktopReady = false;
  window.desktopControlsReady = false;
  const requests = new Map();
  const requestPrefix = Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
  let requestNumber = 0;
  const requestCounts = window.desktopRequestCounts = {};

  function rpc(method, params = {}) {
    requestCounts[method] = (requestCounts[method] || 0) + 1;
    return new Promise((resolve, reject) => {
      const id = requestPrefix + '-' + ++requestNumber;
      const deadlineUtcMs = Date.now() + 90000;
      const timer = setTimeout(() => {
        requests.delete(id);
        reject(new Error('timeout'));
      }, 90000);
      requests.set(id, { resolve, reject, timer });
      try {
        bridge.postMessage({ id, method, params, deadlineUtcMs });
      } catch (error) {
        clearTimeout(timer);
        requests.delete(id);
        reject(error);
      }
    });
  }

  let onHostEvent = () => {};
  bridge.addEventListener('message', ({ data }) => {
    try {
      if (typeof data === 'string') data = JSON.parse(data);
    } catch {
      return;
    }
    if (!data) return;
    if (data.event) onHostEvent(data);
    const request = requests.get(data.id);
    if (!request) return;
    clearTimeout(request.timer);
    requests.delete(data.id);
    if (data.error) request.reject(new Error(data.error.code || data.error.message || data.error));
    else request.resolve(data.result);
  });

  document.addEventListener('DOMContentLoaded', initialize, { once: true });

  function initialize() {
    const i18n = window.monitorI18n;
    if (window.desktopRoutes?.catalogs) i18n.register(window.desktopRoutes.catalogs);
    const $ = (selector, parent = document) => parent.querySelector(selector);
    const $$ = (selector, parent = document) => [...parent.querySelectorAll(selector)];
    const t = (key, values) => i18n.t(key, values);
    const tr = (key, values) => t('native.' + key, values);
    const setText = (selector, text, parent = document) => {
      const element = $(selector, parent);
      if (element.textContent !== String(text)) element.textContent = text;
    };
    const setKey = (element, key) => { element.dataset.i18n = key; element.textContent = t(key); };
    const main = $('main');
    let content = $(route === 'basic' ? '.basic-layout' : route === 'color' ? '.color-panel' : '.multi-layout');
    const routeContents = new Map([[route, content]]);
    const initializedRoutes = new Set();
    const routeUrls = new Map([...document.querySelectorAll('.tabs a')].map(link => [
      new URL(link.href).pathname.match(/\/(basic|multi-display|color)\/index\.html$/)?.[1] || route,
      new URL(link.href),
    ]));
    const symbols = window.desktopRoutes?.symbols;
    if (symbols && !document.getElementById('monitor')) document.body.insertAdjacentHTML('afterbegin', symbols);
    $('.page-footer')?.remove();
    let snapshot = null;
    let busy = false;
    const localRequests = new Set();
    let refreshing = false;
    let refreshQueued = false;
    let forceRefreshQueued = false;
    let incompleteRefreshRequested = false;
    let refreshTimer = 0;
    let pending = false;
    let deadline = null;
    let recoveryError = null;
    let timerRefreshAt = 0;
    let selectedId = readStorage('display-selected');
    let message = null;
    let draft = null;
    let drag = null;
    let deferredControlsState = null;
    let multiView = null;
    // One physical write at a time, with only the newest value retained for each
    // device/control. A slow DDC/CI display must never accumulate pointer events.
    const continuous = new Map();
    const rangeBindings = new Set();
    let activeContinuous = null;
    let continuousTimer = 0;
    let lastContinuousSent = -Infinity;
    let continuousSuspended = false;
    let continuousRecovery = false;
    const orientations = { 0: 'multi.landscape', 90: 'multi.portrait', 180: 'native.landscapeFlipped', 270: 'native.portraitFlipped' };
    const directions = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'top', ArrowDown: 'bottom' };
    const modeKeys = { extend: 'multi.extend', mirror: 'multi.mirror', builtin: 'multi.onlyBuiltin', external: 'multi.onlyExternal' };

    function readStorage(key) {
      try { return sessionStorage.getItem(key); } catch { return null; }
    }

    function saveStorage(key, value) {
      try {
        if (value == null) sessionStorage.removeItem(key);
        else sessionStorage.setItem(key, value);
      } catch {
        // Selection is still retained for the lifetime of the current page.
      }
    }

    function node(tag, className, text) {
      const element = document.createElement(tag);
      if (className) element.className = className;
      if (text != null) element.textContent = text;
      return element;
    }

    function button(label, onClick, className = 'button') {
      const element = node('button', className, label);
      element.type = 'button';
      element.addEventListener('click', onClick);
      return element;
    }

    const notice = node('section', 'panel native-notice');
    const noticeText = node('p');
    noticeText.setAttribute('role', 'status');
    const retry = button('', () => { message = null; refresh(true); });
    notice.append(noticeText, retry);
    const confirmation = node('section', 'panel native-notice native-confirm');
    const confirmationText = node('p');
    confirmationText.id = 'native-confirm-title';
    confirmation.setAttribute('aria-labelledby', confirmationText.id);
    confirmationText.setAttribute('aria-live', 'off');
    const confirmationActions = node('div', 'native-actions');
    const keep = button('', () => mutate('confirm'), 'button primary');
    const revert = button('', () => mutate('revert'));
    confirmationActions.append(keep, revert);
    confirmation.append(confirmationText, confirmationActions);
    $('.tabs').after(confirmation, notice);

    const displays = () => snapshot?.displays ?? [];
    const selected = () => displays().find((display) => display.id === selectedId);
    const displayName = (display) => display.name || t(display.kind === 'builtin' ? 'builtin' : 'external');
    const connection = (display) => display.kind === 'builtin' ? t('builtinConnection') : (display.connection || t('external'));
    const screenNumber = (display) => displays().findIndex((item) => item.id === display.id) + 1;
    const screenLabel = (display) => screenNumber(display) + ' · ' + displayName(display);
    const rangeLocked = () => busy || refreshing || pending || continuousSuspended || continuousRecovery || Boolean(window.desktopLastError);
    const locked = () => rangeLocked() || Boolean(activeContinuous) || continuous.size > 0;
    const focusControl = () => {
      const active = document.activeElement;
      return active?.dataset.nativeDevice ? { device: active.dataset.nativeDevice } : active?.id ? { id: active.id } : null;
    };
    function restoreFocus(focus) {
      if (!focus || document.activeElement !== document.body) return;
      const element = focus.id ? document.getElementById(focus.id) : $$('[data-native-device]').find((item) => item.dataset.nativeDevice === focus.device);
      if (element && !element.disabled && !element.closest('[hidden]')) element.focus({ preventScroll: true });
    }

    function selectDevice(id) {
      finishRanges();
      selectedId = id;
      saveStorage('display-selected', id);
      render();
    }

    function reasonText(reason) {
      if (reason === 'loading') return '';
      const key = 'native.' + (reason || 'unsupported');
      const message = t(key);
      return message === key ? tr('unsupported') : message;
    }

    function setPending(isPending, seconds, error) {
      pending = Boolean(isPending);
      if (pending) cancelContinuous();
      recoveryError = pending ? error : null;
      if (pending) {
        if (Number.isFinite(seconds)) deadline = Date.now() + seconds * 1000;
        else deadline ??= Number(readStorage('display-pending-until')) || null;
      } else {
        deadline = null;
        timerRefreshAt = 0;
      }
      saveStorage('display-pending-until', deadline);
    }

    function snapshotSignature(state) {
      return JSON.stringify([state.mode, state.displays.map(({ id, active, primary, x, y, width, height, orientation, refreshRate, dynamicRefreshRate }) =>
        [id, active, primary, x, y, width, height, orientation, refreshRate, dynamicRefreshRate])]);
    }

    function acceptSnapshot(state, resetDraft = false) {
      if (!Array.isArray(state?.displays)) throw new Error('readbackFailed');
      // Background probes and a command can finish close together. A later
      // snapshot must never be replaced by an older command/event response.
      if (Number.isFinite(state.revision) && Number.isFinite(snapshot?.revision) && state.revision < snapshot.revision) return false;
      if (draft && snapshot && snapshotSignature(state) !== snapshotSignature(snapshot)) {
        if (!resetDraft && hasDraft() && !message?.error) message = { key: 'draftReset' };
        draft = null;
      }
      if (resetDraft) draft = null;
      if ((state.pending || state.controlsComplete === false) && snapshot) {
        for (const display of state.displays) {
          const previous = snapshot.displays.find((item) => item.id === display.id &&
            item.active === display.active && item.kind === display.kind &&
            ['devicePath', 'gdiName'].every((key) => !item[key] || !display[key] ||
              item[key].toLowerCase() === display[key].toLowerCase()));
          if (previous) display.controls = { ...previous.controls, ...display.controls };
        }
      }
      snapshot = state;
      for (const [key, entry] of continuous) {
        const display = displays().find(item => item.id === entry.displayId);
        if (!display?.active || displayIdentity(display) !== entry.identity || !control(display, entry.name).supported) {
          continuous.delete(key);
          if (activeContinuous?.entry === entry) activeContinuous.invalidated = true;
        }
      }
      window.desktopControlsReady = state.displays.every(display => !display.controlsLoading);
      window.desktopStateRevision = state.revision;
      window.desktopLastError = undefined;
      setPending(state.pending, state.seconds, state.recoveryError);
      const chosen = selected() || displays().find((display) => display.active && display.primary) ||
        displays().find((display) => display.active) || displays()[0];
      selectedId = chosen?.id ?? null;
      if (chosen) saveStorage('display-selected', chosen.id);
      if (state.controlsComplete !== false) incompleteRefreshRequested = false;
      else if (!state.backgroundControls && !pending && !incompleteRefreshRequested) {
        // A fast mutation reply may omit unqueried hardware controls. Keep known
        // capabilities until one complete refresh, without creating a read loop.
        incompleteRefreshRequested = true;
        scheduleRefresh();
      }
      return true;
    }

    function scheduleRefresh(force = false) {
      forceRefreshQueued ||= force;
      clearTimeout(refreshTimer);
      refreshTimer = setTimeout(() => refresh(), 180);
    }

    async function refresh(force = false) {
      forceRefreshQueued ||= force;
      if (pending && (recoveryError || !deadline || Date.now() < deadline)) {
        refreshQueued = true;
        return;
      }
      if (busy || refreshing || activeContinuous || continuous.size) {
        refreshQueued = true;
        return;
      }
      const focus = focusControl();
      finishDrag(true);
      clearTimeout(refreshTimer);
      refreshQueued = false;
      const forceRefresh = forceRefreshQueued;
      forceRefreshQueued = false;
      refreshing = true;
      render();
      try {
        acceptSnapshot(await rpc('displays', forceRefresh ? { forceRefresh: true } : {}));
        if (message?.key === 'readError') message = null;
      } catch (error) {
        window.desktopLastError = error.message;
        message = { key: 'readError', detail: error.message, error: true };
      } finally {
        refreshing = false;
        continuousSuspended = false;
        render();
        restoreFocus(focus);
        if (refreshQueued) {
          refreshQueued = false;
          scheduleRefresh();
        }
      }
    }

    async function mutate(method, params = {}) {
      if (busy || refreshing || activeContinuous || continuous.size || ((pending || window.desktopLastError) && !['confirm', 'revert'].includes(method))) return;
      if (method === 'control') {
        const display = displays().find((item) => item.id === params.displayId);
        if (display?.controls?.[params.control]?.value === params.value) return;
      }
      const focus = focusControl();
      busy = true;
      message = null;
      render();
      try {
        const result = await rpc(method, params);
        if (['layout', 'control', 'confirm', 'revert'].includes(method)) {
          if (method === 'layout') draft = null;
          const state = Array.isArray(result?.displays) ? result : await rpc('displays');
          // An HDR confirmation does not discard an unrelated staged arrangement.
          // Actual topology changes invalidate drafts in acceptSnapshot itself.
          acceptSnapshot(state, method === 'layout');
          if (!pending) message = { key: method === 'revert' ? 'reverted' : 'changed' };
        }
      } catch (error) {
        message = { key: 'error', detail: error.message, error: true };
        try {
          acceptSnapshot(await rpc('displays'));
        } catch (readError) {
          window.desktopLastError = readError.message;
        }
      } finally {
        busy = false;
        render();
        if (pending && ['layout', 'control'].includes(method) && !keep.disabled) keep.focus();
        else restoreFocus(focus);
        if (refreshQueued) {
          refreshQueued = false;
          scheduleRefresh();
        }
      }
    }

    const continuousKey = (displayId, name) => JSON.stringify([displayId, name]);
    function displayIdentity(display) {
      return JSON.stringify([display.kind, display.devicePath?.toLowerCase(), display.gdiName?.toLowerCase()]);
    }

    function cancelContinuous() {
      clearTimeout(continuousTimer);
      continuous.clear();
      if (activeContinuous) activeContinuous.invalidated = true;
      for (const binding of rangeBindings) {
        if (binding.displayId) binding.suppressChange = true;
        if (binding.pointerActive || binding.keyActive) binding.blocked = true;
        binding.displayId = null;
      }
    }

    function finishRange(binding) {
      if (!binding.displayId) return;
      const entry = continuous.get(continuousKey(binding.displayId, binding.name));
      binding.displayId = null;
      if (!entry) return;
      entry.released = true;
      if (entry.ackedVersion === entry.version) { continuous.delete(entry.key); render(); }
      pumpContinuous();
    }

    function finishRanges() {
      for (const binding of rangeBindings) {
        if (binding.displayId) binding.suppressChange = true;
        if (binding.pointerActive || binding.keyActive) binding.blocked = true;
        finishRange(binding);
      }
    }

    function queueContinuous(displayId, name, value, released = false) {
      if (rangeLocked() || !Number.isFinite(value)) return;
      const display = displays().find(item => item.id === displayId);
      if (!display || !control(display, name).supported) return;
      const key = continuousKey(displayId, name);
      let entry = continuous.get(key);
      const started = !entry;
      if (!entry) {
        if (Number(display.controls[name].value) === value) return;
        entry = { key, displayId, name, identity: displayIdentity(display), value, version: 1,
          sentVersion: 0, ackedVersion: 0, released };
        continuous.set(key, entry);
      } else {
        if (entry.value !== value) { entry.value = value; entry.version++; }
        entry.released = released;
      }
      message = null;
      const settled = released && entry.ackedVersion === entry.version;
      if (settled) continuous.delete(key);
      // Render once when a gesture starts to lock topology changes. Subsequent
      // pointer events update only their slider; hardware replies render safely
      // using the optimistic value until the final readback is acknowledged.
      if (started || settled) render();
      pumpContinuous();
    }

    function pumpContinuous() {
      clearTimeout(continuousTimer);
      if (activeContinuous || busy || refreshing || pending || continuousSuspended) return;
      const entry = [...continuous.values()].find(item => item.version > item.sentVersion);
      if (!entry) {
        if (!continuous.size && refreshQueued) { refreshQueued = false; scheduleRefresh(); }
        return;
      }
      const wait = entry.released ? 0 : Math.max(0, 75 - (performance.now() - lastContinuousSent));
      if (wait > 0) { continuousTimer = setTimeout(pumpContinuous, wait); return; }
      const write = { entry, version: entry.version, value: entry.value, invalidated: false };
      entry.sentVersion = write.version;
      activeContinuous = write;
      lastContinuousSent = performance.now();
      void sendContinuous(write);
    }

    async function sendContinuous(write) {
      const { entry } = write;
      try {
        const result = await rpc('control', { displayId: entry.displayId, control: entry.name, value: write.value });
        if (!write.invalidated) {
          const state = Array.isArray(result?.displays) ? result : await rpc('displays');
          if (!write.invalidated) acceptSnapshot(state);
        }
        if (continuous.get(entry.key) === entry) {
          entry.ackedVersion = write.version;
          if (entry.released && entry.version === write.version) continuous.delete(entry.key);
        }
      } catch (error) {
        if (!write.invalidated) {
          continuousRecovery = true;
          cancelContinuous();
          message = { key: 'error', detail: error.message, error: true };
          render();
          try { acceptSnapshot(await rpc('displays')); }
          catch (readError) { window.desktopLastError = readError.message; }
        }
      } finally {
        continuousRecovery = false;
        activeContinuous = null;
        render();
        pumpContinuous();
        if (!continuous.size && refreshQueued) { refreshQueued = false; scheduleRefresh(); }
      }
    }

    async function localAction(method) {
      if (localRequests.has(method) || method === 'openSettings' && pending ||
        method === 'identify' && !displays().some((display) => display.active)) return;
      localRequests.add(method);
      render();
      try {
        await rpc(method);
        if (method === 'identify') message = { key: 'identified' };
      } catch (error) {
        message = { key: 'error', detail: error.message, error: true };
      } finally {
        localRequests.delete(method);
        render();
      }
    }

    function updateConfirmation() {
      confirmation.hidden = !pending;
      if (!pending) return;
      const seconds = deadline == null ? null : Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
      confirmation.toggleAttribute('data-error', Boolean(recoveryError));
      confirmationText.textContent = recoveryError ? tr('recoveryError') : seconds === null ? tr('pending')
        : seconds === 0 ? tr('rollingBack') : tr('countdown', { seconds });
      keep.textContent = tr('keep');
      revert.textContent = tr('revert');
      keep.disabled = busy || refreshing || seconds === 0 || Boolean(recoveryError);
      revert.disabled = busy || refreshing || (seconds === 0 && !recoveryError);
      if (!recoveryError && seconds === 0 && Date.now() - timerRefreshAt > 1000) {
        timerRefreshAt = Date.now();
        scheduleRefresh();
      }
    }

    function render() {
      const focus = focusControl();
      notice.toggleAttribute('data-error', Boolean(message?.error));
      const text = message?.error ? tr(message.key) : snapshot && !displays().length ? tr('noDisplays')
        : message?.key === 'draftReset' ? tr('draftReset') : '';
      let detail = message?.detail ? t('native.' + message.detail) : '';
      const unknownDetail = detail === 'native.' + message?.detail;
      if (unknownDetail) detail = '';
      noticeText.textContent = [text, detail].filter(Boolean).join(' ');
      retry.textContent = tr('refresh');
      retry.hidden = refreshing || busy || pending || !(window.desktopLastError || snapshot && !displays().length);
      notice.hidden = !text && retry.hidden;
      updateConfirmation();
      if (!snapshot) {
        window.monitorStartup?.render({ hasSnapshot: false, hasDisplays: false, error: Boolean(message?.error), content });
        return;
      }
      content.hidden = !displays().length;
      if (displays().length) {
        if (route === 'basic') renderBasic();
        if (route === 'color') renderColor();
        if (route === 'multi-display') renderMulti();
      }
      const identify = $('#identify');
      if (identify) identify.disabled = localRequests.has('identify') || !displays().some((display) => display.active);
      root.dataset.desktopReady = '';
      window.desktopReady = true;
      window.monitorStartup?.render({ hasSnapshot: true, hasDisplays: displays().length > 0, error: Boolean(message?.error), content });
      restoreFocus(focus);
    }

    function control(display, name) {
      const capability = display.controls?.[name] ?? {};
      const physical = ['brightness', 'contrast', 'sharpness', 'temperature', 'response'].includes(name);
      const loading = display.active && (capability.reason === 'loading' || display.controlsLoading && physical);
      const optimistic = continuous.get(continuousKey(display.id, name));
      return { ...capability, ...(optimistic ? { value: optimistic.value } : {}), supported: Boolean(display.active && capability.supported && !loading),
        loading: Boolean(loading), reason: !display.active ? 'inactive' : loading ? 'loading' : capability.reason };
    }

    function fillRange(input) {
      const fraction = (Number(input.value) - Number(input.min)) / (Number(input.max) - Number(input.min));
      input.style.setProperty('--fill', (Number.isFinite(fraction) ? fraction * 100 : 0) + '%');
    }

    function setRange(input, capability) {
      input.min = capability.min ?? 0;
      input.max = capability.max ?? 100;
      input.step = capability.step ?? 1;
      if (capability.value != null) input.value = capability.value;
      else input.value = input.min;
      input.disabled = rangeLocked() || !capability.supported;
      fillRange(input);
    }

    function bindRange(selector, name, draw, suffix = '') {
      const input = $(selector);
      const binding = { input, name, displayId: null, value: null, pointerActive: false, keyActive: false, blocked: false, suppressChange: false };
      rangeBindings.add(binding);
      input.addEventListener('input', () => {
        if (!input.isConnected || input.disabled || binding.blocked || rangeLocked()) return;
        binding.suppressChange = false;
        binding.displayId ??= selectedId;
        binding.value = input.valueAsNumber;
        draw(input.value + suffix);
        fillRange(input);
        queueContinuous(binding.displayId, name, binding.value);
      });
      input.addEventListener('change', () => {
        if (!input.isConnected || input.disabled || binding.blocked || binding.suppressChange || rangeLocked()) {
          binding.suppressChange = false;
          finishRange(binding);
          return;
        }
        const displayId = binding.displayId ?? selectedId;
        const value = binding.displayId && binding.displayId !== selectedId ? binding.value : input.valueAsNumber;
        queueContinuous(displayId, name, value, true);
        binding.displayId = null;
      });
      input.addEventListener('pointerdown', () => { binding.pointerActive = true; binding.blocked = false; binding.suppressChange = false; });
      const rangeKey = event => /^(Arrow(Left|Right|Up|Down)|Home|End|PageUp|PageDown)$/.test(event.key);
      input.addEventListener('keydown', event => {
        if (!rangeKey(event)) return;
        binding.keyActive = true;
        if (!event.repeat) { binding.blocked = false; binding.suppressChange = false; }
      });
      input.addEventListener('blur', () => { if (!binding.pointerActive && !binding.keyActive) binding.blocked = false; finishRange(binding); });
      input.addEventListener('keyup', event => {
        if (!rangeKey(event)) return;
        binding.keyActive = false;
        if (!binding.pointerActive) binding.blocked = false;
        finishRange(binding);
      });
      for (const event of ['pointerup', 'pointercancel']) input.addEventListener(event, () => {
        binding.pointerActive = false; binding.blocked = false; finishRange(binding);
      });
    }

    const selectOptions = new WeakMap();
    function renderSelect(select, options, value) {
      const signature = JSON.stringify(options);
      if (selectOptions.get(select) !== signature) {
        select.replaceChildren(...options.map(({ value, label, disabled }) => {
          const option = new Option(label, value);
          option.disabled = Boolean(disabled);
          return option;
        }));
        selectOptions.set(select, signature);
      }
      const nextValue = value == null ? '' : String(value);
      if (select.value !== nextValue) select.value = nextValue;
    }

    function selectOption(value, label) { return { value, label }; }


    let basicTemplates;
    let settingsButton;
    let colorTemperatureSelect;

    function initBasic() {
      basicTemplates = [$('#display-1').cloneNode(true), $('#display-2').cloneNode(true)];
      $('#reset').hidden = true;
      $('.connection-state > span').removeAttribute('data-i18n');
      const scale = $('#scale');
      settingsButton = button(tr('settings'), () => localAction('openSettings'), 'button native-managed-button');
      settingsButton.id = 'native-open-settings';
      settingsButton.setAttribute('aria-describedby', 'native-scale-note');
      scale.labels[0].removeAttribute('for');
      const scaleNote = node('span', 'subtle native-mode-hint');
      scaleNote.id = 'native-scale-note';
      scale.replaceWith(settingsButton, scaleNote);
      const modeNote = node('p', 'subtle native-mode-hint');
      modeNote.id = 'native-format-note';
      $('.format-group').append(modeNote);
      for (const id of ['resolution', 'refresh-rate']) $('#' + id).setAttribute('aria-describedby', modeNote.id);

      bindRange('#brightness', 'brightness', (value) => {
        const capability = control(selected(), 'brightness');
        setText('#brightness-value', value + (Number(capability.min ?? 0) === 0 && Number(capability.max ?? 100) === 100 ? '%' : ''));
      });
      $('#resolution').addEventListener('change', (event) => {
        const display = selected();
        const dimensions = event.target.value;
        const modes = displayModes(display).filter((mode) => modeDimensions(mode) === dimensions);
        const candidate = modes.find((mode) => mode.refreshRate === display.refreshRate) || modes[0];
        if (candidate) mutate('layout', { displays: [{ id: display.id, width: candidate.width, height: candidate.height, refreshRate: candidate.refreshRate }] });
      });
      $('#refresh-rate').addEventListener('change', (event) => {
        const display = selected();
        const dynamic = event.target.value.startsWith('dynamic:');
        const rate = Number(dynamic ? event.target.value.slice('dynamic:'.length) : event.target.value);
        if (display && Number.isFinite(rate) && rate > 0)
          mutate('layout', { displays: [{ id: display.id, refreshRate: rate, dynamicRefreshRate: dynamic }] });
      });
    }

    function basicCard(display) {
      const template = basicTemplates[display.kind === 'builtin' ? 0 : 1];
      const card = template.cloneNode(true);
      card.removeAttribute('id');
      card.dataset.nativeDevice = display.id;
      card.setAttribute('aria-pressed', String(selectedId === display.id));
      card.setAttribute('aria-label', screenLabel(display));
      card.toggleAttribute('data-inactive', !display.active);
      const name = $('strong', card);
      name.removeAttribute('data-i18n');
      name.textContent = displayName(display);
      $('.selected-label', card).textContent = t('basic.selected');
      const detail = $('.screen-detail', card);
      detail.removeAttribute('data-i18n');
      detail.textContent = connection(display) + (!display.active ? ' · ' + tr('inactiveLabel') : display.primary ? ' · ' + t('primaryDisplay') : '');
      setText('.screen-index', screenNumber(display), card);
      card.addEventListener('click', () => selectDevice(display.id));
      return card;
    }

    function modeDimensions(mode) { return mode.width + ' × ' + mode.height; }
    function displayModes(display) {
      if (!display?.active) return [];
      const modes = (display.modes ?? []).filter((mode) => mode.orientation == null || Number(mode.orientation) === Number(display.orientation));
      return [display, ...modes].filter((mode) => mode.width > 0 && mode.height > 0 && mode.refreshRate > 0);
    }

    function renderBasic() {
      const display = selected();
      $('.screen-options').replaceChildren(...displays().map(basicCard));
      setText('.connection-state > span', tr('count', { count: displays().length }));
      setText('#identify-status', tr('identifyHint'));
      setText('#device-name', displayName(display));
      $('#device-icon').setAttribute('href', display.kind === 'builtin' ? '#laptop' : '#monitor');
      setText('#device-summary', [t('basic.number', { number: screenNumber(display) }), connection(display),
        display.active && display.width && display.height ? modeDimensions(display) : '',
        display.active && display.refreshRate ? display.refreshRate + ' Hz' : '',
        display.active ? '' : tr('inactiveLabel')].filter(Boolean).join(' · '));
      const brightness = control(display, 'brightness');
      setRange($('#brightness'), brightness);
      const brightnessUnit = Number(brightness.min ?? 0) === 0 && Number(brightness.max ?? 100) === 100 ? '%' : '';
      const valueText = brightness.loading ? '—' : brightness.supported && brightness.value != null ? brightness.value + brightnessUnit : '—';
      setText('#brightness-value', valueText);
      $('#brightness-value').dataset.loading = String(brightness.loading);
      $('#brightness').setAttribute('aria-valuetext', valueText);
      setText('#brightness-hint', brightness.supported ? tr('brightness') : reasonText(brightness.reason));
      settingsButton.textContent = tr('settings');
      settingsButton.disabled = localRequests.has('openSettings') || pending;
      $('#native-scale-note').hidden = true;

      const modes = displayModes(display);
      const dimensions = [...new Set(modes.map(modeDimensions))];
      const rates = [...new Set(modes.filter((mode) => modeDimensions(mode) === modeDimensions(display)).map((mode) => mode.refreshRate))].sort((a, b) => a - b);
      const formatReason = !display.active ? 'inactive' : snapshot.mode === 'mirror' ? 'mirrorFormat' :
        ['resolution', 'refreshRate'].map((name) => display.controls?.[name])
          .find((capability) => capability?.supported === false)?.reason;
      const resolution = $('#resolution');
      renderSelect(resolution, dimensions.map((value) => selectOption(value, value)), modeDimensions(display));
      resolution.disabled = locked() || !display.active || snapshot.mode === 'mirror' ||
        display.controls?.resolution?.supported === false || dimensions.length < 2;
      const refresh = $('#refresh-rate');
      const dynamic = control(display, 'dynamicRefreshRate');
      const dynamicEnabled = display.dynamicRefreshRate === true || dynamic.value === 1 || dynamic.value === true;
      const dynamicRates = (dynamic.options ?? []).map(option => Number(option.value)).filter(rate => Number.isFinite(rate) && rate > 0);
      if (dynamicEnabled && display.refreshRate > 0 && !dynamicRates.includes(display.refreshRate)) dynamicRates.push(display.refreshRate);
      const rateOptions = rates.map(value => selectOption(value, value + ' Hz'));
      for (const rate of dynamicRates) {
        const max = dynamicEnabled && rate === display.refreshRate && display.physicalRefreshRate > rate ? display.physicalRefreshRate : null;
        rateOptions.push({ value: 'dynamic:' + rate, label: max ? tr('dynamicRefreshRateRange', { rate, max })
          : tr('dynamicRefreshRateOption', { rate }), disabled: !dynamic.supported });
      }
      if (!dynamic.supported && !dynamicEnabled) rateOptions.push({ value: 'dynamic:unsupported', label: tr('dynamicRefreshUnsupported'), disabled: true });
      renderSelect(refresh, rateOptions, dynamicEnabled ? 'dynamic:' + display.refreshRate : display.refreshRate);
      refresh.disabled = locked() || !display.active || snapshot.mode === 'mirror' ||
        display.controls?.refreshRate?.supported === false || rates.length + (dynamic.supported ? dynamicRates.length : 0) < 2;
      refresh.title = dynamic.supported ? '' : reasonText(dynamic.reason || 'dynamicRefreshUnavailable');
      setText('#native-format-note', formatReason ? reasonText(formatReason) : tr('modeConfirm'));
    }

    function initColor() {
      colorTemperatureSelect = node('select', 'native-temperature-option');
      colorTemperatureSelect.id = 'temperature-options';
      colorTemperatureSelect.setAttribute('aria-describedby', 'temperature-hint');
      $('#temperature').after(colorTemperatureSelect);
      colorTemperatureSelect.addEventListener('change', () => {
        const display = selected();
        if (display) mutate('control', { displayId: display.id, control: 'temperature', value: Number(colorTemperatureSelect.value) });
      });
      for (const [id, suffix] of [['temperature', ' K'], ['contrast', ''], ['sharpness', '']]) {
        bindRange('#' + id, id, (value) => setText('#' + id + '-value', value), suffix);
      }
      $('#hdr').addEventListener('change', (event) => {
        if (selected()) mutate('control', { displayId: selectedId, control: 'hdr', value: Number(event.target.checked) });
      });
      $('#response').addEventListener('change', (event) => {
        const display = selected();
        if (!display) return;
        const option = control(display, 'response').options?.find((option) => String(option.value) === event.target.value);
        if (option) {
          const numeric = Number(option.value);
          mutate('control', { displayId: selectedId, control: 'response', value: Number.isFinite(numeric) ? numeric : option.value });
        }
      });
      $('.temperature-scale').hidden = true;
      $('.temperature-presets').hidden = true;
      $('.motion-preview').hidden = true;
      setKey($('.preview-head p'), 'native.sample');
    }

    function colorCard(display) {
      const card = button(null, () => selectDevice(display.id), 'color-device');
      card.dataset.nativeDevice = display.id;
      card.setAttribute('aria-pressed', String(display.id === selectedId));
      card.toggleAttribute('data-inactive', !display.active);
      const number = node('span', 'color-device-id', screenNumber(display));
      number.setAttribute('aria-hidden', 'true');
      card.append(number, node('span', '', displayName(display) + (display.active ? '' : ' · ' + tr('inactiveLabel'))));
      return card;
    }

    function renderColor() {
      const display = selected();
      $('.color-devices').replaceChildren(...displays().map(colorCard));
      setText('#color-selected', tr('selected', { name: displayName(display) + ' · ' + connection(display) }));
      const hints = { temperature: 'color.temperatureHint', contrast: 'color.contrastHint', sharpness: 'color.sharpnessHint' };
      for (const [name, hint] of Object.entries(hints)) {
        const capability = control(display, name);
        const input = $('#' + name);
        setRange(input, capability);
        const value = $('#' + name + '-value');
        const valueText = capability.loading ? '—' : capability.supported && capability.value != null ? capability.value + (name === 'temperature' ? ' K' : '') : t('notSupported');
        input.setAttribute('aria-valuetext', valueText);
        value.textContent = valueText;
        value.dataset.unavailable = String(!capability.supported);
        value.dataset.loading = String(capability.loading);
        setText('#' + name + '-hint', capability.supported ? t(hint) : reasonText(capability.reason));
      }
      const temperature = control(display, 'temperature');
      const options = temperature.options ?? [];
      const temperatureLabel = $('label.color-label[for="temperature"], label.color-label[for="temperature-options"]');
      temperatureLabel.htmlFor = options.length ? 'temperature-options' : 'temperature';
      $('#temperature').hidden = options.length > 0;
      colorTemperatureSelect.hidden = !options.length;
      colorTemperatureSelect.disabled = locked() || !temperature.supported;
      renderSelect(colorTemperatureSelect, options.map(({ value, label }) => selectOption(value, label || value + ' K')), temperature.value);
      setText('#sample-temperature', temperature.supported && temperature.value != null ? t('color.whitePoint', { value: temperature.value + ' K' }) : '');
      const hdr = control(display, 'hdr');
      const hdrInput = $('#hdr');
      hdrInput.disabled = locked() || !hdr.supported;
      hdrInput.checked = hdr.value === true || hdr.value === 1;
      setText('#hdr-hint', hdr.supported ? tr('hdr') : reasonText(hdr.reason));
      setText('#hdr-status', hdr.loading ? '—' : hdr.supported ? tr(hdrInput.checked ? 'on' : 'off') : t('notSupported'));
      $('#hdr-status').dataset.loading = String(hdr.loading);
      setText('#sample-dynamic-range', hdr.supported ? 'HDR · ' + tr(hdrInput.checked ? 'on' : 'off') : '');

      const response = control(display, 'response');
      const responseSelect = $('#response');
      const responseOptions = response.options ?? [];
      renderSelect(responseSelect, responseOptions.map(({ value, label }) => {
        const key = 'color.' + value;
        const translation = t(key);
        return selectOption(value, translation !== key ? translation : (label ?? value));
      }), response.value);
      responseSelect.disabled = locked() || !response.supported || !responseOptions.length;
      $('#response-unsupported').removeAttribute('data-i18n');
      setText('#response-unsupported', response.loading ? '—' : t('notSupported'));
      $('#response-unsupported').dataset.loading = String(response.loading);
      $('#response-unsupported').hidden = response.supported;
      setText('#response-hint', response.supported ? t('color.responseHint') : reasonText(response.reason));
    }


    function modeValue() {
      const active = displays().filter((display) => display.active);
      if (active.length === 1 && displays().filter((display) => display.kind === active[0].kind).length > 1)
        return 'solo:' + active[0].id;
      return snapshot.mode;
    }

    function currentDraft() {
      draft ??= { mode: modeValue(), primaryId: displays().find((display) => display.primary)?.id ?? null, changes: new Map(), snaps: new Map() };
      return draft;
    }

    function hasDraft() {
      return draft && (draft.mode !== modeValue() || draft.changes.size > 0 ||
        (draft.primaryId ?? null) !== (displays().find((display) => display.primary)?.id ?? null));
    }

    function unavailableMode(mode) {
      const capability = snapshot.modeCapabilities?.[mode];
      return capability?.supported === false ? capability : null;
    }

    function activeGeometry() {
      const changes = currentDraft().changes;
      return displays().filter((display) => display.active && display.width > 0 && display.height > 0).map((display) => {
        const change = changes.get(display.id) ?? {};
        const orientation = change.orientation ?? display.orientation;
        const rotate = (orientation % 180) !== (display.orientation % 180);
        return { ...display, ...change, orientation, width: rotate ? display.height : display.width, height: rotate ? display.width : display.height };
      });
    }

    function geometryLocked() {
      return locked() || currentDraft().mode !== modeValue() || snapshot.mode !== 'extend' || activeGeometry().length < 2;
    }

    function editDisplay(id, values) {
      const display = displays().find((item) => item.id === id);
      const changes = currentDraft().changes;
      const change = { ...changes.get(id), ...values };
      for (const key of Object.keys(change)) if (change[key] === display[key]) delete change[key];
      if (Object.keys(change).length) changes.set(id, change);
      else changes.delete(id);
    }

    function applyDraft() {
      if (locked() || !hasDraft() || (draft.mode !== modeValue() && unavailableMode(draft.mode))) return;
      const changes = [...draft.changes].map(([id, change]) => ({ id, ...change }));
      const params = { displays: changes };
      if (draft.mode !== modeValue()) {
        if (draft.mode.startsWith('solo:')) {
          params.mode = 'solo';
          params.soloId = draft.mode.slice(5);
        } else {
          params.mode = draft.mode;
        }
      }
      const primaryId = displays().find((display) => display.primary)?.id;
      if (draft.mode === 'extend' && draft.primaryId && draft.primaryId !== primaryId) params.primaryId = draft.primaryId;
      mutate('layout', params);
    }

    function initMulti() {
      const stage = $('#arrangement-stage');
      const map = node('div', 'native-map');
      const inactive = node('div', 'native-inactive-list');
      stage.replaceChildren(map, inactive);
      const position = $('#display-position');
      const reference = node('select');
      reference.id = 'native-position-target';
      const referenceLabel = node('label');
      referenceLabel.htmlFor = reference.id;
      const positionRow = $('.position-control');
      positionRow.before(node('div', 'native-layout-actions'));
      $('.native-layout-actions').append(
        button(tr('apply'), applyDraft, 'button primary'),
        button(tr('discard'), () => { draft = null; render(); }),
      );
      const [apply, discard] = $$('.native-layout-actions button');
      apply.id = 'native-apply';
      discard.id = 'native-discard';
      const referenceRow = node('div', 'position-control native-reference');
      referenceRow.append(referenceLabel, reference);
      positionRow.before(referenceRow);
      const orientationPanel = $('#orientation-heading').closest('.panel');
      $$('.config-row', orientationPanel).forEach((row) => row.remove());
      const orientationRows = node('div');
      orientationPanel.append(orientationRows);
      setKey($('#mode-heading + p'), 'native.activeInfo');
      setKey($('#orientation-heading + p'), 'native.orientationInfo');
      multiView = { stage, map, inactive, position, reference, referenceLabel, orientationRows, apply, discard, referenceId: null };
      $('#display-mode').addEventListener('change', (event) => {
        draft = null;
        currentDraft().mode = event.target.value;
        render();
      });
      $('#primary-display').addEventListener('change', (event) => {
        currentDraft().primaryId = event.target.value;
        render();
      });
      reference.addEventListener('change', () => {
        multiView.referenceId = reference.value;
        render();
      });
      position.addEventListener('change', () => {
        if (!geometryLocked()) snapDisplay(selectedId, multiView.referenceId, position.value);
      });
      new ResizeObserver(() => {
        if (route === 'multi-display' && !drag && snapshot && displays().length) drawMap();
      }).observe(map);
      document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') finishDrag(true);
      });
      window.addEventListener('blur', () => finishDrag(true));
      window.addEventListener('appearancefit', () => {
        finishDrag(true);
        if (route === 'multi-display' && snapshot && displays().length) drawMap();
      });
    }

    function modeOptions() {
      const all = displays();
      const options = [];
      for (const [mode, label] of Object.entries(modeKeys)) {
        const available = ['extend', 'mirror'].includes(mode) ? all.length > 1 : all.some((display) => display.kind === mode);
        if (!available && !snapshot.modeCapabilities?.[mode] && mode !== modeValue()) continue;
        const unsupported = unavailableMode(mode);
        options.push({ value: mode, label: t(label) + (unsupported ? ' · ' + t('notSupported') : ''), disabled: Boolean(unsupported) });
      }
      const solo = all.filter((display) => all.filter((item) => item.kind === display.kind).length > 1 || snapshot.mode === 'solo');
      for (const display of solo) options.push(selectOption('solo:' + display.id, tr('only', { name: screenLabel(display) })));
      if (snapshot.mode === 'solo' && !options.some((option) => option.value === 'solo'))
        options.push(selectOption('solo', tr('solo')));
      return options;
    }

    function renderOrientation(display) {
      const row = node('div', 'config-row');
      const description = node('div');
      const label = node('label', '', screenLabel(display));
      const select = node('select');
      select.id = 'native-orientation-' + screenNumber(display);
      label.htmlFor = select.id;
      description.append(label);
      const capability = display.controls?.orientation;
      const unavailable = !display.active ? tr('inactive') : snapshot.mode === 'mirror' ? tr('mirrorOrientation') :
        capability?.supported === false ? reasonText(capability.reason) : '';
      if (unavailable) {
        const note = node('p', 'subtle', unavailable);
        note.id = select.id + '-note';
        select.setAttribute('aria-describedby', note.id);
        description.append(note);
      }
      row.append(description, select);
      const value = currentDraft().changes.get(display.id)?.orientation ?? display.orientation;
      renderSelect(select, Object.entries(orientations).map(([value, key]) => selectOption(value, t(key))), value);
      select.disabled = locked() || Boolean(unavailable) || currentDraft().mode !== modeValue();
      select.addEventListener('change', () => {
        editDisplay(display.id, { orientation: Number(select.value) });
        for (const [id, snap] of currentDraft().snaps) {
          if (id === display.id || snap.targetId === display.id) applySnap(id, snap.targetId, snap.direction);
        }
        render();
      });
      return row;
    }

    function renderMulti() {
      const { map, inactive, position, reference, referenceLabel, orientationRows, apply, discard } = multiView;
      const current = currentDraft();
      const geometry = activeGeometry();
      const chooseReference = geometry.filter((display) => display.id !== selectedId);
      const source = geometry.find((display) => display.id === selectedId);
      if (!chooseReference.some((display) => display.id === multiView.referenceId)) multiView.referenceId = chooseReference[0]?.id;
      const target = chooseReference.find((display) => display.id === multiView.referenceId);
      referenceLabel.textContent = tr('reference');
      renderSelect(reference, chooseReference.map((display) => selectOption(display.id, screenLabel(display))), multiView.referenceId);
      reference.disabled = !source || geometryLocked();
      reference.closest('.native-reference').hidden = chooseReference.length < 2;
      position.disabled = !source || !target || geometryLocked();
      position.value = source && target ? sideOf(center(source), center(target)) : '';
      setText('#position-label', source ? t('multi.positionLabel', { name: screenLabel(source) }) : t('multi.arrangement'));
      setText('#arrangement-help', geometryLocked() ? tr('activeOnly') : tr('drag'));
      setText('#arrangement-note', hasDraft() ? tr('draft') : tr('activeOnly'));
      renderSelect($('#display-mode'), modeOptions(), current.mode);
      $('#display-mode').disabled = locked() || displays().length < 2;
      const unavailable = unavailableMode(current.mode);
      setText('#mode-note', unavailable ? reasonText(unavailable.reason) : tr('activeInfo'));
      const primary = $('#primary-display');
      renderSelect(primary, geometry.map((display) => selectOption(display.id, screenLabel(display))), current.primaryId);
      primary.disabled = geometryLocked();
      setText('#primary-note', t(primary.disabled ? 'multi.primaryUnavailable' : 'multi.primaryHelp'));
      orientationRows.replaceChildren(...displays().map(renderOrientation));
      inactive.replaceChildren(...displays().filter((display) => !display.active).map((display) => {
        const element = button(screenLabel(display) + ' · ' + tr('inactiveLabel'), () => selectDevice(display.id));
        element.dataset.nativeDevice = display.id;
        element.setAttribute('aria-pressed', String(selectedId === display.id));
        return element;
      }));
      apply.textContent = tr('apply');
      discard.textContent = tr('discard');
      discard.disabled = locked() || !hasDraft();
      apply.disabled = discard.disabled || (current.mode !== modeValue() && Boolean(unavailable));
      map.toggleAttribute('data-movable', !geometryLocked());
      if (!drag) drawMap();
    }

    function center(rect) { return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 }; }
    function sideOf(point, target) {
      const dx = point.x - target.x;
      const dy = point.y - target.y;
      return Math.abs(dx) >= Math.abs(dy) ? (dx < 0 ? 'left' : 'right') : (dy < 0 ? 'top' : 'bottom');
    }

    function drawMap() {
      const map = multiView.map;
      const geometry = activeGeometry();
      if (!geometry.length) {
        map.replaceChildren(node('p', 'subtle native-map-empty', tr('noActive')));
        return;
      }
      const x = Math.min(...geometry.map((item) => item.x));
      const y = Math.min(...geometry.map((item) => item.y));
      const width = Math.max(...geometry.map((item) => item.x + item.width)) - x;
      const height = Math.max(...geometry.map((item) => item.y + item.height)) - y;
      const padding = 20;
      const scale = Math.min((map.clientWidth - padding * 2) / width, (map.clientHeight - padding * 2) / height);
      if (scale <= 0) return;
      const left = (map.clientWidth - width * scale) / 2;
      const top = (map.clientHeight - height * scale) / 2;
      const mirrored = snapshot.mode === 'mirror';
      if (mirrored) {
        const item = geometry.find((display) => display.id === selectedId) || geometry[0];
        const frame = node('div', 'native-monitor');
        frame.style.left = left + 'px';
        frame.style.top = top + 'px';
        frame.style.width = width * scale + 'px';
        frame.style.height = height * scale + 'px';
        frame.append(node('span', 'native-monitor-number', geometry.map(screenNumber).join(' / ')), node('span', 'native-monitor-label', t('multi.mirror')));
        map.replaceChildren(frame);
        const devices = node('div', 'native-mirror-devices');
        for (const display of geometry) {
          const choice = button(screenLabel(display), () => selectDevice(display.id));
          choice.dataset.nativeDevice = display.id;
          choice.setAttribute('aria-pressed', String(item.id === display.id));
          devices.append(choice);
        }
        map.append(devices);
        return;
      }
      const focus = focusControl();
      map.replaceChildren(...geometry.map((display) => {
        const element = button(null, () => selectDevice(display.id), 'native-monitor');
        element.dataset.nativeDevice = display.id;
        element.setAttribute('aria-pressed', String(selectedId === display.id));
        const primary = currentDraft().primaryId === display.id;
        element.setAttribute('aria-label', screenLabel(display) + (primary ? ' · ' + t('primaryDisplay') : ''));
        element.style.left = left + (display.x - x) * scale + 'px';
        element.style.top = top + (display.y - y) * scale + 'px';
        element.style.width = Math.max(24, display.width * scale) + 'px';
        element.style.height = Math.max(24, display.height * scale) + 'px';
        element.append(node('span', 'native-monitor-number', screenNumber(display)),
          node('span', 'native-monitor-label', displayName(display) + (primary ? ' · ' + t('primaryDisplay') : '')));
        if (!geometryLocked()) element.setAttribute('aria-keyshortcuts', Object.keys(directions).join(' '));
        element.addEventListener('keydown', (event) => {
          const direction = directions[event.key];
          if (!direction || geometryLocked()) return;
          event.preventDefault();
          const target = nearest(display.id, center(display), activeGeometry());
          if (target) snapDisplay(display.id, target.id, direction);
        });
        element.addEventListener('pointerdown', (event) => startDrag(event, element, display.id));
        element.addEventListener('pointermove', moveDrag);
        element.addEventListener('pointerup', (event) => {
          if (drag?.pointerId !== event.pointerId) return;
          moveDrag(event);
          finishDrag(false);
        });
        for (const eventName of ['lostpointercapture', 'pointercancel']) {
          element.addEventListener(eventName, (event) => {
            if (drag?.pointerId === event.pointerId) finishDrag(true);
          });
        }
        return element;
      }));
      restoreFocus(focus);
    }

    function nearest(sourceId, point, candidates) {
      let nearestDisplay;
      let min = Infinity;
      for (const display of candidates) {
        if (display.id === sourceId) continue;
        const target = center(display);
        const distance = Math.hypot(point.x - target.x, point.y - target.y);
        if (distance < min) {
          min = distance;
          nearestDisplay = display;
        }
      }
      return nearestDisplay;
    }

    function startDrag(event, element, id) {
      if (drag || geometryLocked() || event.button !== 0 || !event.isPrimary) return;
      const rect = element.getBoundingClientRect();
      const bounds = multiView.map.getBoundingClientRect();
      const candidates = $$('.native-monitor', multiView.map).map((monitor) => {
        const rect = monitor.getBoundingClientRect();
        return { id: monitor.dataset.nativeDevice, x: rect.x, y: rect.y, width: rect.width, height: rect.height, element: monitor };
      });
      drag = { id, element, rect, bounds, candidates, pointerId: event.pointerId, x: event.clientX, y: event.clientY, moved: false, target: null };
      element.setPointerCapture(event.pointerId);
    }

    function moveDrag(event) {
      if (drag?.pointerId !== event.pointerId) return;
      const { id, element, rect, bounds, candidates, x, y } = drag;
      const dx = event.clientX - x;
      const dy = event.clientY - y;
      if (!drag.moved && Math.hypot(dx, dy) < 5) return;
      drag.moved = true;
      element.dataset.dragging = '';
      const pageScale = window.monitorAppearance?.scale || 1;
      element.style.transform = 'translate(' + dx / pageScale + 'px,' + dy / pageScale + 'px)';
      const point = { x: rect.x + rect.width / 2 + dx, y: rect.y + rect.height / 2 + dy };
      if (drag.target) drag.target.element.removeAttribute('data-snap');
      const inside = event.clientX >= bounds.left && event.clientX <= bounds.right &&
        event.clientY >= bounds.top && event.clientY <= bounds.bottom;
      drag.target = inside ? nearest(id, point, candidates) : null;
      if (drag.target) {
        drag.direction = sideOf(point, center(drag.target));
        drag.target.element.dataset.snap = drag.direction;
      }
    }

    function finishDrag(cancel) {
      if (!drag) return;
      const { id, element, pointerId, moved, target, direction } = drag;
      drag = null;
      if (element.hasPointerCapture(pointerId)) element.releasePointerCapture(pointerId);
      element.removeAttribute('data-dragging');
      element.style.removeProperty('transform');
      target?.element.removeAttribute('data-snap');
      if (!cancel && moved && target && direction) snapDisplay(id, target.id, direction);
      if (deferredControlsState) {
        const state = deferredControlsState;
        deferredControlsState = null;
        if (acceptSnapshot(state)) render();
      }
    }

    function intersects(a, b) {
      return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y;
    }

    function applySnap(id, targetId, direction) {
      const geometry = activeGeometry();
      const source = geometry.find((item) => item.id === id);
      const target = geometry.find((item) => item.id === targetId);
      if (!source || !target) return;
      const next = { ...source, x: target.x, y: target.y };
      if (direction === 'left') next.x -= source.width;
      if (direction === 'right') next.x += target.width;
      if (direction === 'top') next.y -= source.height;
      if (direction === 'bottom') next.y += target.height;
      const others = geometry.filter((item) => item.id !== id);
      for (let pass = 0; pass < others.length; pass++) {
        const collision = others.find((item) => intersects(next, item));
        if (!collision) break;
        if (direction === 'left') next.x = collision.x - source.width;
        if (direction === 'right') next.x = collision.x + collision.width;
        if (direction === 'top') next.y = collision.y - source.height;
        if (direction === 'bottom') next.y = collision.y + collision.height;
      }
      editDisplay(id, { x: next.x, y: next.y });
    }

    function snapDisplay(id, targetId, direction) {
      applySnap(id, targetId, direction);
      currentDraft().snaps.set(id, { targetId, direction });
      multiView.referenceId = targetId;
      selectDevice(id);
    }

    function initializeRoute() {
      if (initializedRoutes.has(route)) return;
      if (route === 'basic') initBasic();
      if (route === 'color') initColor();
      if (route === 'multi-display') initMulti();
      $('#identify')?.addEventListener('click', () => localAction('identify'));
      initializedRoutes.add(route);
    }

    function navigate(next, historyMode = 'push') {
      finishRanges();
      if (!routeUrls.has(next) || next === route) return;
      const started = performance.now();
      let nextContent = routeContents.get(next);
      if (!nextContent) {
        const html = window.desktopRoutes?.routes[next];
        if (!html) return;
        const template = document.createElement('template');
        template.innerHTML = html;
        nextContent = template.content.firstElementChild;
        routeContents.set(next, nextContent);
      }
      finishDrag(true);
      content.replaceWith(nextContent);
      content = nextContent;
      route = next;
      root.dataset.desktopRoute = route;
      for (const link of $$('.tabs a')) {
        if (link.dataset.route === route) link.setAttribute('aria-current', 'page');
        else link.removeAttribute('aria-current');
      }
      if (historyMode !== 'none') {
        const url = new URL(routeUrls.get(route));
        url.searchParams.set('lang', root.lang || 'zh-CN');
        try { history[historyMode === 'replace' ? 'replaceState' : 'pushState']({ displayRoute: route }, '', url); }
        catch {
          // Local-file preview browsers restrict cross-directory History URLs.
          history[historyMode === 'replace' ? 'replaceState' : 'pushState']({ displayRoute: route }, '', '#' + route);
        }
      }
      initializeRoute();
      i18n.render();
      window.dispatchEvent(new CustomEvent('desktoproutechange', { detail: { route } }));
      window.desktopRouteMetrics = { route, durationMs: performance.now() - started };
    }

    for (const link of $$('.tabs a')) {
      const target = new URL(link.href).pathname.match(/\/(basic|multi-display|color)\/index\.html$/)?.[1] || route;
      link.dataset.route = target;
      link.href = routeUrls.get(target).href;
      link.addEventListener('click', event => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        if (!window.desktopRoutes?.routes[target]) return;
        event.preventDefault();
        navigate(target);
      });
    }
    history.replaceState({ displayRoute: route }, '', location.href);
    window.addEventListener('popstate', event => {
      const next = event.state?.displayRoute || location.pathname.match(/\/(basic|multi-display|color)\/index\.html$/)?.[1];
      if (next) navigate(next, 'none');
    });
    window.addEventListener('blur', finishRanges);
    document.addEventListener('visibilitychange', () => { if (document.hidden) finishRanges(); });
    window.desktopNavigate = navigate;
    window.desktopNavigation = { get route() { return route; }, navigate };
    window.desktopNavigationReady = Boolean(window.desktopRoutes);
    initializeRoute();

    onHostEvent = (data) => {
      if (data.event === 'controlsChanged' && data.state) {
        if (drag) {
          if (!deferredControlsState || !Number.isFinite(data.state.revision) ||
            !Number.isFinite(deferredControlsState.revision) || data.state.revision >= deferredControlsState.revision)
            deferredControlsState = data.state;
        } else if (acceptSnapshot(data.state)) render();
      }
      if (data.event === 'displaysChanged') {
        continuousSuspended = true;
        cancelContinuous();
        scheduleRefresh();
        render();
      }
      if (data.event === 'pending') {
        setPending(data.pending ?? (data.seconds == null || data.seconds > 0), data.seconds, data.recoveryError);
        if (!pending) scheduleRefresh();
        render();
      }
    };
    setInterval(updateConfirmation, 250);
    i18n.register({}, render);
    refresh();
  }
})();

