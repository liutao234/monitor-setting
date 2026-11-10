(() => {
  const root = document.documentElement;
  const system = matchMedia('(prefers-color-scheme: dark)');
  const key = 'monitor-setting-theme';
  let preference = 'system';
  try { const stored = localStorage.getItem(key); if (['light', 'dark', 'system'].includes(stored)) preference = stored; } catch { }
  function applyTheme() {
    root.dataset.themePreference = preference;
    root.dataset.theme = preference === 'system' ? system.matches ? 'dark' : 'light' : preference;
    root.style.colorScheme = root.dataset.theme;
  }
  applyTheme();
  system.addEventListener('change', applyTheme);
  const messages = {
    'zh-CN': { background: '背景', system: '跟随系统', light: '白色', dark: '黑色', brightness: '亮度', format: '画面规格', arrangement: '排列', mode: '显示模式', orientation: '方向', picture: '色温与 HDR', advanced: '高级色彩', preview: '预览', display: '显示器', sections: '设置分组' },
    ja: { background: '背景', system: 'システム', light: 'ホワイト', dark: 'ブラック', brightness: '明るさ', format: '画面仕様', arrangement: '配置', mode: '表示モード', orientation: '向き', picture: '色温度と HDR', advanced: '詳細カラー', preview: 'プレビュー', display: 'ディスプレイ', sections: '設定グループ' },
    ko: { background: '배경', system: '시스템 설정', light: '화이트', dark: '블랙', brightness: '밝기', format: '화면 규격', arrangement: '배치', mode: '표시 모드', orientation: '방향', picture: '색온도 및 HDR', advanced: '고급 색상', preview: '미리보기', display: '디스플레이', sections: '설정 그룹' },
    en: { background: 'Background', system: 'System', light: 'White', dark: 'Black', brightness: 'Brightness', format: 'Display format', arrangement: 'Arrangement', mode: 'Display mode', orientation: 'Orientation', picture: 'Color & HDR', advanced: 'Advanced color', preview: 'Preview', display: 'Display', sections: 'Settings groups' },
  };
  const translate = name => window.monitorI18n?.t('appearance.' + name) ?? messages['zh-CN'][name];
  let frame = 0;
  function scheduleRefresh() { if (!frame) frame = requestAnimationFrame(() => { frame = 0; refresh(); }); }
  function setData(element, name, value) { if (element.dataset[name] !== value) element.dataset[name] = value; }
  function sectionTabs(container, values, target = container) {
    let nav = container.querySelector(':scope > .adaptive-sections');
    if (!nav) {
      nav = document.createElement('div'); nav.className = 'adaptive-sections'; nav.setAttribute('role', 'tablist');
      for (const value of values) {
        const button = document.createElement('button'); button.type = 'button'; button.dataset.section = value; button.setAttribute('role', 'tab');
        button.addEventListener('click', () => { setData(target, 'section', value); refresh(); window.dispatchEvent(new Event('appearancefit')); });
        button.addEventListener('keydown', event => {
          if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
          event.preventDefault(); const index = values.indexOf(value);
          const next = event.key === 'Home' ? 0 : event.key === 'End' ? values.length - 1 : (index + (event.key === 'ArrowLeft' ? -1 : 1) + values.length) % values.length;
          nav.children[next].click(); nav.children[next].focus();
        });
        nav.append(button);
      }
      container.prepend(nav);
    }
    if (!values.includes(target.dataset.section)) setData(target, 'section', values[0]);
    nav.setAttribute('aria-label', translate('sections'));
    for (const button of nav.children) {
      const active = button.dataset.section === target.dataset.section; const label = translate(button.dataset.section);
      if (button.textContent !== label) button.textContent = label;
      button.setAttribute('aria-selected', String(active)); button.tabIndex = active ? 0 : -1;
    }
  }
  function devicePicker(id, host, source) {
    if (!host || !source) return;
    let select = host.querySelector('#' + id);
    if (!select) {
      select = document.createElement('select'); select.id = id; select.className = 'adaptive-device-picker';
      select.addEventListener('change', () => [...source.querySelectorAll('[data-native-device]')].find(button => button.dataset.nativeDevice === select.value)?.click());
      host.append(select);
    }
    select.setAttribute('aria-label', translate('display'));
    const options = [...source.querySelectorAll('[data-native-device]')].map(button => ({ value: button.dataset.nativeDevice, label: button.getAttribute('aria-label') || button.textContent.trim(), selected: button.getAttribute('aria-pressed') === 'true' }));
    const signature = JSON.stringify(options.map(({ value, label }) => [value, label]));
    if (select.dataset.options !== signature) { select.replaceChildren(...options.map(({ value, label }) => new Option(label, value))); select.dataset.options = signature; }
    const selected = options.find(option => option.selected)?.value;
    if (selected != null && select.value !== selected) select.value = selected;
  }
  function refresh() {
    const width = document.documentElement.clientWidth; const height = document.documentElement.clientHeight;
    setData(root, 'density', width < 980 || height < 800 ? 'compact' : 'comfortable');
    setData(root, 'narrow', String(width < 600)); setData(root, 'short', String(height < 500)); setData(root, 'low', String(height < 400));
    const page = document.querySelector('.page'); if (!page) return;
    for (const property of ['width', 'transform', 'top']) if (page.style[property]) page.style.removeProperty(property);
    const basic = page.querySelector('.basic-layout');
    if (basic) {
      const settings = basic.querySelector('.settings-grid'); if (settings) sectionTabs(settings, ['brightness', 'format']);
      devicePicker('adaptive-basic-device', basic.querySelector('.panel:nth-child(2) .section-head'), basic.querySelector('.screen-options'));
      const identify = basic.querySelector('#identify');
      const host = basic.querySelector(width < 600 ? '.panel:nth-child(2) .section-head' : '.panel:first-child .section-head');
      if (identify && host && identify.parentElement !== host) host.append(identify);
    }
    const multi = page.querySelector('.multi-layout');
    if (multi) {
      sectionTabs(multi, ['arrangement', 'mode', 'orientation']);
      const mode = multi.querySelector('#mode-heading')?.closest('.panel'); const orientation = multi.querySelector('#orientation-heading')?.closest('.panel');
      if (mode) setData(mode, 'adaptivePanel', 'mode'); if (orientation) setData(orientation, 'adaptivePanel', 'orientation');
      const actions = multi.querySelector('.native-layout-actions'); if (actions && actions.parentElement !== multi) multi.append(actions);
      const identify = multi.querySelector('#identify'); const arrangementHead = multi.querySelector('.arrangement-head');
      if (actions && identify && arrangementHead) {
        const host = height < 500 ? actions : arrangementHead;
        if (identify.parentElement !== host) height < 500 ? host.prepend(identify) : host.append(identify);
      }
      if (orientation) {
        const head = orientation.querySelector('.section-head');
        let picker = head?.querySelector('.adaptive-orientation-picker');
        if (head && !picker) {
          picker = document.createElement('select'); picker.className = 'adaptive-orientation-picker'; picker.id = 'adaptive-orientation-device';
          picker.addEventListener('change', refresh); head.append(picker);
        }
        if (picker) {
          picker.setAttribute('aria-label', translate('display'));
          const rows = [...orientation.querySelectorAll('.config-row')];
          const choices = rows.map(row => [row.querySelector('select')?.id, row.querySelector('label')?.textContent]);
          const signature = JSON.stringify(choices);
          if (picker.dataset.options !== signature) {
            const selected = picker.value;
            picker.replaceChildren(...choices.filter(([id]) => id).map(([id, label]) => new Option(label, id)));
            if (choices.some(([id]) => id === selected)) picker.value = selected;
            picker.dataset.options = signature;
          }
          for (const row of rows) setData(row, 'orientationVisible', String(row.querySelector('select')?.id === picker.value));
        }
      }
    }
    const color = page.querySelector('.color-panel');
    if (color) {
      const body = color.querySelector('.color-body'); if (body) sectionTabs(body, ['picture', 'advanced', 'preview'], color);
      devicePicker('adaptive-color-device', color.querySelector('.color-head'), color.querySelector('.color-devices'));
    }
    root.dataset.viewportFitted = '';
  }
  window.monitorAppearance = { scale: 1, fit: scheduleRefresh, refresh };
  document.addEventListener('DOMContentLoaded', () => {
    const actions = document.querySelector('.shell-actions');
    if (actions) {
      const picker = document.createElement('div'); picker.className = 'appearance-picker';
      const label = document.createElement('label'); label.htmlFor = 'ui-theme'; label.dataset.i18n = 'appearance.background';
      const select = document.createElement('select'); select.id = 'ui-theme'; select.dataset.i18nAria = 'appearance.background';
      for (const value of ['system', 'light', 'dark']) { const option = new Option('', value); option.dataset.i18n = 'appearance.' + value; select.add(option); }
      select.value = preference;
      select.addEventListener('change', () => { preference = select.value; try { localStorage.setItem(key, preference); } catch { } applyTheme(); });
      picker.append(label, select); actions.prepend(picker);
    }
    window.monitorI18n?.register(Object.fromEntries(Object.entries(messages).map(([locale, values]) => [locale, Object.fromEntries(Object.entries(values).map(([name, value]) => ['appearance.' + name, value]))])), scheduleRefresh);
    const page = document.querySelector('.page');
    if (page) new MutationObserver(scheduleRefresh).observe(page, { subtree: true, childList: true, attributes: true, attributeFilter: ['hidden'] });
    refresh();
  }, { once: true });
  window.addEventListener('desktoproutechange', refresh); window.addEventListener('resize', scheduleRefresh); window.visualViewport?.addEventListener('resize', scheduleRefresh);
})();
