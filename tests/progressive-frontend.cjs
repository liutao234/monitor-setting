const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));

// All Windows traffic is mocked. A slow physical probe is intentionally held
// until publishControls() is called; no test writes hardware settings.
async function install(page, selected = 'builtin') {
  await page.addInitScript(selected => {
    const range = value => ({ supported: true, value, min: 0, max: 100, step: 1 });
    const loading = () => ({ supported: false, reason: 'loading' });
    const display = (id, kind, x, primary) => ({
      id, name: kind === 'builtin' ? 'Internal panel' : 'Desk panel', kind,
      active: true, primary, devicePath: 'DISPLAY#' + id, gdiName: 'DISPLAY' + x,
      x, y: 0, width: 1920, height: 1080, orientation: 0, refreshRate: 60,
      modes: [
        { width: 1920, height: 1080, orientation: 0, refreshRate: 60 },
        { width: 1920, height: 1080, orientation: 0, refreshRate: 120 },
        { width: 1280, height: 720, orientation: 0, refreshRate: 60 },
      ],
      controlsLoading: kind === 'external',
      controls: { brightness: kind === 'builtin' ? range(50) : loading(),
        contrast: loading(), sharpness: loading(), temperature: loading(), response: loading(),
        hdr: { supported: true, value: 0 }, orientation: { supported: true },
        resolution: { supported: true }, refreshRate: { supported: true } }
    });
    const state = { revision: 1, backgroundControls: true, controlsComplete: false,
      mode: 'extend', pending: false, seconds: 0,
      modeCapabilities: Object.fromEntries(['extend', 'mirror', 'builtin', 'external'].map(mode => [mode, { supported: true }])),
      displays: [display('builtin', 'builtin', 0, true), display('external', 'external', 1920, false)] };
    let receive;
    window.calls = [];
    window.holdRead = false;
    window.mockEvent = data => receive({ data });
    window.publishControls = (failed = false) => {
      state.revision++;
      const external = state.displays[1];
      external.controlsLoading = false;
      Object.assign(external.controls, {
        brightness: failed ? { supported: false, reason: 'ddcUnavailable' } : range(65),
        contrast: failed ? { supported: false, reason: 'ddcUnavailable' } : range(60),
        sharpness: range(8), temperature: { supported: false, reason: 'unsupported' },
        response: { supported: false, reason: 'vendorSpecific' }
      });
      state.controlsComplete = true;
      receive({ data: { event: 'controlsChanged', state: structuredClone(state) } });
    };
    window.publishBuiltin = value => {
      state.revision++;
      state.displays[0].controls.brightness.value = value;
      receive({ data: { event: 'controlsChanged', state: structuredClone(state) } });
    };
    window.chrome ??= {};
    window.chrome.webview = {
      addEventListener(_, listener) { receive = listener; },
      postMessage(message) {
        calls.push(message);
        if (message.method === 'control') {
          state.revision++;
          state.displays.find(display => display.id === message.params.displayId)
            .controls[message.params.control].value = message.params.value;
        }
        const result = structuredClone(state);
        if (message.method === 'displays' && window.holdRead) {
          window.releaseRead = () => receive({ data: { id: message.id, result } });
        } else setTimeout(() => receive({ data: { id: message.id, result } }), 25);
      }
    };
    sessionStorage.setItem('display-selected', selected);
  }, selected);
}

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  try {
    const basic = await browser.newPage({ viewport: { width: 1280, height: 880 } });
    await install(basic);
    await basic.goto(pathToFileURL(path.resolve(__dirname, '../basic/index.html')).href + '?lang=en');
    await basic.waitForFunction(() => window.desktopReady);
    assert.equal(await basic.locator('.screen-option').count(), 2);
    assert.ok(await basic.locator('#brightness').isEnabled(), 'built-in control must be ready while external probe waits');
    await basic.waitForTimeout(550);
    assert.equal(await basic.evaluate(() => calls.filter(call => call.method === 'displays').length), 1,
      'background incomplete snapshots must not cause polling');
    await basic.locator('[data-native-device="external"]').click();
    assert.equal(await basic.locator('#brightness-value').textContent(), '—');
    assert.ok(await basic.locator('#brightness').isDisabled());
    assert.ok(await basic.locator('#resolution').isEnabled(), 'Windows display modes stay usable during DDC probing');
    assert.equal(await basic.locator('#brightness-hint').textContent(), '', 'Background communication is silent');
    await basic.locator('[data-native-device="builtin"]').click();
    await basic.locator('#brightness').evaluate(input => {
      input.value = 70; input.dispatchEvent(new Event('change', { bubbles: true }));
    });
    await basic.waitForFunction(() => !document.querySelector('#brightness').disabled);
    assert.equal(await basic.locator('#brightness-value').textContent(), '70%');
    await basic.evaluate(() => publishControls());
    assert.equal(await basic.locator('#brightness-value').textContent(), '70%');
    assert.equal(await basic.evaluate(() => calls.filter(call => call.method === 'displays').length), 1);

    await basic.evaluate(() => { holdRead = true; mockEvent({ event: 'displaysChanged' }); });
    await basic.waitForFunction(() => typeof releaseRead === 'function');
    await basic.evaluate(() => { publishBuiltin(71); releaseRead(); });
    await basic.waitForFunction(() => !document.querySelector('#brightness').disabled);
    assert.equal(await basic.locator('#brightness-value').textContent(), '71%', 'older RPC snapshot cannot overwrite a newer controls event');
    await basic.close();

    const color = await browser.newPage({ viewport: { width: 1280, height: 880 } });
    await install(color, 'external');
    await color.goto(pathToFileURL(path.resolve(__dirname, '../color/index.html')).href + '?lang=en');
    await color.waitForFunction(() => window.desktopReady);
    assert.equal(await color.locator('#contrast-value').textContent(), '—');
    assert.equal(await color.locator('#response-unsupported').textContent(), '—');
    assert.ok(await color.locator('#contrast').isDisabled());
    assert.ok(await color.locator('#hdr').isEnabled(), 'HDR is managed by Windows and must not wait for DDC');
    for (const locale of ['zh-CN', 'ja', 'ko', 'en']) {
      await color.locator('#ui-language').selectOption(locale);
      assert.equal(await color.locator('#contrast-value').textContent(), '—');
      assert.equal(await color.locator('#response-unsupported').textContent(), '—');
    }
    await color.evaluate(() => publishControls(true));
    assert.equal(await color.locator('#contrast-value').textContent(), 'Unsupported');
    assert.doesNotMatch(await color.locator('#contrast-hint').textContent(), /DDC\/CI|communicat/i);
    assert.ok(await color.locator('#sharpness').isEnabled());
    assert.equal(await color.locator('#sharpness-value').textContent(), '8');
    await color.close();

    const multi = await browser.newPage({ viewport: { width: 1280, height: 880 } });
    await install(multi);
    await multi.goto(pathToFileURL(path.resolve(__dirname, '../multi-display/index.html')).href + '?lang=en');
    await multi.waitForFunction(() => window.desktopReady);
    await multi.locator('#display-position').selectOption('top');
    assert.ok(await multi.locator('#native-apply').isEnabled());
    await multi.evaluate(() => publishControls());
    assert.ok(await multi.locator('#native-apply').isEnabled(), 'background capabilities must preserve unapplied arrangement');
    assert.equal(await multi.locator('#display-position').inputValue(), 'top');
    await multi.locator('#native-discard').click();
    // A smaller viewport adapts its arrangement view without scaling text or controls.
    await multi.setViewportSize({ width: 660, height: 470 });
    await multi.waitForFunction(() => window.monitorAppearance?.scale === 1);
    await multi.waitForTimeout(100);
    const first = multi.locator('.native-monitor[data-native-device="builtin"]');
    const second = multi.locator('.native-monitor[data-native-device="external"]');
    const a = await first.boundingBox();
    const b = await second.boundingBox();
    await multi.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
    await multi.mouse.down();
    await multi.mouse.move(b.x + b.width / 2, b.y + b.height * .85, { steps: 6 });
    const translated = await first.boundingBox();
    assert.ok(Math.abs((translated.x + translated.width / 2) - (b.x + b.width / 2)) < 3, 'adaptive arrangement drag follows the pointer');
    await multi.mouse.up();
    assert.equal(await multi.locator('#display-position').inputValue(), 'bottom');
    assert.ok(await multi.locator('#native-apply').isEnabled());
    await multi.close();
    console.log('PASS progressive device rendering, independent controls, no polling, revisions, quiet loading, failure states, drafts and adaptive drag');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
