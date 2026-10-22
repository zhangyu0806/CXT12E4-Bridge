/* Shared navigation for the Windows, Mac and tmux manuals. */
(function () {
  'use strict';

  const page = document.body.dataset.manualPage;
  const links = [...document.querySelectorAll('a[data-manual-page]')];

  function validLayer(value, fallback) {
    if (typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= 8) {
      return value === 0 ? 8 : value;
    }
    if (typeof value === 'string' && /^[0-8]$/.test(value)) {
      return value === '0' ? 8 : Number(value);
    }
    return fallback;
  }

  function read(search, fallbackLayer = 8) {
    const parameters = new URLSearchParams(search);
    return {
      device: parameters.get('device') === 'xbox' ? 'xbox' : 'keyboard',
      layer: validLayer(parameters.get('layer'), validLayer(fallbackLayer, 8))
    };
  }

  function normalized(device, layer) {
    return { device: device === 'xbox' ? 'xbox' : 'keyboard', layer: validLayer(layer, 8) };
  }

  function theme(hex) {
    if (!/^#[0-9a-f]{6}$/i.test(hex)) return null;
    const color = value => [1, 3, 5].map(index => parseInt(value.slice(index, index + 2), 16));
    const luminance = rgb => rgb.reduce((total, channel, index) => {
      const value = channel / 255;
      const linear = value <= 0.04045 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
      return total + linear * [0.2126, 0.7152, 0.0722][index];
    }, 0);
    const contrast = (a, b) => {
      const left = luminance(a), right = luminance(b);
      return (Math.max(left, right) + 0.05) / (Math.min(left, right) + 0.05);
    };
    const blend = (from, to, amount) => from.map((channel, index) => Math.round(channel + (to[index] - channel) * amount));
    const hexColor = rgb => '#' + rgb.map(channel => channel.toString(16).padStart(2, '0')).join('');
    const accent = color(hex), dark = color('#172033'), white = [255, 255, 255];
    let ink = contrast(accent, dark) > contrast(accent, white) ? dark : white;
    // A few middle-luminance colors need a slightly darker ink than the body text.
    if (contrast(accent, ink) < 4.5) {
      for (let step = 1; step <= 100; step++) {
        ink = blend(dark, [0, 0, 0], step / 100);
        if (contrast(accent, ink) >= 4.5) break;
      }
    }
    let text = accent;
    if (contrast(text, white) < 4.5) {
      for (let step = 1; step <= 100; step++) {
        text = blend(accent, dark, step / 100);
        if (contrast(text, white) >= 4.5) break;
      }
    }
    const result = { ink: hexColor(ink), text: hexColor(text) };
    document.documentElement.style.setProperty('--accent-ink', result.ink);
    document.documentElement.style.setProperty('--accent-text', result.text);
    return result;
  }

  function update(device, layer) {
    const state = normalized(device, layer);
    for (const link of links) {
      const destination = new URL(link.dataset.manualBase, window.location.href);
      // Explicit index files work both offline and when the site root redirects to a featured guide.
      if (destination.pathname.endsWith('/')) destination.pathname += 'index.html';
      destination.searchParams.set('device', state.device);
      destination.searchParams.set('layer', state.layer);
      link.href = destination.href;
    }
    return state;
  }

  function write(device, layer, mode = 'push') {
    const state = normalized(device, layer);
    if (mode !== 'none') {
      const destination = new URL(window.location.href);
      destination.searchParams.set('device', state.device);
      destination.searchParams.set('layer', state.layer);
      if (destination.href !== window.location.href) {
        const method = mode === 'replace' ? 'replaceState' : 'pushState';
        window.history[method](window.history.state, '', destination.href);
      }
    }
    return update(state.device, state.layer);
  }

  for (const link of links) {
    link.addEventListener('click', event => {
      if (link.dataset.manualPage !== page || event.defaultPrevented ||
          (event.button !== undefined && event.button !== 0) ||
          event.metaKey || event.ctrlKey || event.shiftKey || event.altKey ||
          (link.target && link.target !== '_self') || link.hasAttribute('download')) return;
      // An already selected platform is a stable location, not a reset button.
      event.preventDefault();
    });
  }

  window.ManualNavigation = { read, update, write, theme };
  const initial = read(window.location.search, page === 'tmux' ? 2 : 8);
  update(initial.device, initial.layer);
}());
