const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));

// Mock transport only: this regression test never calls Windows display APIs.
(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 880 } });
    page.setDefaultTimeout(5000);
    await page.addInitScript(() => {
      const range = { supported: true, value: 50, min: 0, max: 100, step: 1 };
      const state = { mode: 'builtin', pending: false, seconds: 0, displays: [{
        id: 'builtin', name: 'Internal panel', kind: 'builtin', active: true, primary: true,
        x: 0, y: 0, width: 1920, height: 1080, orientation: 0, refreshRate: 60,
        modes: [{ width: 1920, height: 1080, orientation: 0, refreshRate: 60 }],
        controls: { brightness: range, hdr: { supported: false, reason: 'unsupported' } }
      }] };
      let receive;
      window.calls = [];
      window.failNextRead = false;
      window.readDelay = 60;
      window.pendingWrites = 0;
      window.mockEvent = (data) => receive({ data });
      window.chrome ??= {};
      window.chrome.webview = {
        addEventListener(_, listener) { receive = listener; },
        postMessage(message) {
          calls.push({ ...message, sentAt: Date.now() });
          if (message.method === 'control') window.pendingWrites++;
          if (message.method === 'control') state.displays[0].controls[message.params.control].value = message.params.value;
          const fail = message.method === 'displays' && window.failNextRead;
          if (fail) window.failNextRead = false;
          setTimeout(() => {
            if (message.method === 'control') window.pendingWrites--;
            receive({ data: fail ? { id: message.id, error: { code: 'driver' } }
            : { id: message.id, result: structuredClone(state) } });
          }, message.method === 'control' ? 250
              : message.method === 'displays' ? window.readDelay : 10);
        }
      };
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../basic/index.html')).href + '?lang=en');
    await page.waitForFunction(() => window.desktopReady);
    const count = (method) => page.evaluate(method => calls.filter(call => call.method === method).length, method);
    assert.equal(await count('displays'), 1);
    assert.ok(await page.evaluate(() => calls.every(call => call.deadlineUtcMs > call.sentAt + 89000 && call.deadlineUtcMs <= call.sentAt + 90000)));

    await page.locator('#brightness').evaluate(input => {
      for (const value of [51, 54, 57]) {
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
      }
    });
    assert.equal(await count('control'), 1, 'the first input writes while the drag is still active');
    assert.equal(await page.locator('#brightness').inputValue(), '57');
    assert.ok(await page.locator('#brightness').isEnabled(), 'a write must not disable the dragging slider');
    await page.locator('#brightness').dispatchEvent('change');
    await page.waitForFunction(() => calls.filter(call => call.method === 'control').length === 2 && !window.pendingWrites);
    assert.equal(await count('displays'), 1, 'write result must not trigger another full hardware query');
    await page.locator('#brightness').dispatchEvent('change');
    assert.equal(await count('control'), 2, 'unchanged slider value must not write to the device');

    await page.evaluate(() => {
      for (let index = 0; index < 20; index++) mockEvent({ event: 'displaysChanged' });
    });
    await page.waitForFunction(() => calls.filter(call => call.method === 'displays').length === 2);
    await page.waitForTimeout(300);
    assert.equal(await count('displays'), 2, 'one hotplug event burst should produce one refresh');
    await page.locator('#brightness').evaluate(input => {
      input.value = 60;
      input.dispatchEvent(new Event('change', { bubbles: true }));
    });
    await page.evaluate(() => {
      for (let index = 0; index < 20; index++) mockEvent({ event: 'displaysChanged' });
    });
    await page.waitForFunction(() => calls.filter(call => call.method === 'displays').length === 3);
    await page.waitForTimeout(300);
    assert.equal(await count('displays'), 3, 'events during a write require only one trailing refresh');

    await page.evaluate(() => { window.failNextRead = true; mockEvent({ event: 'displaysChanged' }); });
    await page.waitForFunction(() => window.desktopLastError === 'driver');
    await page.getByRole('button', { name: 'Detect again', exact: true }).click();
    await page.waitForFunction(() => !window.desktopLastError);
    const latest = await page.evaluate(() => calls.filter(call => call.method === 'displays').at(-1));
    assert.equal(latest.params.forceRefresh, true, 'manual retry must bypass hardware capability caches');
    assert.equal(await page.locator('#brightness').inputValue(), '60');
    const reads = await count('displays');
    await page.evaluate(() => { window.readDelay = 1000; mockEvent({ event: 'displaysChanged' }); });
    await page.waitForFunction(previous => calls.filter(call => call.method === 'displays').length > previous, reads);
    assert.ok(await page.locator('#brightness').isDisabled());
    await page.locator('#identify').click();
    await page.waitForFunction(() => calls.some(call => call.method === 'identify'));
    assert.ok(await page.locator('#brightness').isDisabled(), 'identify should bypass an unfinished hardware read');
    await page.getByRole('button', { name: 'Open Windows settings' }).click();
    await page.waitForFunction(() => calls.some(call => call.method === 'openSettings'));
    assert.ok(await page.locator('#brightness').isDisabled(), 'opening Windows settings should bypass an unfinished hardware read');
    console.log('PASS bridge deadlines, live slider/coalesced final/no-op, direct write responses, event coalescing, forced retry, concurrent local actions');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
