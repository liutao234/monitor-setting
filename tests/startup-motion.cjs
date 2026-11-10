const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const { pathToFileURL } = require('node:url');
const { chromium } = require(path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));
const output = process.argv[2] || path.join(process.env.TEMP, 'monitor-startup-1.0.5');
fs.mkdirSync(output, { recursive: true });

// The topology and slower DDC capability response are released independently.
// No request in this suite reaches Windows or changes a real display.
async function install(page, theme = 'light') {
  await page.addInitScript(theme => {
    localStorage.setItem('monitor-setting-theme', theme);
    sessionStorage.setItem('display-selected', 'builtin');
    const range = value => ({ supported: true, value, min: 0, max: 100, step: 1 });
    const unavailable = reason => ({ supported: false, reason });
    const device = (id, kind, x) => ({
      id, kind, name: kind === 'builtin' ? 'Internal panel' : 'Desk panel',
      active: true, primary: x === 0, x, y: 0, width: 1920, height: 1080, refreshRate: 120, orientation: 0,
      devicePath: 'DISPLAY#' + id, gdiName: 'DISPLAY' + x, controlsLoading: kind === 'external',
      modes: [60, 120].map(refreshRate => ({ width: 1920, height: 1080, orientation: 0, refreshRate })),
      controls: { brightness: kind === 'builtin' ? range(50) : unavailable('loading'),
        contrast: unavailable('loading'), sharpness: unavailable('loading'), temperature: unavailable('unsupported'),
        response: unavailable('vendorSpecific'), hdr: { supported: true, value: 0 },
        resolution: { supported: true }, refreshRate: { supported: true }, orientation: { supported: true },
        dynamicRefreshRate: unavailable('dynamicRefreshUnavailable') }
    });
    const state = { revision: 1, backgroundControls: true, controlsComplete: false,
      mode: 'extend', pending: false, seconds: 0,
      modeCapabilities: Object.fromEntries(['extend', 'mirror', 'builtin', 'external'].map(name => [name, { supported: true }])),
      displays: [device('builtin', 'builtin', 0), device('external', 'external', 1920)] };
    let receive, held;
    window.calls = [];
    window.releaseTopology = outcome => {
      const message = held;
      if (!message) throw new Error('No topology request is waiting');
      held = null;
      const data = outcome === 'error' ? { id: message.id, error: { code: 'readbackFailed' } }
        : { id: message.id, result: { ...structuredClone(state), ...(outcome === 'empty' ? { displays: [], controlsComplete: true } : {}) } };
      receive({ data });
    };
    window.publishControls = () => {
      state.revision++;
      state.controlsComplete = true;
      for (const display of state.displays) {
        display.controlsLoading = false;
        display.controls.brightness = range(display.kind === 'builtin' ? 50 : 65);
        display.controls.contrast = range(60);
        display.controls.sharpness = range(8);
      }
      receive({ data: { event: 'controlsChanged', state: structuredClone(state) } });
    };
    window.chrome ??= {};
    window.chrome.webview = {
      addEventListener(_, listener) { receive = listener; },
      postMessage(message) {
        calls.push(message);
        if (message.method === 'displays') held = message;
        else if (!['shellReady', 'appearance'].includes(message.method) && message.id)
          queueMicrotask(() => receive({ data: { id: message.id, result: structuredClone(state) } }));
      }
    };
  }, theme);
}

async function geometry(page, label) {
  const actual = await page.evaluate(() => {
    const visible = [...document.querySelectorAll('button, input, select, .tabs a')].filter(element =>
      element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden' && !element.closest('[hidden]'));
    const outside = visible.filter(element => {
      const box = element.getBoundingClientRect();
      return box.left < -1 || box.top < -1 || box.right > innerWidth + 1 || box.bottom > innerHeight + 1;
    }).map(element => element.id || element.className);
    window.scrollTo(1000, 1000);
    return { outside, scrollX, scrollY, scale: window.monitorAppearance?.scale,
      background: getComputedStyle(document.body).backgroundColor, intro: document.querySelector('.page-heading > p')?.textContent };
  });
  assert.deepEqual(actual.outside, [], label + ': controls fit the viewport');
  assert.equal(actual.scrollX, 0, label + ': no horizontal page scroll');
  assert.equal(actual.scrollY, 0, label + ': no vertical page scroll');
  assert.equal(actual.scale, 1, label + ': text is not scaled');
  assert.equal(actual.intro, undefined, label + ': requested sentence is removed');
  return actual;
}

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  const pageErrors = [];
  const checks = [];
  async function open(viewport, theme, locale = 'zh-CN', reducedMotion = 'no-preference') {
    const page = await browser.newPage({ viewport, colorScheme: theme === 'dark' ? 'dark' : 'light', reducedMotion });
    page.setDefaultTimeout(5000);
    page.on('pageerror', error => pageErrors.push(error.message));
    await install(page, theme);
    await page.goto(pathToFileURL(path.resolve(__dirname, '../basic/index.html')).href + '?lang=' + locale);
    await page.waitForFunction(() => calls.some(call => call.method === 'displays'));
    return page;
  }
  try {
    for (const [name, viewport, theme] of [
      ['large-light', { width: 1280, height: 880 }, 'light'],
      ['small-dark', { width: 496, height: 360 }, 'dark']
    ]) {
      const page = await open(viewport, theme);
      await page.waitForFunction(() => document.documentElement.dataset.startupPhase === 'waiting');
      assert.ok(await page.locator('#startup-scene').isVisible(), 'waiting phase visibly introduces symbolic monitors');
      assert.equal((await page.locator('#startup-scene').textContent()).trim(), '', 'startup animation has no fake device names, progress or communication text');
      assert.equal(await page.locator('#startup-scene').getAttribute('aria-hidden'), 'true');
      assert.equal(await page.evaluate(() => window.desktopReady), false);
      assert.equal(await page.locator('.basic-layout').evaluate(element => getComputedStyle(element).visibility), 'hidden', 'prototype devices are not exposed before discovery');
      await page.waitForFunction(() => calls.some(call => call.method === 'shellReady'));
      const initial = await geometry(page, name + ' waiting');
      assert.equal(initial.background, theme === 'dark' ? 'rgb(20, 20, 20)' : 'rgb(255, 255, 255)');
      await page.screenshot({ path: path.join(output, name + '-waiting.png') });
      await page.evaluate(() => releaseTopology());
      await page.waitForFunction(() => window.desktopReady);
      assert.equal(await page.locator('#startup-scene').count(), 0, 'first real state removes waiting scene without a minimum animation duration');
      assert.equal(await page.evaluate(() => document.documentElement.dataset.startupPhase), 'ready');
      assert.ok(await page.locator('#brightness').isEnabled(), 'available hardware is immediately usable while slower external controls load');
      assert.equal(await page.evaluate(() => window.desktopControlsReady), false);
      assert.equal(await page.locator('[data-native-device]').count(), 2);
      const revealCount = await page.evaluate(() => window.monitorStartup.revealCount);
      assert.ok(revealCount > 0, 'identified devices receive an initial entrance');
      await page.screenshot({ path: path.join(output, name + '-discovered.png') });
      await page.evaluate(() => publishControls());
      await page.waitForFunction(() => window.desktopControlsReady);
      await page.waitForTimeout(450);
      await geometry(page, name + ' ready');
      await page.screenshot({ path: path.join(output, name + '-ready.png') });
      await page.evaluate(() => { publishControls(); desktopNavigate('color'); desktopNavigate('multi-display'); desktopNavigate('basic'); });
      assert.equal(await page.evaluate(() => window.monitorStartup.revealCount), revealCount, 'hardware replies and route changes do not replay startup motion');
      assert.equal(await page.locator('#startup-scene').count(), 0);
      assert.equal(await page.evaluate(() => calls.filter(call => call.method === 'displays').length), 1);
      checks.push(name);
      await page.close();
    }

    for (const locale of ['zh-CN', 'ja', 'ko', 'en']) for (const theme of ['light', 'dark']) {
      const page = await open({ width: 496, height: 360 }, theme, locale, 'reduce');
      await page.waitForFunction(() => document.documentElement.dataset.startupPhase === 'waiting');
      assert.equal(await page.locator('#startup-scene').evaluate(element => element.getAnimations({ subtree: true }).length), 0, 'reduced motion keeps the waiting illustration still');
      await geometry(page, locale + '-' + theme + ' reduced waiting');
      await page.evaluate(() => releaseTopology());
      await page.waitForFunction(() => window.desktopReady);
      const movement = await page.evaluate(() => document.getAnimations().some(animation =>
        animation.effect?.getKeyframes().some(frame => frame.transform && frame.transform !== 'none')));
      assert.equal(movement, false, 'reduced-motion discovery has no spatial animation');
      await geometry(page, locale + '-' + theme + ' reduced ready');
      checks.push(locale + '-' + theme + '-reduced');
      await page.close();
    }

    for (const outcome of ['error', 'empty']) {
      const page = await open({ width: 744, height: 541 }, 'light');
      await page.evaluate(outcome => releaseTopology(outcome), outcome);
      await page.waitForFunction(outcome => document.documentElement.dataset.startupPhase === outcome, outcome);
      assert.equal(await page.locator('#startup-scene').count(), 0, outcome + ' stops waiting motion');
      const retry = page.locator('.native-notice:not([hidden]) button:not([hidden])').first();
      assert.ok(await retry.isEnabled(), outcome + ' remains recoverable');
      await retry.click();
      await page.waitForFunction(() => calls.filter(call => call.method === 'displays').length === 2);
      await page.evaluate(() => releaseTopology());
      await page.waitForFunction(() => window.desktopReady);
      assert.equal(await page.locator('#startup-scene').count(), 0, 'retry does not trap the recovered interface behind startup motion');
      await geometry(page, outcome + ' recovery');
      checks.push(outcome + '-recovery');
      await page.close();
    }

    const duringNavigation = await open({ width: 744, height: 541 }, 'dark', 'en');
    await duringNavigation.locator('.tabs a[data-route="color"]').click();
    assert.ok(await duringNavigation.locator('#startup-scene').isVisible(), 'changing category during discovery keeps an appropriate waiting state');
    await duringNavigation.evaluate(() => releaseTopology());
    await duringNavigation.waitForFunction(() => window.desktopReady);
    assert.ok(await duringNavigation.locator('#hdr').isEnabled());
    assert.equal(await duringNavigation.evaluate(() => calls.filter(call => call.method === 'displays').length), 1);
    await duringNavigation.close();
    checks.push('navigation-during-discovery');
    assert.deepEqual(pageErrors, []);
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ passed: true, checks }, null, 2));
    console.log('PASS startup illustration, immediate real-device interactivity, one-time motion, themes, four languages, reduced motion, small layouts, error/empty recovery and navigation');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
