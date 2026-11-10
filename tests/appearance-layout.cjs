const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const { pathToFileURL } = require('node:url');
const { chromium } = require(path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));
const output = process.argv[2] || path.join(process.env.TEMP, 'monitor-appearance-1.0.3');
fs.mkdirSync(output, { recursive: true });

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  const errors = [];
  const checks = [];
  const failures = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 880 }, colorScheme: 'light' });
    page.on('pageerror', error => errors.push(error.message));
    page.setDefaultTimeout(6000);
    await page.addInitScript(() => {
      const range = value => ({ supported: true, value, min: 0, max: 100, step: 1 });
      const unsupported = reason => ({ supported: false, reason });
      const state = { revision: 1, backgroundControls: true, controlsComplete: true, mode: 'extend', pending: false, seconds: 0,
        modeCapabilities: Object.fromEntries(['extend', 'mirror', 'builtin', 'external'].map(name => [name, { supported: true }])),
        displays: [
          { id: 'builtin', name: 'NE160QDM-NZC', kind: 'builtin', active: true, primary: true, x: 0, y: 0,
            width: 2560, height: 1600, refreshRate: 240, orientation: 0, connection: 'Internal', controlsLoading: false,
            modes: [{ width: 2560, height: 1600, refreshRate: 240, orientation: 0 }],
            controls: { brightness: range(100), contrast: unsupported('unsupported'), sharpness: unsupported('unsupported'),
              temperature: unsupported('unsupported'), hdr: unsupported('unsupported'), response: unsupported('vendorSpecific'),
              scale: unsupported('windowsManaged'), resolution: { supported: true }, refreshRate: { supported: true }, orientation: { supported: true } } },
          { id: 'external', name: 'LG ULTRAGEAR', kind: 'external', active: true, primary: false, x: -2560, y: 80,
            width: 2560, height: 1440, refreshRate: 300, orientation: 0, connection: 'HDMI', controlsLoading: false,
            modes: [{ width: 2560, height: 1440, refreshRate: 300, orientation: 0 }],
            controls: { brightness: range(90), contrast: range(60), sharpness: range(6), temperature: unsupported('ddcUnavailable'),
              hdr: { supported: true, value: 0 }, response: unsupported('vendorSpecific'), scale: unsupported('windowsManaged'),
              resolution: { supported: true }, refreshRate: { supported: true }, orientation: { supported: true } }
          },
          { id: 'inactive', name: 'Travel display', kind: 'external', active: false, primary: false, x: 0, y: 0,
            width: 1920, height: 1080, refreshRate: 60, orientation: 0, connection: 'DisplayPort', controlsLoading: false,
            modes: [{ width: 1920, height: 1080, refreshRate: 60, orientation: 0 }],
            controls: Object.fromEntries(['brightness', 'contrast', 'sharpness', 'temperature', 'hdr', 'response'].map(name => [name, unsupported('inactive')])) }
        ] };
      let listener;
      window.chrome ??= {};
      window.chrome.webview = {
        addEventListener(_, handler) { listener = handler; },
        postMessage({ id }) { setTimeout(() => listener({ data: { id, result: structuredClone(state) } }), 5); }
      };
      window.mockStateEvent = extra => listener({ data: { event: 'controlsChanged', state: { ...structuredClone(state), ...extra, revision: ++state.revision } } });
    });
    const settle = () => page.waitForTimeout(100);
    const inspect = async label => {
      await settle();
      const actual = await page.evaluate(() => {
        const elements = [...document.querySelectorAll('button, input, select, .tabs a, summary')]
          .filter(element => !element.closest('[hidden]') && getComputedStyle(element).visibility !== 'hidden' && element.getClientRects().length);
        const outside = elements.filter(element => {
          const box = element.getBoundingClientRect();
          return box.left < -1 || box.top < -1 || box.right > innerWidth + 1 || box.bottom > innerHeight + 1;
        }).map(element => element.id || element.className);
        const clipped = elements.filter(element => {
          const box = element.getBoundingClientRect();
          for (let ancestor = element.parentElement; ancestor && ancestor !== document.body; ancestor = ancestor.parentElement) {
            const style = getComputedStyle(ancestor);
            const clipX = ['hidden', 'clip'].includes(style.overflowX);
            const clipY = ['hidden', 'clip'].includes(style.overflowY);
            if (!clipX && !clipY) continue;
            const bounds = ancestor.getBoundingClientRect();
            if (clipX && (box.left < bounds.left - 1 || box.right > bounds.right + 1) ||
                clipY && (box.top < bounds.top - 1 || box.bottom > bounds.bottom + 1)) return true;
          }
          return false;
        }).map(element => element.id || element.className);
        const tiny = elements.filter(element => {
          const bounds = element.getBoundingClientRect();
          return parseFloat(getComputedStyle(element).fontSize) < 13 ||
            (element.matches('button, select, a, summary') && !element.matches('.native-monitor') && bounds.height < 32);
        }).map(element => ({ name: element.id || element.className, height: element.getBoundingClientRect().height, font: getComputedStyle(element).fontSize }));
        const page = document.querySelector('.page');
        const matrix = new DOMMatrix(getComputedStyle(page).transform);
        const map = document.querySelector('.native-map');
        const mapVisible = Boolean(map?.getClientRects().length && getComputedStyle(map).visibility !== 'hidden');
        const colorBody = document.querySelector('.color-body');
        const colorControls = document.querySelector('.color-controls');
        let compactColorWidth = null;
        if (document.documentElement.dataset.density === 'compact' && colorControls?.getClientRects().length) {
          const style = getComputedStyle(colorBody);
          compactColorWidth = {
            actual: colorControls.getBoundingClientRect().width,
            expected: colorBody.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight)
          };
        }
        window.scrollTo(500, 500);
        const geometry = ['.page', '.page-heading', '.tabs', '.native-confirm', '.color-head', '.color-body', '.color-controls', '#contrast', '#sharpness', '#response', '.response-control'].map(selector => {
          const element = document.querySelector(selector); if (!element) return null;
          const box = element.getBoundingClientRect(); const style = getComputedStyle(element);
          return { selector, x: box.x, y: box.y, width: box.width, height: box.height, margin: style.margin, padding: style.padding, gap: style.gap, display: style.display };
        }).filter(Boolean);
        return { outside, clipped, tiny, geometry, compactColorWidth, mapVisible, mapHeight: map?.getBoundingClientRect().height ?? 0,
          monitorCount: document.querySelectorAll('.native-map .native-monitor').length,
          transformX: matrix.a, transformY: matrix.d, scrollX, scrollY, background: getComputedStyle(document.body).backgroundColor,
          image: getComputedStyle(document.body).backgroundImage, theme: document.documentElement.dataset.theme,
          scale: window.monitorAppearance?.scale, width: innerWidth, height: innerHeight,
          topbars: document.querySelectorAll('.topbar').length };
      });
      try {
      assert.equal(actual.topbars, 0, label + ': removed topbar');
      assert.deepEqual(actual.outside, [], label + ': every visible control fits viewport');
      assert.deepEqual(actual.clipped, [], label + ': parent containers do not clip visible controls');
      assert.deepEqual(actual.tiny, [], label + ': text stays at least13px and buttons/selects32px tall');
      assert.equal(actual.scale, 1, label + ': no whole-page scale');
      assert.equal(actual.transformX, 1, label + ': horizontal scale unchanged');
      assert.equal(actual.transformY, 1, label + ': vertical scale unchanged');
      if (actual.compactColorWidth) {
        assert.ok(Math.abs(actual.compactColorWidth.actual - actual.compactColorWidth.expected) <= 2,
          label + ': compact color controls fill the available content width');
      }
      if (actual.mapVisible) {
        assert.ok(actual.mapHeight >= 64, label + ': arrangement canvas retains usable height');
        assert.equal(actual.monitorCount, 2, label + ': both active monitors are actually drawn');
      }
      assert.equal(actual.scrollX, 0, label + ': no horizontal scrolling');
      assert.equal(actual.scrollY, 0, label + ': no vertical scrolling');
      assert.equal(actual.image, 'none', label + ': plain background');
      if (actual.theme === 'light') assert.equal(actual.background, 'rgb(255, 255, 255)', label + ': white background');
      else assert.match(actual.background, /^rgb\((\d+), \1, \1\)$/, label + ': neutral dark background');
      } catch (error) { failures.push({ label, message: error.message, ...actual }); }
      checks.push({ label, ...actual });
    };

    const inspectSections = async label => {
      await inspect(label);
      const sections = await page.locator('.adaptive-sections button[data-section]:visible').evaluateAll(buttons => buttons.map(button => button.dataset.section));
      for (const section of sections) {
        await page.locator(`.adaptive-sections button[data-section="${section}"]`).click();
        await inspect(label + ':section=' + section);
        const orientationPicker = page.locator('#adaptive-orientation-device');
        if (await orientationPicker.isVisible()) {
          for (const id of await orientationPicker.locator('option').evaluateAll(options => options.map(option => option.value))) {
            await orientationPicker.selectOption(id);
            assert.ok(await page.locator('#' + id).isVisible(), 'Every monitor orientation remains reachable');
            await inspect(label + ':orientation=' + id);
          }
        }
      }
    };

    for (const route of ['basic', 'multi-display', 'color']) {
      await page.setViewportSize({ width: 1280, height: 880 });
      await page.goto(pathToFileURL(path.resolve(__dirname, '..', route, 'index.html')).href + '?lang=zh-CN');
      await page.waitForFunction(() => window.desktopReady && window.monitorAppearance);
      if (route === 'color') await page.locator('[data-native-device="external"]').click();
      assert.deepEqual(await page.locator('#ui-theme option').evaluateAll(options => options.map(option => option.value).sort()), ['dark', 'light', 'system']);
      await page.locator('#ui-theme').selectOption('light');
      await inspect(route + ':light:1280');
      await page.screenshot({ path: path.join(output, route + '-light.png') });
      await page.locator('#ui-theme').selectOption('dark');
      await inspect(route + ':dark:1280');
      await page.screenshot({ path: path.join(output, route + '-dark.png') });

      for (const size of [{ width: 1280, height: 760 }, { width: 1280, height: 720 }, { width: 960, height: 650 }, { width: 800, height: 880 }, { width: 744, height: 541 }, { width: 640, height: 440 }, { width: 496, height: 360 }, { width: 320, height: 440 }]) {
        await page.setViewportSize(size);
        for (const locale of ['zh-CN', 'ja', 'ko', 'en']) {
          await page.locator('#ui-language').selectOption(locale);
          await inspectSections(`${route}:dark:${size.width}x${size.height}:${locale}`);
        }
        if (size.width === 496) {
          await page.evaluate(() => mockStateEvent({ pending: true, seconds: 20 }));
          await inspectSections(route + ':confirmation:496x360');
          await page.evaluate(() => mockStateEvent({ pending: false, seconds: 0 }));
        }
      }
      await page.screenshot({ path: path.join(output, route + '-small.png') });
      const required = route === 'basic' ? ['#brightness', '#resolution', '#refresh-rate']
        : route === 'multi-display' ? ['#native-apply', '#display-mode', '#native-orientation-1']
        : ['#temperature', '#hdr', '#contrast', '#sharpness', '#response'];
      const sections = await page.locator('.adaptive-sections button[data-section]:visible').evaluateAll(buttons => buttons.map(button => button.dataset.section));
      for (const selector of required) {
        let reachable = await page.locator(selector).isVisible();
        for (const section of sections) {
          if (reachable) break;
          await page.locator(`.adaptive-sections button[data-section="${section}"]`).click();
          reachable = await page.locator(selector).isVisible();
        }
        assert.ok(reachable, route + ': all settings reachable without scrolling: ' + selector);
      }
      await page.evaluate(() => mockStateEvent({ pending: true, seconds: 20 }));
      await inspectSections(route + ':confirmation:small');
      await page.evaluate(() => mockStateEvent({ pending: false, seconds: 0 }));
    }
    await page.setViewportSize({ width: 1280, height: 880 });
    await page.locator('#ui-theme').selectOption('system');
    await page.emulateMedia({ colorScheme: 'light' });
    await page.waitForFunction(() => document.documentElement.dataset.theme === 'light');
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.waitForFunction(() => document.documentElement.dataset.theme === 'dark');
    await page.locator('#ui-theme').selectOption('light');
    await page.reload();
    await page.waitForFunction(() => window.desktopReady);
    assert.equal(await page.locator('#ui-theme').inputValue(), 'light', 'theme selection survives reload');
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'light', 'manual light overrides dark system');
    await page.mouse.wheel(0, 800);
    assert.equal(await page.evaluate(() => scrollY), 0, 'wheel does not move page');
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify(checks, null, 2));
    fs.writeFileSync(path.join(output, 'failures.json'), JSON.stringify(failures, null, 2));
    assert.deepEqual(failures.map(({ label, message }) => ({ label, message })), [], 'All adaptive combinations must keep controls readable and visible');
    console.log(`PASS ${checks.length} viewport/theme/language/section/confirmation combinations, unscaled readable controls, plain backgrounds, live system theme, persistence and no page scrolling`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
