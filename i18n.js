(() => {
  const languages = { 'zh-CN': '简体中文', ja: '日本語', ko: '한국어', en: 'English' };
  const messages = {
    'zh-CN': {
      brand: '屏幕工作台', preview: '交互原型', language: '界面语言',
      heading: '显示器设置',
      categories: '设置分类', basicTab: '基础设置', multiTab: '多屏管理', colorTab: '色彩与 HDR',
      footer: '设备与参数为示例，调整仅在此预览中生效。',
      builtin: '内建显示器', external: '外接显示器', primaryDisplay: '主显示器',
      identify: '识别显示器', currentSelection: '当前选择', builtinConnection: '内部连接', notSupported: '不支持',
    },
    ja: {
      brand: '画面ワークスペース', preview: '操作プレビュー', language: '表示言語',
      heading: 'ディスプレイ設定',
      categories: '設定カテゴリ', basicTab: '基本設定', multiTab: 'マルチ画面', colorTab: 'カラーと HDR',
      footer: 'デバイスと設定値はサンプルです。変更はこのプレビュー内で反映されます。',
      builtin: '内蔵ディスプレイ', external: '外部ディスプレイ', primaryDisplay: 'メインディスプレイ',
      identify: '識別', currentSelection: '選択中', builtinConnection: '内蔵接続', notSupported: '非対応',
    },
    ko: {
      brand: '디스플레이 작업 공간', preview: '인터랙티브 미리보기', language: '인터페이스 언어',
      heading: '디스플레이 설정',
      categories: '설정 분류', basicTab: '기본 설정', multiTab: '다중 디스플레이', colorTab: '색상 및 HDR',
      footer: '기기와 설정값은 예시이며, 변경 사항은 이 미리보기에서만 적용됩니다.',
      builtin: '내장 디스플레이', external: '외장 디스플레이', primaryDisplay: '주 디스플레이',
      identify: '식별', currentSelection: '선택한 화면', builtinConnection: '내부 연결', notSupported: '지원 안 함',
    },
    en: {
      brand: 'Display Workspace', preview: 'Interactive prototype', language: 'Interface language',
      heading: 'Display settings',
      categories: 'Settings categories', basicTab: 'Basic settings', multiTab: 'Multi-display', colorTab: 'Color & HDR',
      footer: 'Devices and values are examples. Changes apply within this preview.',
      builtin: 'Built-in display', external: 'External display', primaryDisplay: 'Primary display',
      identify: 'Identify', currentSelection: 'Current selection', builtinConnection: 'Internal', notSupported: 'Unsupported',
    },
  };

  const storageKey = 'monitor-setting-language';
  const supported = (value) => Object.hasOwn(languages, value);
  const renderers = new Set();
  const navKeys = ['basicTab', 'multiTab', 'colorTab'];
  let locale = languageFromUrl();

  function languageFromUrl() {
    const requested = new URLSearchParams(location.search).get('lang');
    if (supported(requested)) return requested;
    try {
      const saved = localStorage.getItem(storageKey);
      if (supported(saved)) return saved;
    } catch {
      // Navigation carries the language even when browser storage is unavailable.
    }
    return 'zh-CN';
  }

  function findMessage(key) {
    return messages[locale][key] ?? messages['zh-CN'][key];
  }

  function t(key, values = {}) {
    const message = findMessage(key) ?? key;
    return message.replace(/\{(\w+)\}/g, (placeholder, name) => values[name] ?? placeholder);
  }

  function localizeShell() {
    const selectors = {
      '.brand': 'brand', '.preview-tag': 'preview', '.page-heading h1': 'heading',
      '.page-footer': 'footer',
    };
    for (const [selector, key] of Object.entries(selectors)) {
      const element = document.querySelector(selector);
      if (!element) continue;
      const text = [...element.childNodes].find((node) => node.nodeType === Node.TEXT_NODE && node.textContent.trim());
      const label = document.createElement('span');
      label.dataset.i18n = key;
      if (text) text.replaceWith(label);
      else element.append(label);
    }

    const nav = document.querySelector('.tabs');
    if (nav) {
      nav.dataset.i18nAria = 'categories';
      nav.querySelectorAll('a').forEach((link, index) => { link.dataset.i18n = navKeys[index]; });
    }
  }

  function render() {
    document.documentElement.lang = locale;
    const titleKey = document.querySelector('.tabs [aria-current="page"]')?.dataset.i18n;
    if (titleKey) document.title = `${t(titleKey)} · ${t('brand')}`;
    for (const element of document.querySelectorAll('[data-i18n]')) {
      const message = findMessage(element.dataset.i18n);
      if (message !== undefined) element.textContent = message;
    }
    for (const element of document.querySelectorAll('[data-i18n-aria]')) {
      const message = findMessage(element.dataset.i18nAria);
      if (message !== undefined) element.setAttribute('aria-label', message);
    }
    for (const link of document.querySelectorAll('.brand[href], .tabs a[href]')) {
      const url = new URL(link.href);
      url.searchParams.set('lang', locale);
      link.href = url.href;
    }
    renderers.forEach((update) => update());
  }

  function addLanguagePicker() {
    const heading = document.querySelector('.page-heading');
    if (!heading) return;
    const actions = document.createElement('div');
    actions.className = 'shell-actions';

    const picker = document.createElement('div');
    picker.className = 'language-picker';
    picker.innerHTML = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a16 16 0 0 0 0 18m0-18a16 16 0 0 1 0 18"/></svg>';
    const select = document.createElement('select');
    select.id = 'ui-language';
    select.dataset.i18nAria = 'language';
    for (const [code, label] of Object.entries(languages)) {
      const option = new Option(label, code);
      option.lang = code;
      select.add(option);
    }
    select.value = locale;
    select.addEventListener('change', () => {
      locale = select.value;
      const url = new URL(location.href);
      url.searchParams.set('lang', locale);
      try { history.replaceState(history.state, '', url); } catch { /* Some local-file contexts restrict History. */ }
      try { localStorage.setItem(storageKey, locale); } catch { /* The URL remains the source for navigation. */ }
      render();
    });
    picker.append(select);
    actions.append(picker);
    heading.append(actions);
  }

  window.monitorI18n = {
    t,
    render,
    register(catalog, onLanguageChange) {
      for (const [code, dictionary] of Object.entries(catalog)) {
        Object.assign(messages[code], dictionary);
      }
      if (onLanguageChange) renderers.add(onLanguageChange);
      render();
    },
  };

  localizeShell();
  addLanguagePicker();
  render();
})();
