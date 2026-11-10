const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));

async function install(page) {
  await page.addInitScript(() => {
    window.documentIdentity = crypto.randomUUID();
    const range = value => ({ supported: true, value, min: 0, max: 100, step: 1 });
    const unsupported = reason => ({ supported: false, reason });
    const device = (id, kind, x, active = true) => ({
      id, name: id === 'builtin' ? 'Internal panel' : id === 'external' ? 'Desk panel' : 'Travel display', kind,
      active, primary: id === 'builtin', x, y: 0, width: 1920, height: 1080, orientation: 0, refreshRate: 60,
      devicePath: 'DISPLAY#' + id, gdiName: 'DISPLAY' + x, controlsLoading: false,
      modes: [{ width: 1920, height: 1080, orientation: 0, refreshRate: 60 }, { width: 1920, height: 1080, orientation: 0, refreshRate: 120 }],
      controls: active ? { brightness: range(50), contrast: range(60), sharpness: range(8), temperature: unsupported('unsupported'),
        hdr: { supported: true, value: 0 }, response: unsupported('vendorSpecific'), resolution: { supported: true },
        refreshRate: { supported: true }, orientation: { supported: true }, scale: unsupported('windowsManaged') }
        : Object.fromEntries(['brightness', 'contrast', 'sharpness', 'temperature', 'hdr', 'response'].map(name => [name, unsupported('inactive')]))
    });
    const state = { revision: 1, backgroundControls: true, controlsComplete: true, mode: 'extend', pending: false, seconds: 0,
      modeCapabilities: Object.fromEntries(['extend', 'mirror', 'builtin', 'external'].map(mode => [mode, { supported: true }])),
      displays: [device('builtin', 'builtin', 0), device('external', 'external', 1920), device('inactive', 'external', 0, false)] };
    let receive;
    window.calls = [];
    window.mockPending = value => {
      state.pending = value; state.seconds = value ? 20 : 0; state.revision++;
      receive({ data: { event: 'controlsChanged', state: structuredClone(state) } });
    };
    window.mockLoading = () => {
      state.displays[1].controlsLoading = true;
      state.displays[1].controls.brightness = unsupported('loading');
      state.revision++;
      receive({ data: { event: 'controlsChanged', state: structuredClone(state) } });
    };
    window.chrome ??= {};
    window.chrome.webview = {
      addEventListener(_, listener) { receive = listener; },
      postMessage(message) {
        calls.push(message);
        if (message.method === 'control') {
          state.displays.find(device => device.id === message.params.displayId).controls[message.params.control].value = message.params.value;
          if (message.params.control === 'hdr') { state.pending = true; state.seconds = 20; }
          state.revision++;
        }
        if (message.method === 'confirm' || message.method === 'revert') {
          state.pending = false; state.seconds = 0; state.revision++;
        }
        const result = structuredClone(state);
        setTimeout(() => receive({ data: { id: message.id, result } }), 10);
      }
    };
  });
}

async function reveal(page, selector) {
  if (await page.locator(selector).isVisible()) return;
  for (const section of await page.locator('.adaptive-sections button[data-section]').evaluateAll(buttons => buttons.map(button => button.dataset.section))) {
    await page.locator(`.adaptive-sections button[data-section="${section}"]`).click();
    if (await page.locator(selector).isVisible()) return;
  }
  throw new Error('Control not reachable through section navigation: ' + selector);
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
    const identity = await page.evaluate(() => documentIdentity);
    const countReads = () => page.evaluate(() => calls.filter(call => call.method === 'displays').length);
    assert.equal(await countReads(), 1);
    await page.locator('[data-native-device="external"]').click();
    await page.locator('.tabs a').nth(1).click();
    await page.waitForFunction(() => document.documentElement.dataset.desktopRoute === 'multi-display');
    await reveal(page, '#display-position');
    await page.locator('#display-position').selectOption('top');
    await reveal(page, '#native-orientation-2');
    await page.locator('#native-orientation-2').selectOption('90');
    assert.ok(await page.locator('#native-apply').isEnabled());
    await page.locator('.tabs a').nth(2).click();
    assert.match(await page.locator('#color-selected').innerText(), /Desk panel/);
    await page.locator('.tabs a').nth(1).click();
    await reveal(page, '#display-position');
    await page.waitForFunction(() => document.querySelectorAll('.native-map .native-monitor').length === 2);
    assert.equal(await page.locator('#display-position').inputValue(), 'top', 'Unapplied layout survives tab navigation');
    assert.ok(await page.locator('#native-apply').isEnabled());
    assert.equal(await page.evaluate(() => documentIdentity), identity, 'Switching tabs retains the same document');
    assert.equal(await countReads(), 1, 'Route switches reuse the snapshot without hardware reads');

    const durations = await page.evaluate(() => {
      const durations = [];
      const routes = ['basic', 'multi-display', 'color'];
      for (let index = 0; index < 24; index++) {
        const target = routes[index % routes.length];
        const start = performance.now();
        window.desktopNavigate(target);
        durations.push(performance.now() - start);
        if (document.documentElement.dataset.desktopRoute !== target) throw new Error('Route is not ready synchronously');
      }
      return durations;
    });
    assert.ok(Math.max(...durations) < 100, `Each cached switch should finish below100ms; observed${Math.max(...durations).toFixed(1)}ms`);
    assert.equal(await countReads(), 1);
    assert.equal(await page.evaluate(() => documentIdentity), identity);
    assert.equal(await page.locator('.tabs [aria-current="page"]').getAttribute('data-i18n'), 'colorTab');
    await page.goBack();
    await page.waitForFunction(() => document.documentElement.dataset.desktopRoute === 'multi-display');
    await page.goForward();
    await page.waitForFunction(() => document.documentElement.dataset.desktopRoute === 'color');
    assert.equal(await page.evaluate(() => documentIdentity), identity, 'History uses same-document routing');

    await page.locator('#ui-language').selectOption('ja');
    await page.locator('.tabs a').nth(0).click();
    assert.equal(await page.locator('html').getAttribute('lang'), 'ja');
    assert.ok((await page.title()).includes('基本設定'));
    await page.locator('#ui-theme').selectOption('dark');
    await page.locator('.tabs a').nth(2).click();
    assert.equal(await page.locator('#ui-theme').inputValue(), 'dark');
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark');
    await page.locator('#ui-language').selectOption('en');

    await reveal(page, '#hdr');
    await page.locator('#hdr').click();
    await page.waitForFunction(() => !document.querySelector('.native-confirm').hidden);
    await page.locator('.tabs a').nth(0).click();
    assert.ok(await page.locator('.native-confirm').isVisible(), 'Safety confirmation survives route switch');
    await page.getByRole('button', { name: 'Keep changes', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.native-confirm').hidden);
    assert.equal(await countReads(), 1, 'Confirm response updates state without a duplicate read');
    await page.locator('.tabs a').nth(1).click();
    await reveal(page, '#native-orientation-2');
    assert.equal(await page.locator('#native-orientation-2').inputValue(), '90', 'Confirming HDR must preserve unrelated staged orientation');
    await reveal(page, '#native-apply');
    assert.ok(await page.locator('#native-apply').isEnabled(), 'Unrelated layout draft remains applicable');
    await page.locator('.tabs a').nth(0).click();
    await page.evaluate(() => mockLoading());
    await page.locator('[data-native-device="external"]').click();
    assert.equal(await page.locator('#brightness-value').innerText(), '—', 'Unknown value is neutral, not a communication status');
    assert.ok(await page.locator('#brightness').isDisabled());
    assert.ok(!(await page.locator('body').innerText()).match(/Reading connected|Reading…|background read|communicat|正在读取|读取中|后台读取/i));
    assert.ok(await page.locator('.page-footer').isHidden(), 'Implementation footer is suppressed');

    await page.setViewportSize({ width: 640, height: 440 });
    for (const route of ['basic', 'multi-display', 'color']) {
      await page.evaluate(route => desktopNavigate(route), route);
      await page.waitForTimeout(80);
      assert.equal(await page.evaluate(() => window.monitorAppearance.scale), 1, 'Small windows do not scale the whole page');
      assert.equal(await page.evaluate(() => { scrollTo(100, 100); return scrollY; }), 0);
      if (route === 'multi-display') {
        await reveal(page, '#display-position');
        await page.waitForFunction(() => {
          const map = document.querySelector('.native-map');
          return map?.clientWidth > 100 && map.clientHeight >= 64 && map.querySelectorAll('.native-monitor').length === 2;
        });
      }
    }
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ passed: true, routeSwitches: durations.length, maxSwitchMs: Math.max(...durations), medianSwitchMs: durations.toSorted((a,b) => a-b)[12], reads: await countReads(), checks: ['same document', 'no tab hardware reads', 'draft retained', 'history', 'language/theme', 'pending retained', 'quiet communication', 'unscaled small window', 'arrangement after route return and resize'] }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
