// No browser, input or network side effects. Run: node tests/manual-navigation.test.js
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const source = fs.readFileSync(path.join(__dirname, '../docs/cxt12e4-manual/manual-navigation.js'), 'utf8');
const pages = {
  windows: { path: '/cxt12e4/', bases: ['./', 'mac/', 'mac/tmux.html', 'mac/shortcuts.html'] },
  mac: { path: '/cxt12e4/mac/', bases: ['../', './', 'tmux.html', 'shortcuts.html'] },
  tmux: { path: '/cxt12e4/mac/tmux.html', bases: ['../', './', 'tmux.html', 'shortcuts.html'] },
  shortcuts: { path: '/cxt12e4/mac/shortcuts.html', bases: ['../', './', 'tmux.html', 'shortcuts.html'] }
};

function load(page, search = '', href) {
  const links = Object.keys(pages).map((name, i) => ({
    dataset: { manualPage: name, manualBase: pages[page].bases[i] },
    target: '', attributes: {}, href: '', events: {},
    addEventListener(name, callback) { this.events[name] = callback; },
    hasAttribute(name) { return Object.hasOwn(this.attributes, name); }
  }));
  const location = { href: href || 'http://localhost' + pages[page].path + search };
  Object.defineProperty(location, 'search', { get() { return new URL(this.href).search; } });
  const calls = [];
  const entries = [location.href];
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
    body: { dataset: { manualPage: page } },
    documentElement: { style: { setProperty(name, value) { styles[name] = value; } } },
    querySelectorAll(selector) { assert.equal(selector, 'a[data-manual-page]'); return links; }
  };
  const window = { location, history };
  new vm.Script(source).runInNewContext({ window, document, URL, URLSearchParams });
  const api = window.ManualNavigation;
  const read = (...args) => JSON.parse(JSON.stringify(api.read(...args)));
  const link = name => links.find(item => item.dataset.manualPage === name);
  return { api, read, links, link, location, history, calls, entries, styles };
}

const indexPath = pathname => pathname.endsWith('/') ? pathname + 'index.html' : pathname;

function assertLinks(manual, device, layer) {
  for (const [page, definition] of Object.entries(pages)) {
    const url = new URL(manual.link(page).href);
    assert.equal(url.pathname, indexPath(definition.path), 'Correct ' + page + ' destination');
    assert.equal(url.searchParams.get('device'), device);
    assert.equal(url.searchParams.get('layer'), String(layer));
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
  assert.equal(click(manual.link(page)), true, 'Current platform retains the current view');
  assert.equal(manual.location.href, original);
  for (const other of Object.keys(pages).filter(name => name !== page)) {
    assert.equal(click(manual.link(other)), false, 'Other platforms remain normal links');
    const destination = load(other, '', manual.link(other).href);
    assertLinks(destination, 'xbox', 6);
    assert.equal(new URL(destination.link(page).href).pathname, indexPath(pages[page].path));
  }
  for (const modifier of ['metaKey', 'ctrlKey', 'shiftKey', 'altKey']) {
    assert.equal(click(manual.link(page), { [modifier]: true }), false, 'Modified clicks stay native');
  }
  assert.equal(click(manual.link(page), { button: 1 }), false, 'Middle click stays native');
  manual.link(page).target = '_blank';
  assert.equal(click(manual.link(page)), false, 'New-tab links stay native');
  manual.link(page).target = '';
  manual.link(page).attributes.download = '';
  assert.equal(click(manual.link(page)), false, 'Download links stay native');

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

// A dedicated domain may redirect / to the shortcut guide. Windows navigation
// must therefore target /index.html, while old /cxt12e4/ deployments still work.
for (const prefix of ['/', '/cxt12e4/']) {
  for (const [page, definition] of Object.entries(pages)) {
    const currentPath = indexPath(definition.path.replace('/cxt12e4/', prefix));
    const manual = load(page, '', 'https://cxt12e4.11166666.xyz' + currentPath + '?device=xbox&layer=6');
    for (const [target, destination] of Object.entries(pages)) {
      const expectedPath = indexPath(destination.path.replace('/cxt12e4/', prefix));
      assert.equal(manual.link(target).href,
        'https://cxt12e4.11166666.xyz' + expectedPath + '?device=xbox&layer=6');
      const next = load(target, '', manual.link(target).href);
      assert.equal(new URL(next.link(page).href).pathname, currentPath, 'Return navigation retains the deployment base');
    }
  }
}

// Downloaded manuals must navigate to actual files, not local directory listings.
for (const page of Object.keys(pages)) {
  const base = 'file:///tmp/manual' + pages[page].path;
  const href = (base.endsWith('/') ? base + 'index.html' : base) + '?device=xbox&layer=6';
  const offline = load(page, '', href);
  for (const [target, definition] of Object.entries(pages)) {
    const expected = 'file:///tmp/manual' + definition.path + (definition.path.endsWith('/') ? 'index.html' : '');
    assert.equal(offline.link(target).href, expected + '?device=xbox&layer=6');
  }
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
assertLinks(load('tmux'), 'keyboard', 2);
assertLinks(load('windows'), 'keyboard', 8);
assertLinks(load('mac'), 'keyboard', 8);
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

console.log('PASS: shared manual navigation across Windows, Mac, tmux and shortcuts; root and subdirectory deployments, strict query parsing, preserved context, native link behavior, deduplicated browser history and readable text for all eight layer colors.');
