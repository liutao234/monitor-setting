(() => {
  const i18n = window.monitorI18n;
  const messages = {
    "zh-CN": {
      "color.chooseDisplay": "选择显示器",
      "color.selection": "当前选择：{name} · {connection}",
      "color.preview": "色彩预览",
      "color.previewHint": "用色块和灰阶查看色调、明暗和边缘变化。",
      "color.whitePoint": "白点 {value}",
      "color.sdrPreview": "标准动态范围",
      "color.hdrPreview": "HDR 效果预览",
      "color.temperature": "色温",
      "color.temperatureHint": "数值越低，画面越偏暖。",
      "color.temperatureMin": "2700 K 暖",
      "color.temperatureMax": "8500 K 冷",
      "color.presets": "色温预设",
      "color.warm": "暖色",
      "color.standard": "标准",
      "color.cool": "冷色",
      "color.contrast": "对比度",
      "color.contrastHint": "调整亮部与暗部的差异。",
      "color.sharpness": "锐度",
      "color.sharpnessHint": "调整色块边缘的清晰程度。",
      "color.sharpnessUnavailable": "此示例屏幕的锐度固定。",
      "color.hdrHint": "预览更明亮的高光和更深的阴影。",
      "color.hdrUnavailable": "此示例设备不支持 HDR。",
      "color.on": "已开启",
      "color.off": "已关闭",
      "color.response": "响应速度",
      "color.responseHint": "选择像素响应档位。",
      "color.responseUnavailable": "此示例屏幕使用固定响应档位。",
      "color.fast": "快速",
      "color.fastest": "最快",
      "color.motionPreview": "动态响应预览",
      "color.motionHint": "响应越快，示意色块的拖影越短。",
      "color.motionMode": "当前响应：{mode}",
      "color.playMotion": "播放动态预览",
    },
    ja: {
      "color.chooseDisplay": "ディスプレイを選択",
      "color.selection": "選択中：{name} · {connection}",
      "color.preview": "色のプレビュー",
      "color.previewHint": "カラーパッチとグレースケールで色合い、明暗、輪郭を確認できます。",
      "color.whitePoint": "白色点 {value}",
      "color.sdrPreview": "標準ダイナミックレンジ",
      "color.hdrPreview": "HDR 効果のプレビュー",
      "color.temperature": "色温度",
      "color.temperatureHint": "値を下げると暖色に近づきます。",
      "color.temperatureMin": "2700 K 暖色",
      "color.temperatureMax": "8500 K 寒色",
      "color.presets": "色温度のプリセット",
      "color.warm": "暖色",
      "color.standard": "標準",
      "color.cool": "寒色",
      "color.contrast": "コントラスト",
      "color.contrastHint": "明るい部分と暗い部分の差を調整します。",
      "color.sharpness": "シャープネス",
      "color.sharpnessHint": "カラーパッチの輪郭の鮮明さを調整します。",
      "color.sharpnessUnavailable": "このサンプルディスプレイではシャープネスは固定です。",
      "color.hdrHint": "より明るいハイライトと深いシャドウをプレビューします。",
      "color.hdrUnavailable": "このサンプルディスプレイは HDR に対応していません。",
      "color.on": "オン",
      "color.off": "オフ",
      "color.response": "応答速度",
      "color.responseHint": "画素の応答モードを選択します。",
      "color.responseUnavailable": "このサンプルディスプレイでは応答モードは固定です。",
      "color.fast": "高速",
      "color.fastest": "最速",
      "color.motionPreview": "応答速度のプレビュー",
      "color.motionHint": "応答速度が上がると、サンプルの残像が短くなります。",
      "color.motionMode": "現在の応答モード：{mode}",
      "color.playMotion": "動きのプレビューを再生",
    },
    ko: {
      "color.chooseDisplay": "디스플레이 선택",
      "color.selection": "현재 선택: {name} · {connection}",
      "color.preview": "색상 미리 보기",
      "color.previewHint": "색상 견본과 회색조로 색조, 명암, 윤곽을 확인하세요.",
      "color.whitePoint": "백색점 {value}",
      "color.sdrPreview": "표준 다이내믹 레인지",
      "color.hdrPreview": "HDR 효과 미리 보기",
      "color.temperature": "색온도",
      "color.temperatureHint": "값이 낮을수록 색이 따뜻해집니다.",
      "color.temperatureMin": "2700 K 따뜻함",
      "color.temperatureMax": "8500 K 차가움",
      "color.presets": "색온도 프리셋",
      "color.warm": "따뜻하게",
      "color.standard": "표준",
      "color.cool": "차갑게",
      "color.contrast": "명암비",
      "color.contrastHint": "밝은 영역과 어두운 영역의 차이를 조절합니다.",
      "color.sharpness": "선명도",
      "color.sharpnessHint": "색상 견본의 윤곽이 얼마나 선명한지 조절합니다.",
      "color.sharpnessUnavailable": "이 예시 디스플레이의 선명도는 고정되어 있습니다.",
      "color.hdrHint": "더 밝은 하이라이트와 깊은 그림자를 미리 봅니다.",
      "color.hdrUnavailable": "이 예시 디스플레이는 HDR을 지원하지 않습니다.",
      "color.on": "켬",
      "color.off": "끔",
      "color.response": "응답 속도",
      "color.responseHint": "픽셀 응답 모드를 선택합니다.",
      "color.responseUnavailable": "이 예시 디스플레이의 응답 모드는 고정되어 있습니다.",
      "color.fast": "빠름",
      "color.fastest": "가장 빠름",
      "color.motionPreview": "동작 응답 미리 보기",
      "color.motionHint": "응답이 빠를수록 예시 색상 견본의 잔상이 짧아집니다.",
      "color.motionMode": "현재 응답 모드: {mode}",
      "color.playMotion": "동작 미리 보기 재생",
    },
    en: {
      "color.chooseDisplay": "Choose a display",
      "color.selection": "Selected: {name} · {connection}",
      "color.preview": "Color preview",
      "color.previewHint": "Check tone, contrast, and edges with color patches and grayscale.",
      "color.whitePoint": "White point {value}",
      "color.sdrPreview": "Standard dynamic range",
      "color.hdrPreview": "HDR effect preview",
      "color.temperature": "Color temperature",
      "color.temperatureHint": "Lower values make the image warmer.",
      "color.temperatureMin": "2700 K warm",
      "color.temperatureMax": "8500 K cool",
      "color.presets": "Color temperature presets",
      "color.warm": "Warm",
      "color.standard": "Standard",
      "color.cool": "Cool",
      "color.contrast": "Contrast",
      "color.contrastHint": "Adjust the difference between light and dark areas.",
      "color.sharpness": "Sharpness",
      "color.sharpnessHint": "Adjust how clearly patch edges are defined.",
      "color.sharpnessUnavailable": "Sharpness is fixed on this sample display.",
      "color.hdrHint": "Preview brighter highlights and deeper shadows.",
      "color.hdrUnavailable": "This sample display does not support HDR.",
      "color.on": "On",
      "color.off": "Off",
      "color.response": "Response speed",
      "color.responseHint": "Choose a pixel response mode.",
      "color.responseUnavailable": "Response mode is fixed on this sample display.",
      "color.fast": "Fast",
      "color.fastest": "Fastest",
      "color.motionPreview": "Motion response preview",
      "color.motionHint": "Faster modes shorten the sample patch's simulated trail.",
      "color.motionMode": "Response mode: {mode}",
      "color.playMotion": "Play motion preview",
    },
  };

  if (window.chrome?.webview) {
    i18n.register(messages);
    return;
  }

  const displays = {
    1: {
      name: "builtin", connection: "builtinConnection", temperature: 6500, contrast: 50,
      sharpness: 50, response: "standard", hdr: false,
      supports: { sharpness: false, hdr: false, response: false },
    },
    2: {
      name: "external", temperature: 6500, contrast: 50,
      sharpness: 50, response: "standard", hdr: false,
      supports: { sharpness: true, hdr: true, response: true },
    },
  };

  const deviceButtons = document.querySelectorAll("[data-color-device]");
  const presetButtons = document.querySelectorAll("[data-temperature]");
  const selectedLabel = document.querySelector("#color-selected");
  const temperature = document.querySelector("#temperature");
  const temperatureValue = document.querySelector("#temperature-value");
  const contrast = document.querySelector("#contrast");
  const contrastValue = document.querySelector("#contrast-value");
  const sharpness = document.querySelector("#sharpness");
  const sharpnessValue = document.querySelector("#sharpness-value");
  const sharpnessHint = document.querySelector("#sharpness-hint");
  const hdr = document.querySelector("#hdr");
  const hdrHint = document.querySelector("#hdr-hint");
  const hdrStatus = document.querySelector("#hdr-status");
  const response = document.querySelector("#response");
  const responseHint = document.querySelector("#response-hint");
  const responseUnsupported = document.querySelector("#response-unsupported");
  const sample = document.querySelector("#color-sample");
  const sampleTemperature = document.querySelector("#sample-temperature");
  const sampleDynamicRange = document.querySelector("#sample-dynamic-range");
  const motionTrack = document.querySelector("#motion-track");
  const motionMarker = document.querySelector("#motion-marker");
  const motionMode = document.querySelector("#motion-mode");
  const motionPlay = document.querySelector("#motion-play");
  const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
  let selected = "2";
  let motionAnimation;

  function temperatureTint(kelvin) {
    const warm = Math.max(0, (6500 - kelvin) / (6500 - Number(temperature.min)));
    const cool = Math.max(0, (kelvin - 6500) / (Number(temperature.max) - 6500));
    const red = Math.round(255 - cool * 47);
    const green = Math.round(255 - warm * 52 - cool * 23);
    const blue = Math.round(255 - warm * 107);
    return `rgb(${red} ${green} ${blue})`;
  }

  function renderTemperature() {
    const kelvin = displays[selected].temperature;
    const text = `${kelvin} K`;
    temperature.value = kelvin;
    temperature.setAttribute("aria-valuetext", text);
    const fraction = (kelvin - Number(temperature.min)) / (Number(temperature.max) - Number(temperature.min));
    temperature.style.setProperty("--fill", `${fraction * 100}%`);
    temperatureValue.textContent = text;
    sampleTemperature.textContent = i18n.t("color.whitePoint", { value: text });
    sample.style.setProperty("--color-tint", temperatureTint(kelvin));
    for (const button of presetButtons) {
      button.setAttribute("aria-pressed", Number(button.dataset.temperature) === kelvin);
    }
  }

  function renderPictureAdjustments() {
    const display = displays[selected];
    contrast.value = display.contrast;
    contrast.style.setProperty("--fill", `${display.contrast}%`);
    contrastValue.textContent = display.contrast;
    sample.style.setProperty("--sample-contrast", (0.55 + display.contrast * 0.009).toFixed(3));

    sharpness.disabled = !display.supports.sharpness;
    sharpness.value = display.sharpness;
    sharpness.style.setProperty("--fill", `${display.sharpness}%`);
    sharpnessValue.dataset.unavailable = sharpness.disabled;
    sharpnessValue.textContent = sharpness.disabled ? i18n.t("notSupported") : display.sharpness;
    sharpnessHint.textContent = i18n.t(sharpness.disabled ? "color.sharpnessUnavailable" : "color.sharpnessHint");
    sample.style.setProperty("--sample-blur", `${((100 - display.sharpness) * 0.013).toFixed(2)}px`);
  }

  function renderHdr() {
    const display = displays[selected];
    hdr.disabled = !display.supports.hdr;
    hdr.checked = display.hdr;
    hdrHint.textContent = i18n.t(hdr.disabled ? "color.hdrUnavailable" : "color.hdrHint");
    hdrStatus.textContent = i18n.t(hdr.disabled ? "notSupported" : display.hdr ? "color.on" : "color.off");
    sample.dataset.hdr = display.hdr;
    sampleDynamicRange.textContent = i18n.t(display.hdr ? "color.hdrPreview" : "color.sdrPreview");
  }

  function renderResponse() {
    const display = displays[selected];
    response.disabled = !display.supports.response;
    response.value = display.response;
    responseUnsupported.hidden = !response.disabled;
    responseHint.textContent = i18n.t(response.disabled ? "color.responseUnavailable" : "color.responseHint");
    motionPlay.disabled = response.disabled;
    motionPlay.hidden = reducedMotion.matches;
    motionTrack.dataset.response = display.response;
    motionMode.textContent = i18n.t("color.motionMode", { mode: i18n.t(`color.${display.response}`) });
  }

  function renderDisplay() {
    const display = displays[selected];
    for (const button of deviceButtons) {
      button.setAttribute("aria-pressed", button.dataset.colorDevice === selected);
    }
    selectedLabel.textContent = i18n.t("color.selection", {
      name: i18n.t(display.name),
      connection: display.connection ? i18n.t(display.connection) : "USB-C",
    });
    renderTemperature();
    renderPictureAdjustments();
    renderHdr();
    renderResponse();
  }

  for (const button of deviceButtons) {
    button.addEventListener("click", () => {
      selected = button.dataset.colorDevice;
      motionAnimation?.cancel();
      renderDisplay();
    });
  }
  temperature.addEventListener("input", () => {
    displays[selected].temperature = temperature.valueAsNumber;
    renderTemperature();
  });
  for (const button of presetButtons) {
    button.addEventListener("click", () => {
      displays[selected].temperature = Number(button.dataset.temperature);
      renderTemperature();
    });
  }
  contrast.addEventListener("input", () => {
    displays[selected].contrast = contrast.valueAsNumber;
    renderPictureAdjustments();
  });
  sharpness.addEventListener("input", () => {
    displays[selected].sharpness = sharpness.valueAsNumber;
    renderPictureAdjustments();
  });
  hdr.addEventListener("change", () => {
    displays[selected].hdr = hdr.checked;
    renderHdr();
  });
  response.addEventListener("change", () => {
    displays[selected].response = response.value;
    renderResponse();
  });
  motionPlay.addEventListener("click", () => {
    motionAnimation?.cancel();
    motionAnimation = motionMarker.animate([{ left: "17%" }, { left: "83%" }], { duration: 1050 });
  });
  reducedMotion.addEventListener("change", () => {
    motionAnimation?.cancel();
    renderResponse();
  });

  i18n.register(messages, renderDisplay);
})();
