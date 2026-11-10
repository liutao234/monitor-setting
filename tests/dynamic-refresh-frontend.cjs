const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));

// All state and writes are synthetic. This suite never changes a real display.
async function install(page) {
  await page.addInitScript(() => {
    const device = (id, supported) => ({
      id, name: supported ? 'Dynamic panel' : 'Fixed panel', kind: supported ? 'builtin' : 'external',
      active: true, primary: supported, x: supported ? 0 : 1920, y: 0,
      width: 1920, height: 1080, orientation: 0, refreshRate: 144,
      dynamicRefreshRate: false, physicalRefreshRate: 144, controlsLoading: false,
      modes: [60, 120, 144].map(refreshRate => ({ width: 1920, height: 1080, orientation: 0, refreshRate })),
      controls: {
        brightness: { supported: true, value: 50, min: 0, max: 100, step: 1 },
        resolution: { supported: true }, refreshRate: { supported: true }, orientation: { supported: true },
        dynamicRefreshRate: supported
          ? { supported: true, value: 0, options: [60, 72].map(value => ({ value: String(value), label: value + ' Hz' })) }
          : { supported: false, value: 0, reason: 'dynamicRefreshUnavailable', options: [] },
        contrast: { supported: false, reason: 'unsupported' }, sharpness: { supported: false, reason: 'unsupported' },
        temperature: { supported: false, reason: 'unsupported' }, hdr: { supported: false, reason: 'unsupported' },
        response: { supported: false, reason: 'vendorSpecific' }, scale: { supported: false, reason: 'windowsManaged' }
      }
    });
    let state = { revision: 1, backgroundControls: true, controlsComplete: true, mode: 'extend', pending: false, seconds: 0,
      modeCapabilities: Object.fromEntries(['extend', 'mirror', 'builtin', 'external'].map(key => [key, { supported: true }])),
      displays: [device('builtin', true), device('external', false)] };
    let prior = null;
    let receive;
    window.calls = [];
    window.publishDynamicCapability = capability => {
      state.displays[0].controls.dynamicRefreshRate = capability;
      state.revision++;
      receive({ data: { event: 'controlsChanged', state: structuredClone(state) } });
    };
    window.chrome ??= {};
    window.chrome.webview = {
      addEventListener(_, listener) { receive = listener; },
      postMessage(message) {
        calls.push(message);
        if (message.method === 'layout') {
          prior = structuredClone(state);
          for (const request of message.params.displays) {
            const display = state.displays.find(item => item.id === request.id);
            if ('dynamicRefreshRate' in request) {
              display.dynamicRefreshRate = request.dynamicRefreshRate;
              display.controls.dynamicRefreshRate.value = Number(request.dynamicRefreshRate);
            }
            if ('refreshRate' in request) display.refreshRate = request.refreshRate;
            display.physicalRefreshRate = display.dynamicRefreshRate ? display.refreshRate * 2 : display.refreshRate;
          }
          state.pending = true; state.seconds = 20; state.revision++;
        } else if (message.method === 'confirm') {
          state.pending = false; state.seconds = 0; state.revision++; prior = null;
        } else if (message.method === 'revert') {
          const revision = state.revision + 1;
          state = prior ?? state; state.revision = revision; state.pending = false; state.seconds = 0; prior = null;
        }
        setTimeout(() => receive({ data: { id: message.id, result: structuredClone(state) } }), 10);
      }
    };
    sessionStorage.setItem('display-selected', 'builtin');
  });
}

async function revealFormat(page) {
  if (await page.locator('#refresh-rate').isVisible()) return;
  await page.locator('.adaptive-sections button[data-section="format"]').click();
  assert.ok(await page.locator('#refresh-rate').isVisible());
}

async function settle(page) {
  await page.waitForTimeout(80);
}

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  const errors = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 880 } });
    page.setDefaultTimeout(6000);
    page.on('pageerror', error => errors.push(error.message));
    await install(page);
    await page.goto(pathToFileURL(path.resolve(__dirname, '../basic/index.html')).href + '?lang=en');
    await page.waitForFunction(() => window.desktopReady && window.desktopNavigationReady);
    const refresh = page.locator('#refresh-rate');
    assert.equal(await refresh.inputValue(), '144', 'Fixed current mode remains selected before DRR is enabled');
    assert.equal(await refresh.locator('option[value="dynamic:60"]').textContent(), 'Dynamic (60 Hz)',
      'An inactive virtual mode must not borrow the unrelated current physical rate for an invented range');
    assert.equal(await refresh.locator('option[value="dynamic:72"]').textContent(), 'Dynamic (72 Hz)');
    assert.ok(await refresh.isEnabled());

    await refresh.selectOption('dynamic:60');
    await page.waitForFunction(() => document.querySelector('.native-confirm')?.hidden === false);
    let request = await page.evaluate(() => calls.filter(call => call.method === 'layout').at(-1).params.displays[0]);
    assert.deepEqual(request, { id: 'builtin', dynamicRefreshRate: true, refreshRate: 60 });
    assert.equal(await refresh.inputValue(), 'dynamic:60');
    assert.equal(await refresh.locator('option:checked').textContent(), 'Dynamic (60–120 Hz)', 'Only verified active physical rate supplies the upper bound');
    assert.ok(await refresh.isDisabled(), 'DRR changes use the existing protected configuration confirmation');
    await page.locator('.tabs a').nth(2).click();
    await page.locator('.tabs a').nth(0).click();
    assert.equal(await refresh.inputValue(), 'dynamic:60', 'Pending DRR selection survives category navigation');
    assert.equal(await page.evaluate(() => calls.filter(call => call.method === 'displays').length), 1);
    await page.locator('.native-confirm .primary').click();
    await page.waitForFunction(() => document.querySelector('.native-confirm').hidden);

    await refresh.selectOption('120');
    await page.waitForFunction(() => !document.querySelector('.native-confirm').hidden);
    request = await page.evaluate(() => calls.filter(call => call.method === 'layout').at(-1).params.displays[0]);
    assert.deepEqual(request, { id: 'builtin', dynamicRefreshRate: false, refreshRate: 120 }, 'Picking a fixed rate explicitly disables DRR');
    assert.equal(await refresh.inputValue(), '120');
    await page.locator('.native-confirm button:not(.primary)').click();
    await page.waitForFunction(() => document.querySelector('.native-confirm').hidden);
    assert.equal(await refresh.inputValue(), 'dynamic:60', 'Reverting restores dynamic mode and its base rate');

    await page.locator('[data-native-device="external"]').click();
    const unavailable = refresh.locator('option[value="dynamic:unsupported"]');
    assert.equal(await unavailable.count(), 1, 'Unsupported DRR is identified in the rate menu');
    assert.ok(await unavailable.isDisabled());
    assert.equal(await unavailable.textContent(), 'Dynamic refresh rate unavailable');
    assert.equal(await refresh.inputValue(), '144');
    assert.ok(await refresh.isEnabled(), 'Unsupported DRR does not block supported fixed refresh rates');
    await page.locator('[data-native-device="builtin"]').click();

    const localizedDynamic = { 'zh-CN': '动态', ja: '動的', ko: '동적', en: 'Dynamic' };
    let layouts = 0;
    for (const viewport of [{ width: 744, height: 541 }, { width: 496, height: 360 }, { width: 320, height: 440 }]) {
      await page.setViewportSize(viewport);
      for (const locale of Object.keys(localizedDynamic)) {
        await page.locator('#ui-language').selectOption(locale);
        await revealFormat(page);
        await settle(page);
        assert.ok((await refresh.locator('option:checked').textContent()).startsWith(localizedDynamic[locale]));
        assert.equal(await page.locator('.format-group .field').count(), 3, 'DRR adds no extra row in the small-window layout');
        const geometry = await page.evaluate(() => {
          const controls = [...document.querySelectorAll('.format-group select,.format-group button')]
            .filter(element => element.getClientRects().length);
          const outside = controls.filter(element => {
            const box = element.getBoundingClientRect();
            return box.left < 0 || box.top < 0 || box.right > innerWidth + 1 || box.bottom > innerHeight + 1;
          }).map(element => element.id);
          scrollTo(300, 300);
          return { outside, scrollX, scrollY, scale: monitorAppearance.scale,
            font: getComputedStyle(document.querySelector('#refresh-rate')).fontSize,
            height: document.querySelector('#refresh-rate').getBoundingClientRect().height };
        });
        assert.deepEqual(geometry.outside, [], `${viewport.width}×${viewport.height}:${locale} no clipped controls`);
        assert.equal(geometry.scrollX, 0); assert.equal(geometry.scrollY, 0); assert.equal(geometry.scale, 1);
        assert.ok(parseFloat(geometry.font) >= 13); assert.ok(geometry.height >= 32);
        layouts++;
      }
    }
    await page.evaluate(() => publishDynamicCapability({ supported: false, value: 1, reason: 'dynamicRefreshUnavailable', options: [] }));
    await settle(page);
    assert.equal(await refresh.inputValue(), 'dynamic:60', 'An active DRR mode remains represented even when capability probing fails');
    assert.ok(await refresh.locator('option:checked').isDisabled(), 'Unavailable active mode is represented but cannot be re-applied');
    assert.match(await refresh.getAttribute('title'), /does not support dynamic refresh rate/);
    assert.deepEqual(errors, []);
    console.log(`Dynamic refresh frontend passed: supported/fixed/revert/unsupported flows, category state, four languages and ${layouts} compact layouts.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
