const { register, t } = window.monitorI18n;

const basicMessages = {
  'zh-CN': {
    'basic.connected': '已连接的显示器', 'basic.choose': '选择一块屏幕以调整它的设置。',
    'basic.connectedCount': '2 台已连接', 'basic.chooseGroup': '选择显示器', 'basic.selected': '已选中',
    'basic.builtinDetail': '14 英寸 · 内部连接', 'basic.externalDetail': '27 英寸 · USB-C',
    'basic.reset': '恢复默认', 'basic.brightness': '亮度', 'basic.format': '画面规格',
    'basic.resolution': '分辨率', 'basic.scale': '缩放', 'basic.refreshRate': '刷新率',
    'basic.identifyHint': '点击「识别显示器」可突出显示两块屏幕的编号。',
    'basic.identified': '1 号为内建显示器，2 号为外接显示器。', 'basic.number': '{number} 号屏幕',
    'basic.internalBrightness': '拖动滑块可调节内建显示器的亮度。',
    'basic.externalBrightness': '示例外接显示器支持亮度调节。', 'basic.defaultValue': '{value}（默认）',
    'basic.deviceChanged': '当前选择{name}。', 'basic.restored': '已恢复{name}的默认设置。',
  },
  ja: {
    'basic.connected': '接続済みのディスプレイ', 'basic.choose': '設定するディスプレイを選択します。',
    'basic.connectedCount': '2 台接続済み', 'basic.chooseGroup': 'ディスプレイを選択', 'basic.selected': '選択中',
    'basic.builtinDetail': '14 インチ · 内蔵接続', 'basic.externalDetail': '27 インチ · USB-C',
    'basic.reset': '既定値に戻す', 'basic.brightness': '明るさ', 'basic.format': '表示の詳細',
    'basic.resolution': '解像度', 'basic.scale': '拡大率', 'basic.refreshRate': 'リフレッシュレート',
    'basic.identifyHint': '「識別」で各ディスプレイの番号を強調します。',
    'basic.identified': '1 は内蔵、2 は外部ディスプレイです。', 'basic.number': 'ディスプレイ {number}',
    'basic.internalBrightness': 'スライダーで内蔵ディスプレイの明るさを調整します。',
    'basic.externalBrightness': 'このサンプルの外部ディスプレイは明るさの調整に対応しています。',
    'basic.defaultValue': '{value}（既定）', 'basic.deviceChanged': '{name}を選択しました。',
    'basic.restored': '{name}を既定の設定に戻しました。',
  },
  ko: {
    'basic.connected': '연결된 디스플레이', 'basic.choose': '설정할 디스플레이를 선택하세요.',
    'basic.connectedCount': '2대 연결됨', 'basic.chooseGroup': '디스플레이 선택', 'basic.selected': '선택됨',
    'basic.builtinDetail': '14인치 · 내부 연결', 'basic.externalDetail': '27인치 · USB-C',
    'basic.reset': '기본값 복원', 'basic.brightness': '밝기', 'basic.format': '화면 설정',
    'basic.resolution': '해상도', 'basic.scale': '배율', 'basic.refreshRate': '재생 빈도',
    'basic.identifyHint': '「식별」을 누르면 각 디스플레이의 번호가 강조됩니다.',
    'basic.identified': '1번은 내장, 2번은 외장 디스플레이입니다.', 'basic.number': '디스플레이 {number}',
    'basic.internalBrightness': '슬라이더를 움직여 내장 디스플레이의 밝기를 조정하세요.',
    'basic.externalBrightness': '이 예시 외장 디스플레이는 밝기 조정을 지원합니다.', 'basic.defaultValue': '{value} (기본값)',
    'basic.deviceChanged': '{name} 선택됨.', 'basic.restored': '{name}의 기본 설정을 복원했습니다.',
  },
  en: {
    'basic.connected': 'Connected displays', 'basic.choose': 'Select a display to adjust its settings.',
    'basic.connectedCount': '2 connected', 'basic.chooseGroup': 'Select a display', 'basic.selected': 'Selected',
    'basic.builtinDetail': '14 in · Internal', 'basic.externalDetail': '27 in · USB-C',
    'basic.reset': 'Reset defaults', 'basic.brightness': 'Brightness', 'basic.format': 'Display format',
    'basic.resolution': 'Resolution', 'basic.scale': 'Scale', 'basic.refreshRate': 'Refresh rate',
    'basic.identifyHint': 'Select Identify to highlight the display numbers.',
    'basic.identified': '1 is the built-in display. 2 is the external display.', 'basic.number': 'Display {number}',
    'basic.internalBrightness': 'Move the slider to adjust the built-in display’s brightness.',
    'basic.externalBrightness': 'This example external display supports brightness adjustment.',
    'basic.defaultValue': '{value} (default)', 'basic.deviceChanged': '{name} selected.',
    'basic.restored': 'Default settings restored for {name}.',
  },
};

if (window.chrome?.webview) {
  register(basicMessages);
} else {
  const devices = {
    1: {
      name: 'builtin', connection: 'builtinConnection', icon: 'laptop',
      resolution: ['1920 × 1200', '1680 × 1050', '1440 × 900'],
      scale: ['100%', '125%', '150%'],
      refreshRate: ['60 Hz', '48 Hz'],
      defaults: { brightness: '58', resolution: '1920 × 1200', scale: '125%', refreshRate: '60 Hz' },
    },
    2: {
      name: 'external', connection: 'USB-C', icon: 'monitor',
      resolution: ['2560 × 1440', '1920 × 1080', '1600 × 900'],
      scale: ['100%', '125%', '150%'],
      refreshRate: ['144 Hz', '120 Hz', '60 Hz'],
      defaults: { brightness: '72', resolution: '2560 × 1440', scale: '100%', refreshRate: '144 Hz' },
    },
  };

  const settings = Object.fromEntries(Object.entries(devices).map(([id, device]) => [id, { ...device.defaults }]));
  const get = (id) => document.getElementById(id);
  const screens = [get('display-1'), get('display-2')];
  const brightness = get('brightness');
  const formatControls = {
    resolution: get('resolution'),
    scale: get('scale'),
    refreshRate: get('refresh-rate'),
  };

  let active = 2;
  let identifying = false;
  let identifyTimer;

  function showBrightness() {
    const value = `${settings[active].brightness}%`;
    brightness.style.setProperty('--fill', value);
    brightness.setAttribute('aria-valuetext', value);
    get('brightness-value').value = value;
  }

  function showSummary() {
    const { connection } = devices[active];
    const { resolution, refreshRate } = settings[active];
    const label = t('basic.number', { number: active });
    get('device-summary').textContent = `${label} · ${t(connection)} · ${resolution} · ${refreshRate}`;
  }

  function showIdentification() {
    get('identify-status').textContent = t(identifying ? 'basic.identified' : 'basic.identifyHint');
  }

  function renderDevice() {
    const device = devices[active];
    const current = settings[active];
    screens.forEach((screen, index) => {
      const number = index + 1;
      screen.setAttribute('aria-pressed', number === active);
      screen.setAttribute('aria-label', `${t('basic.number', { number })} · ${t(devices[number].name)}`);
    });
    get('device-name').textContent = t(device.name);
    get('device-icon').setAttribute('href', `#${device.icon}`);
    get('brightness-hint').textContent = t(active === 1 ? 'basic.internalBrightness' : 'basic.externalBrightness');
    brightness.value = current.brightness;
    showBrightness();
    showSummary();

    for (const [key, control] of Object.entries(formatControls)) {
      const options = device[key].map((value) => {
        const label = value === device.defaults[key] ? t('basic.defaultValue', { value }) : value;
        return new Option(label, value, false, value === current[key]);
      });
      control.replaceChildren(...options);
    }
  }

  screens.forEach((screen, index) => {
    screen.addEventListener('click', () => {
      active = index + 1;
      renderDevice();
      get('settings-status').textContent = t('basic.deviceChanged', { name: t(devices[active].name) });
    });
  });

  brightness.addEventListener('input', () => {
    settings[active].brightness = brightness.value;
    showBrightness();
  });

  for (const [key, control] of Object.entries(formatControls)) {
    control.addEventListener('change', () => {
      settings[active][key] = control.value;
      showSummary();
    });
  }

  get('reset').addEventListener('click', () => {
    settings[active] = { ...devices[active].defaults };
    renderDevice();
    get('settings-status').textContent = t('basic.restored', { name: t(devices[active].name) });
  });

  get('identify').addEventListener('click', () => {
    clearTimeout(identifyTimer);
    screens.forEach((screen) => screen.classList.remove('identifying'));
    requestAnimationFrame(() => screens.forEach((screen) => screen.classList.add('identifying')));
    identifying = true;
    showIdentification();
    identifyTimer = setTimeout(() => {
      screens.forEach((screen) => screen.classList.remove('identifying'));
      identifying = false;
      showIdentification();
    }, 3200);
  });

  register(basicMessages, () => {
    renderDevice();
    showIdentification();
    get('settings-status').textContent = '';
  });
}
