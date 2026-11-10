const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));

// A deliberately slow, revisioned fake monitor. No Windows settings are written.
async function install(page) {
  await page.addInitScript(() => {
    const range = value => ({ supported: true, value, min: 0, max: 100, step: 1 });
    const device = (id, kind, x) => ({ id, name: id, kind, x, y: 0, active: true, primary: x === 0,
      devicePath: 'DISPLAY#' + id, gdiName: 'DISPLAY' + x, width: 1920, height: 1080, orientation: 0, refreshRate: 60,
      modes: [60, 120].map(refreshRate => ({ width: 1920, height: 1080, orientation: 0, refreshRate })),
      controls: { brightness: range(50), contrast: range(50), sharpness: range(20),
        temperature: { supported: false, reason: 'unsupported' }, response: { supported: false, reason: 'vendorSpecific' },
        hdr: { supported: true, value: 0 }, resolution: { supported: true }, refreshRate: { supported: true } } });
    window.state = { revision: 1, mode: 'extend', pending: false, seconds: 0, controlsComplete: true, backgroundControls: true,
      displays: [device('builtin', 'builtin', 0), device('external', 'external', 1920)] };
    let receive;
    window.calls = [];
    window.writeDelay = 180;
    window.readDelay = 15;
    window.writesInFlight = 0;
    window.maxWritesInFlight = 0;
    window.failWrite = false;
    window.emitState = () => { state.revision++; receive({ data: { event: 'controlsChanged', state: structuredClone(state) } }); };
    window.emit = data => receive({ data });
    window.chrome ??= {};
    window.chrome.webview = {
      addEventListener(_, listener) { receive = listener; },
      postMessage(message) {
        calls.push({ ...message, at: performance.now() });
        const isWrite = message.method === 'control';
        const fail = isWrite && window.failWrite;
        if (isWrite) {
          window.failWrite = false;
          window.writesInFlight++;
          window.maxWritesInFlight = Math.max(window.maxWritesInFlight, window.writesInFlight);
          if (!fail) {
            state.displays.find(display => display.id === message.params.displayId).controls[message.params.control].value = message.params.value;
            state.revision++;
          }
        }
        const result = structuredClone(state);
        setTimeout(() => {
          if (isWrite) window.writesInFlight--;
          receive({ data: fail ? { id: message.id, error: { code: 'readbackFailed' } } : { id: message.id, result } });
        }, isWrite ? window.writeDelay : window.readDelay);
      }
    };
  });
}

async function value(page, selector, next, change = false) {
  await page.locator(selector).evaluate((input, { next, change }) => {
    input.value = next;
    input.dispatchEvent(new Event(change ? 'change' : 'input', { bubbles: true }));
  }, { next, change });
}
async function settled(page) {
  await page.waitForFunction(() => !window.writesInFlight);
  // A queue flush runs in the same microtask as the preceding reply.
  await page.waitForTimeout(30);
  assert.equal(await page.evaluate(() => window.writesInFlight), 0);
}

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 880 } });
    page.setDefaultTimeout(6000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await install(page);
    await page.goto(pathToFileURL(path.resolve(__dirname, '../basic/index.html')).href + '?lang=en');
    await page.waitForFunction(() => window.desktopReady);
    const writes = () => page.evaluate(() => calls.filter(call => call.method === 'control'));

    await value(page, '#brightness', 60);
    assert.equal((await writes()).length, 1, 'write starts before pointer release');
    await value(page, '#brightness', 70);
    await value(page, '#brightness', 80);
    await page.evaluate(() => { state.displays[0].controls.brightness.value = 55; emitState(); });
    assert.equal(await page.locator('#brightness').inputValue(), '80', 'a background snapshot cannot pull the thumb backwards');
    assert.ok(await page.locator('#brightness').isEnabled(), 'the active slider remains usable during DDC/CI');
    assert.ok(await page.locator('#refresh-rate').isDisabled(), 'topology changes wait for the gesture to finish');
    await value(page, '#brightness', 80, true);
    await settled(page);
    assert.deepEqual((await writes()).map(call => call.params.value), [60, 80], 'only the latest queued value is sent');
    assert.equal(await page.locator('#brightness').inputValue(), '80');
    assert.ok(await page.locator('#refresh-rate').isEnabled());

    // A sustained gesture produces intermediate real writes, not one write at
    // the end, while a fast pointer never creates a hardware command backlog.
    await page.evaluate(() => { window.writeDelay = 90; });
    const start = (await writes()).length;
    for (let next = 81; next <= 92; next++) { await value(page, '#brightness', next); await page.waitForTimeout(20); }
    const during = (await writes()).length - start;
    assert.ok(during >= 2 && during < 12, 'sustained dragging streams coalesced intermediate updates');
    await value(page, '#brightness', 92, true);
    await settled(page);
    assert.equal((await writes()).at(-1).params.value, 92, 'the final requested value is not lost');
    assert.equal(await page.evaluate(() => window.maxWritesInFlight), 1);

    // Alt-Tab may blur the window without a range change or element blur.
    await value(page, '#brightness', 59);
    await settled(page);
    assert.ok(await page.locator('#refresh-rate').isDisabled());
    await page.evaluate(() => window.dispatchEvent(new Event('blur')));
    assert.ok(await page.locator('#refresh-rate').isEnabled(), 'window blur flushes an acknowledged gesture and unlocks modes');

    // Selection and same-document navigation must not retarget queued values.
    await page.evaluate(() => { window.writeDelay = 180; });
    const switchStart = (await writes()).length;
    await value(page, '#brightness', 65);
    await value(page, '#brightness', 66);
    await page.locator('[data-native-device="external"]').click();
    await value(page, '#brightness', 35);
    await value(page, '#brightness', 36, true);
    await page.evaluate(() => { window.oldRange = document.querySelector('#brightness'); desktopNavigate('color'); });
    await settled(page);
    const switched = (await writes()).slice(switchStart);
    assert.deepEqual(switched.map(call => [call.params.displayId, call.params.value]), [['builtin', 65], ['builtin', 66], ['external', 36]]);
    const beforeDetachedChange = (await writes()).length;
    await page.locator('[data-native-device="builtin"]').click();
    await page.evaluate(() => window.oldRange.dispatchEvent(new Event('change', { bubbles: true })));
    assert.equal((await writes()).length, beforeDetachedChange, 'a detached old slider cannot write to the newly selected device');
    await page.locator('[data-native-device="external"]').click();
    await value(page, '#contrast', 61);
    await value(page, '#contrast', 63);
    await page.evaluate(() => desktopNavigate('basic'));
    await settled(page);
    assert.equal((await writes()).at(-1).params.control, 'contrast');
    assert.equal((await writes()).at(-1).params.value, 63);
    assert.equal(await page.locator('#brightness').inputValue(), '36');

    // An actual browser pointer drag emits input/change just like native WebView.
    await page.locator('[data-native-device="builtin"]').click();
    const slider = await page.locator('#brightness').boundingBox();
    const pointerStart = (await writes()).length;
    await page.mouse.move(slider.x + slider.width * .3, slider.y + slider.height / 2);
    await page.mouse.down();
    await page.mouse.move(slider.x + slider.width * .7, slider.y + slider.height / 2, { steps: 15 });
    assert.ok((await writes()).length > pointerStart);
    const finalPointerValue = Number(await page.locator('#brightness').inputValue());
    await page.mouse.up();
    await settled(page);
    assert.equal((await writes()).at(-1).params.value, finalPointerValue);
    assert.ok(await page.locator('#refresh-rate').isEnabled());

    const errorStart = (await writes()).length;
    await page.evaluate(() => { window.failWrite = true; window.readDelay = 250; });
    await page.locator('#brightness').dispatchEvent('pointerdown');
    await value(page, '#brightness', 44);
    await value(page, '#brightness', 45, true);
    await page.waitForFunction(() => [...document.querySelectorAll('.native-notice[data-error]')].some(notice => !notice.hidden));
    assert.ok(await page.locator('#brightness').isDisabled(), 'error readback suspends further inputs');
    await value(page, '#brightness', 46);
    await page.waitForFunction(() => !document.querySelector('#brightness').disabled);
    await value(page, '#brightness', 46);
    await page.waitForTimeout(100);
    assert.equal((await writes()).length, errorStart + 1, 'failed writes cancel unsent values instead of repeatedly retrying');
    await page.locator('#brightness').dispatchEvent('pointerup');
    await page.evaluate(() => { window.readDelay = 15; });
    assert.ok(await page.locator('#brightness').isEnabled());

    const keyErrorStart = (await writes()).length;
    await page.evaluate(() => { window.failWrite = true; });
    await page.locator('#brightness').dispatchEvent('keydown', { key: 'ArrowRight', repeat: false });
    await value(page, '#brightness', 53);
    await settled(page);
    await page.locator('#brightness').dispatchEvent('keydown', { key: 'ArrowRight', repeat: true });
    await value(page, '#brightness', 54);
    assert.equal((await writes()).length, keyErrorStart + 1, 'held keyboard repeats cannot restart a failed gesture');
    await page.locator('#brightness').dispatchEvent('keyup', { key: 'ArrowRight' });

    const pendingStart = (await writes()).length;
    await value(page, '#brightness', 47);
    await value(page, '#brightness', 48);
    await page.evaluate(() => { state.pending = true; state.seconds = 20; emitState(); });
    await settled(page);
    assert.equal((await writes()).length, pendingStart + 1, 'pending confirmation cancels unsent continuous writes');
    assert.ok(await page.locator('#brightness').isDisabled());
    assert.ok(await page.locator('.native-confirm').isVisible(), 'an older control reply cannot clear pending confirmation');
    await page.evaluate(() => { state.pending = false; state.seconds = 0; emitState(); });

    const disconnectedStart = (await writes()).length;
    await value(page, '#brightness', 51);
    await value(page, '#brightness', 52);
    await page.evaluate(() => { state.displays[0].active = false; state.revision++; emit({ event: 'displaysChanged' }); });
    await settled(page);
    await page.waitForFunction(() => document.querySelector('#brightness').disabled);
    assert.equal((await writes()).length, disconnectedStart + 1, 'hotplug invalidation drops unsent device values');
    assert.deepEqual(errors, []);
    console.log('PASS live pointer/keyboard slider writes, coalescing, final readback, no thumb rollback, device/route identity, failure/pending/hotplug cancellation');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
