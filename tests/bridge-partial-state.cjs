const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));

// Hardware cache invalidation can make a fast write/confirm reply incomplete.
// Keep known capabilities for the same device until a complete background read.
(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  try {
    const page = await browser.newPage();
    page.setDefaultTimeout(5000);
    await page.addInitScript(() => {
      const range = value => ({ supported: true, value, min: 0, max: 100, step: 1 });
      const state = { controlsComplete: true, mode: 'external', pending: false, seconds: 0, displays: [{
        id: 'external', name: 'Desk panel', kind: 'external', active: true, primary: true,
        devicePath: 'DISPLAY#A', gdiName: 'DISPLAY1',
        x: 0, y: 0, width: 1920, height: 1080, orientation: 0, refreshRate: 60,
        modes: [{ width: 1920, height: 1080, orientation: 0, refreshRate: 60 }],
        controls: { brightness: range(50), contrast: range(55), sharpness: range(20),
          temperature: { supported: false, reason: 'unsupported' },
          response: { supported: false, reason: 'vendorSpecific' }, hdr: { supported: true, value: 0 } }
      }] };
      let receive;
      window.calls = [];
      window.partialReads = false;
      window.replaceDevice = () => { state.displays[0].devicePath = 'DISPLAY#B'; };
      window.mockEvent = (data) => receive({ data });
      window.chrome ??= {};
      window.chrome.webview = {
        addEventListener(_, listener) { receive = listener; },
        postMessage(message) {
          calls.push(message);
          const control = message.params.control;
          if (message.method === 'control') {
            state.displays[0].controls[control].value = message.params.value;
            if (control === 'hdr') { state.pending = true; state.seconds = 20; }
            else state.displays[0].controls.sharpness.value = 21;
          }
          if (message.method === 'confirm') {
            state.pending = false;
            state.seconds = 0;
            state.displays[0].controls.sharpness.value = 22;
          }
          const result = structuredClone(state);
          if (message.method !== 'displays' || window.partialReads) {
            result.controlsComplete = false;
            result.displays[0].controls = { hdr: result.displays[0].controls.hdr };
            if (control) result.displays[0].controls[control] = structuredClone(state.displays[0].controls[control]);
          }
          setTimeout(() => receive({ data: { id: message.id, result } }), message.method === 'displays' ? 450 : 10);
        }
      };
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../color/index.html')).href + '?lang=en');
    await page.waitForFunction(() => window.desktopReady);
    const readCount = () => page.evaluate(() => calls.filter(call => call.method === 'displays').length);
    assert.equal(await page.locator('#sharpness').inputValue(), '20');
    await page.locator('#contrast').evaluate(input => {
      input.value = 60;
      input.dispatchEvent(new Event('change', { bubbles: true }));
    });
    await page.waitForFunction(() => !document.querySelector('#contrast').disabled);
    assert.equal(await page.locator('#contrast').inputValue(), '60');
    assert.equal(await page.locator('#sharpness').inputValue(), '20', 'partial control response retains prior capabilities');
    await page.waitForFunction(() => document.querySelector('#sharpness').value === '21');
    assert.equal(await readCount(), 2, 'partial control response schedules one complete read');

    await page.locator('#hdr').click();
    await page.getByRole('button', { name: 'Keep changes' }).click();
    await page.waitForFunction(() => document.querySelector('.native-confirm').hidden);
    assert.equal(await page.locator('#sharpness').inputValue(), '21', 'partial nonpending confirm response retains prior capabilities');
    await page.waitForFunction(() => document.querySelector('#sharpness').value === '22');
    assert.equal(await readCount(), 3, 'partial confirm response schedules one complete read');

    await page.evaluate(() => { partialReads = true; mockEvent({ event: 'displaysChanged' }); });
    await page.waitForFunction(() => calls.filter(call => call.method === 'displays').length === 5);
    await page.waitForTimeout(800);
    assert.equal(await readCount(), 5, 'persistent partial replies cannot create an infinite refresh loop');
    await page.evaluate(() => { partialReads = false; mockEvent({ event: 'displaysChanged' }); });
    await page.waitForFunction(() => !document.querySelector('#sharpness').disabled);
    await page.waitForTimeout(700);

    await page.evaluate(() => { replaceDevice(); partialReads = true; mockEvent({ event: 'displaysChanged' }); });
    await page.waitForFunction(() => document.querySelector('#sharpness').value === '0');
    assert.ok(await page.locator('#sharpness').isDisabled(), 'different physical mapping must not reuse old hardware capabilities');
    console.log('PASS partial write/confirm snapshots, complete refresh, bounded retries, device mapping isolation');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
