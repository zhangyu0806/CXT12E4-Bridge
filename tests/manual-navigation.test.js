// No browser, input or network side effects. Run: node tests/manual-navigation.test.js
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const source = fs.readFileSync(path.join(__dirname, '../docs/cxt12e4-manual/manual-navigation.js'), 'utf8');
const pages = {
  windows: { path: 'index.html', platform: 'windows', root: './', category: 'device' },
  mac: { path: 'mac/index.html', platform: 'mac', root: '../', category: 'device' },
  tmux: { path: 'mac/tmux.html', platform: 'mac', root: '../', category: 'shortcuts' },
  shortcuts: { path: 'mac/shortcuts.html', platform: 'mac', root: '../', category: 'shortcuts' },
  'windows-shortcuts': { path: 'shortcuts.html', platform: 'windows', root: './', category: 'shortcuts' }
};
const platforms = ['windows', 'mac'];
const sections = ['keyboard', 'xbox', 'shortcuts'];
const relativePath = (platform, category) => (platform === 'mac' ? 'mac/' : '') + (category === 'shortcuts' ? 'shortcuts.html' : 'index.html');

function load(page, search = '', href) {
  const definition = pages[page];
  const location = { href: href || 'http://localhost/cxt12e4/' + definition.path + search };
  Object.defineProperty(location, 'search', { get() { return new URL(this.href).search; } });
  function anchor(dataset, fallback) {
    return {
      dataset, target: '', attributes: { href: fallback }, events: {},
      get href() { return new URL(this.attributes.href, location.href).href; },
      set href(value) { this.attributes.href = String(value); },
      addEventListener(name, callback) { this.events[name] = callback; },
      hasAttribute(name) { return Object.hasOwn(this.attributes, name); },
      getAttribute(name) { return this.attributes[name] ?? null; },
      setAttribute(name, value) { this.attributes[name] = String(value); },
      removeAttribute(name) { delete this.attributes[name]; }
    };
  }
  const platformLinks = platforms.map(name => anchor({ manualPlatform: name }, definition.root + relativePath(name, definition.category)));
  const sectionLinks = sections.map(name => anchor({ manualSection: name }, definition.root + relativePath(definition.platform, name) + (name === 'shortcuts' ? '' : '?device=' + name)));
  const links = [...platformLinks, ...sectionLinks];
  const calls = [], entries = [location.href];
  let position = 0;
  const history = {
    state: { preserved: true },
    pushState(state, title, url) {
      assert.equal(state, this.state, 'History metadata is retained');
      calls.push('push'); location.href = String(url);
      entries.splice(++position); entries.push(location.href);
    },
    replaceState(state, title, url) {
      assert.equal(state, this.state, 'History metadata is retained');
      calls.push('replace'); location.href = String(url); entries[position] = location.href;
    },
    back() { if (position) location.href = entries[--position]; },
    forward() { if (position < entries.length - 1) location.href = entries[++position]; }
  };
  const styles = {};
  const document = {
    body: { dataset: { manualPage: page, manualRoot: definition.root } },
    documentElement: { style: { setProperty(name, value) { styles[name] = value; } } },
    querySelectorAll(selector) {
      if (selector === 'a[data-manual-platform]') return platformLinks;
      if (selector === 'a[data-manual-section]') return sectionLinks;
      throw new Error('Unexpected selector: ' + selector);
    }
  };
  const window = { location, history };
  new vm.Script(source).runInNewContext({ window, document, URL, URLSearchParams });
  const api = window.ManualNavigation;
  const read = (...args) => JSON.parse(JSON.stringify(api.read(...args)));
  return { page, definition, api, read, links, platformLinks, sectionLinks, location, history, calls, entries, styles,
    platform: name => platformLinks.find(link => link.dataset.manualPlatform === name),
    section: name => sectionLinks.find(link => link.dataset.manualSection === name) };
}

function assertLinks(manual, device, layer) {
  const root = new URL(manual.definition.root, manual.location.href);
  function target(link, platform, category, destinationDevice) {
    const expected = new URL(relativePath(platform, category), root);
    expected.searchParams.set('device', destinationDevice);
    expected.searchParams.set('layer', layer);
    assert.equal(link.href, expected.href, 'Correct platform/category destination');
  }
  for (const platform of platforms) {
    if (platform === manual.definition.platform) {
      assert.equal(manual.platform(platform).href, manual.location.href, 'Selected platform retains the exact current URL');
    } else target(manual.platform(platform), platform, manual.definition.category, device);
    assert.equal(manual.platform(platform).getAttribute('aria-current') === 'true', platform === manual.definition.platform);
  }
  for (const section of sections) {
    target(manual.section(section), manual.definition.platform, section, section === 'shortcuts' ? device : section);
    const selected = manual.definition.category === 'shortcuts' ? 'shortcuts' : device;
    assert.equal(manual.section(section).getAttribute('aria-current') === 'true', section === selected);
  }
}

function click(link, options = {}) {
  const event = { button: 0, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...options };
  link.events.click(event);
  return event.defaultPrevented;
}

for (const page of Object.keys(pages)) {
  const manual = load(page, '?device=xbox&layer=6&rev=keep#section');
  assertLinks(manual, 'xbox', 6);
  assert.equal(manual.calls.length, 0, 'Initialization never changes browser history');
  const original = manual.location.href;
  assert.equal(click(manual.platform(manual.definition.platform)), true, 'Current platform retains the current view');
  assert.equal(manual.location.href, original, 'Current platform preserves tmux, query and anchor');
  const other = manual.definition.platform === 'windows' ? 'mac' : 'windows';
  assert.equal(click(manual.platform(other)), false, 'Cross-platform navigation stays a real link');
  const otherPage = manual.definition.category === 'shortcuts' ? (other === 'mac' ? 'shortcuts' : 'windows-shortcuts') : other;
  assertLinks(load(otherPage, '', manual.platform(other).href), 'xbox', 6);
  if (manual.definition.category === 'shortcuts') {
    for (const section of sections) {
      assert.equal(click(manual.section(section)), section === 'shortcuts' && page !== 'tmux',
        'Reference sections navigate normally; only the already selected shortcut page stays put');
    }
  } else {
    assert.equal(click(manual.section('shortcuts')), false, 'Device manuals navigate to their own shortcut page');
    assert.equal(click(manual.section('keyboard')), false, 'An unregistered device callback cannot swallow navigation');
  }
  for (const link of manual.links) {
    for (const modifier of ['metaKey', 'ctrlKey', 'shiftKey', 'altKey']) {
      assert.equal(click(link, { [modifier]: true }), false, 'Modified clicks stay native');
    }
    assert.equal(click(link, { button: 1 }), false, 'Middle click stays native');
    link.target = '_blank';
    assert.equal(click(link), false, 'New-tab links stay native');
    link.target = '';
    link.attributes.download = '';
    assert.equal(click(link), false, 'Download links stay native');
    delete link.attributes.download;
    const event = { button: 0, defaultPrevented: true, preventDefault() { assert.fail('Already handled clicks must not be intercepted'); } };
    link.events.click(event);
  }

  manual.api.write('keyboard', 3);
  assertLinks(manual, 'keyboard', 3);
  assert.deepEqual(manual.calls, ['push']);
  assert.equal(new URL(manual.location.href).searchParams.get('rev'), 'keep');
  assert.equal(new URL(manual.location.href).hash, '#section');
  manual.api.write('keyboard', 3);
  assert.deepEqual(manual.calls, ['push'], 'Selecting the same view does not duplicate history');
  manual.api.write('xbox', 2, 'replace');
  assert.deepEqual(manual.calls, ['push', 'replace']);
  assertLinks(manual, 'xbox', 2);
  manual.history.back();
  const restored = manual.read(manual.location.search);
  assert.deepEqual(restored, { device: 'xbox', layer: 6 });
  manual.api.write(restored.device, restored.layer, 'none');
  assertLinks(manual, 'xbox', 6);
  assert.deepEqual(manual.calls, ['push', 'replace'], 'Popstate rendering adds no new history');
  manual.history.forward();
  assert.deepEqual(manual.read(manual.location.search), { device: 'xbox', layer: 2 });
}

for (const page of ['windows', 'mac']) {
  const manual = load(page, '?device=keyboard&layer=5&rev=keep#section');
  const observed = [], stale = [];
  manual.api.onDeviceChange((device, layer) => stale.push([device, layer]));
  manual.api.onDeviceChange((device, layer) => {
    observed.push([device, layer]);
    manual.api.write(device, layer); // The index-page rendering callback owns its history write.
  });
  assert.equal(click(manual.section('keyboard')), true);
  assert.deepEqual(observed, [], 'An already selected device does not reset the view or call the renderer');
  assert.deepEqual(manual.calls, []);
  for (const options of [{ metaKey: true }, { ctrlKey: true }, { shiftKey: true }, { altKey: true }, { button: 1 }]) {
    assert.equal(click(manual.section('xbox'), options), false);
  }
  assert.deepEqual(observed, [], 'Native clicks do not invoke the in-page renderer');
  assert.equal(click(manual.section('xbox')), true);
  assert.deepEqual(observed, [['xbox', 5]], 'Same-page device switching invokes the renderer exactly once with its layer');
  assert.deepEqual(stale, [], 'Registering a callback replaces the previous callback');
  assertLinks(manual, 'xbox', 5);
  assert.deepEqual(manual.calls, ['push'], 'The device switch creates exactly one history entry');
  assert.equal(new URL(manual.location.href).searchParams.get('rev'), 'keep');
  assert.equal(new URL(manual.location.href).hash, '#section');
  assert.equal(click(manual.section('xbox')), true);
  assert.deepEqual(observed, [['xbox', 5]], 'Repeated device selection is stable');
  assert.deepEqual(manual.calls, ['push']);
  assert.equal(click(manual.section('keyboard')), true);
  assert.deepEqual(observed, [['xbox', 5], ['keyboard', 5]]);
  assertLinks(manual, 'keyboard', 5);
}

// Every route works on the dedicated domain, a subdirectory and a local copy.
// Explicit index filenames avoid root redirects and local directory listings.
for (const base of ['https://cxt12e4.11166666.xyz/', 'http://localhost/cxt12e4/', 'file:///tmp/manual copy/']) {
  for (const [page, definition] of Object.entries(pages)) {
    const current = new URL(definition.path, base);
    current.search = '?device=xbox&layer=6';
    const manual = load(page, '', current.href);
    assertLinks(manual, 'xbox', 6);
    for (const platform of platforms) {
      const nextPage = platform === definition.platform ? page :
        (definition.category === 'shortcuts' ? (platform === 'mac' ? 'shortcuts' : 'windows-shortcuts') : platform);
      const next = load(nextPage, '', manual.platform(platform).href);
      assertLinks(next, 'xbox', 6);
    }
  }
}
// Old bookmarks using directory paths retain the same deployment base.
for (const base of ['https://cxt12e4.11166666.xyz/', 'http://localhost/cxt12e4/']) {
  assertLinks(load('windows', '', base + '?device=xbox&layer=6'), 'xbox', 6);
  assertLinks(load('mac', '', base + 'mac/?device=xbox&layer=6'), 'xbox', 6);
}
const { read } = load('windows');
for (let layer = 1; layer <= 8; layer++) {
  assert.deepEqual(read('?device=xbox&layer=' + layer), { device: 'xbox', layer });
}
assert.deepEqual(read('?device=xbox&layer=0'), { device: 'xbox', layer: 8 }, 'Legacy layer zero is L8');
for (const value of ['1.5', '-1', '9', 'NaN', 'Infinity', '01', '1e0', '1.0', '', ' 2 ']) {
  assert.deepEqual(read('?device=invalid&layer=' + encodeURIComponent(value)), { device: 'keyboard', layer: 8 }, 'Reject invalid layer ' + value);
}
assert.deepEqual(read('', 2), { device: 'keyboard', layer: 2 });
assert.deepEqual(read('', 1.5), { device: 'keyboard', layer: 8 });
assert.deepEqual(read('?device=XBOX&layer=3'), { device: 'keyboard', layer: 3 });
for (const page of Object.keys(pages)) assertLinks(load(page), 'keyboard', page === 'tmux' ? 2 : 8);
const legacy = load('mac', '?device=xbox&layer=0');
legacy.api.write('xbox', 8, 'replace');
assert.deepEqual(legacy.calls, ['replace']);
assert.equal(new URL(legacy.location.href).searchParams.get('layer'), '8');

function contrastRatio(left, right) {
  const brightness = value => {
    const channels = value.slice(1).match(/../g).map(pair => parseInt(pair, 16) / 255);
    const linear = channels.map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
    return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2];
  };
  const values = [brightness(left), brightness(right)].sort((a, b) => a - b);
  return (values[1] + 0.05) / (values[0] + 0.05);
}
const themed = load('windows');
for (const accent of ['#C000C8', '#FF1464', '#FF6000', '#8000FF', '#E5B900', '#00A7D6', '#00C853', '#344054']) {
  const result = themed.api.theme(accent);
  assert(contrastRatio(accent, result.ink) >= 4.5, accent + ' selected controls have readable text');
  assert(contrastRatio('#ffffff', result.text) >= 4.5, accent + ' accent links have readable text');
  assert.equal(themed.styles['--accent-ink'], result.ink);
  assert.equal(themed.styles['--accent-text'], result.text);
  assert.equal(themed.styles['--accent'], undefined, 'Theme never changes the layer color');
}
const previousStyles = { ...themed.styles };
assert.equal(themed.api.theme('not a color'), null);
assert.deepEqual(themed.styles, previousStyles, 'Invalid theme values leave the current theme intact');

console.log('PASS: two-level navigation across five manuals; platform/category isolation, in-page device callbacks, native link behavior, root/subdirectory/file URLs, strict query parsing, preserved history and readable layer colors.');
