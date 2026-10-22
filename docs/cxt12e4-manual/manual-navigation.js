/* Platform first, then device or shortcuts. Works online and from local files. */
(function () {
  'use strict';

  const page = document.body.dataset.manualPage;
  const platform = page === 'windows' || page === 'windows-shortcuts' ? 'windows' : 'mac';
  const isDevicePage = page === 'windows' || page === 'mac';
  const root = new URL(document.body.dataset.manualRoot, window.location.href);
  const platformLinks = [...document.querySelectorAll('a[data-manual-platform]')];
  const sectionLinks = [...document.querySelectorAll('a[data-manual-section]')];
  let currentState;
  let deviceChange;

  function destination(targetPlatform, section, state) {
    const directory = targetPlatform === 'mac' ? 'mac/' : '';
    const url = new URL(directory + (section === 'shortcuts' ? 'shortcuts.html' : 'index.html'), root);
    url.searchParams.set('device', section === 'shortcuts' ? state.device : section);
    url.searchParams.set('layer', state.layer);
    return url;
  }

  function selected(link, active) {
    if (active) link.setAttribute('aria-current', 'true');
    else link.removeAttribute('aria-current');
  }

  function ordinaryClick(event, link) {
    return !event.defaultPrevented && (event.button === undefined || event.button === 0) &&
      !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey &&
      (!link.target || link.target === '_self') && !link.hasAttribute('download');
  }

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
    currentState = state;
    const section = isDevicePage ? state.device : 'shortcuts';
    document.body.dataset.manualDevice = state.device;
    for (const link of platformLinks) {
      const target = link.dataset.manualPlatform;
      link.href = target === platform ? window.location.href : destination(target, section, state).href;
      selected(link, target === platform);
    }
    for (const link of sectionLinks) {
      link.href = destination(platform, link.dataset.manualSection, state).href;
      selected(link, link.dataset.manualSection === section);
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

  for (const link of platformLinks) {
    link.addEventListener('click', event => {
      if (link.dataset.manualPlatform === platform && ordinaryClick(event, link)) event.preventDefault();
    });
  }
  for (const link of sectionLinks) {
    link.addEventListener('click', event => {
      if (!ordinaryClick(event, link)) return;
      const section = link.dataset.manualSection;
      if (isDevicePage && section !== 'shortcuts' && deviceChange) {
        event.preventDefault();
        if (section !== currentState.device) deviceChange(section, currentState.layer);
      } else if (!isDevicePage && page !== 'tmux' && section === 'shortcuts') {
        event.preventDefault();
      }
    });
  }

  window.ManualNavigation = { read, update, write, theme, onDeviceChange(callback) { deviceChange = callback; } };
  const initial = read(window.location.search, page === 'tmux' ? 2 : 8);
  update(initial.device, initial.layer);
}());
