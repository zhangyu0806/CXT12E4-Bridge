// No browser, input or network side effects. Run: node tests/layer-profile-manual.test.js
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(path.join(__dirname, '../docs/cxt12e4-manual/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
new vm.Script(script);
const layerLiteral = script.match(/const layers = (\[[\s\S]*?\n    \]);/);
assert(layerLiteral, 'manual layer data is present');
const layers = JSON.parse(vm.runInNewContext('JSON.stringify(' + layerLiteral[1] + ')'));
assert.equal(layers.length, 8);
assert.equal(new Set(layers.map(layer => layer.n)).size, 8);
for (const layer of layers) {
  assert.equal(layer.keys.length, 12);
  assert.equal(layer.enc.length, 4);
}
const universal = layers.find(layer => layer.n === 8);
const blender = layers.find(layer => layer.n === 5);
const codex = layers.find(layer => layer.n === 1);
assert.equal(universal.name, '通用 / 浏览器');
assert.equal(blender.name, '3D建模 / Blender');
assert.equal(universal.keyLight, 8);
assert.equal(blender.keyLight, 5);
assert.equal(universal.lightColor, '#7088B8');
assert.equal(blender.lightColor, '#FFDC00');
assert.deepEqual(universal.keys.slice(0, 11).map(key => key[1]), [
  'Ctrl + Alt + C', 'Ctrl + Alt + V', 'Ctrl + Z', 'Ctrl + Y',
  'Ctrl + X', 'Ctrl + C', 'Ctrl + V', 'Ctrl + Shift + V',
  'Ctrl + T', 'Ctrl + Shift + T', 'Ctrl + F'
]);
assert.match(universal.keys[11][0], /CopyQ/);
assert.deepEqual(blender.keys.slice(0, 11).map(key => key[1]), [
  'Tab', 'E', 'G', 'R', 'S', 'I', 'Ctrl + B', 'Shift + D',
  'Ctrl + Z', 'Ctrl + Shift + Z', 'Shift + A'
]);
assert.match(blender.keys[11][1], /F3/);
assert.match(universal.enc[0][2], /Ctrl\+L/);
assert.match(universal.enc[1][2], /Ctrl\+W/);
assert.match(universal.enc[3][2], /F5/);
assert.match(blender.enc[0][1], /Numpad4/);
assert.match(blender.enc[0][3], /Numpad6/);
assert.match(blender.enc[1][1], /Numpad8/);
assert.match(blender.enc[1][3], /Numpad2/);
assert.match(blender.enc[3][2], /NumpadDecimal/);
assert.match(codex.enc[0][1], /Sol最高/);
assert.match(codex.enc[0][2], /Astra Ultra \+ Fast/);
assert.match(codex.enc[0][3], /Astra轻度/);
assert.match(codex.notes.join(' '), /GPT-6 Sol.*GPT-6 Astra/);
assert.match(codex.keys[10][1], /Codex 内置浏览器直接打开 ChatGPT 首页/);
assert.match(codex.keys[10][1], /长按切 tmux session 03/);
// Legacy layer=0 and invalid URLs are exercised by manual-navigation.test.js
// and the controller DOM harness through the actual shared navigation module.
const commander = layers.find(layer => layer.n === 6);
assert.match(commander.keys[3][1], /2400/);
assert.match(commander.keys[9][1], /2022/);
assert.match(commander.keys[10][1], /2020/);
const messaging = layers.find(layer => layer.n === 7);
assert.match(messaging.keys[6][1], /不发送/);
assert.doesNotMatch(messaging.keys[6][1], /Ctrl\s*\+\s*R/);
const engine = fs.readFileSync(path.join(__dirname, '../src/Cxt1024Bridge/ActionEngine.cs'), 'utf8');
const messagingBody = engine.slice(engine.indexOf('private void MessagingButton'), engine.indexOf('private static void ObsidianButton'));
assert.doesNotMatch(messagingBody, /InputSender\.Chord\(Vk\.R,/);
const readme = fs.readFileSync(path.join(__dirname, '../README-CXT12E4.md'), 'utf8');
assert.match(readme, /newProjectlessTask/);
const automation = fs.readFileSync(path.join(__dirname, '../src/Cxt1024Bridge/CodexNativeActions.cs'), 'utf8');
const browserAction = automation.slice(automation.indexOf('internal bool OpenInBuiltInBrowser(string url, Func<bool>'));
assert.match(browserAction, /DispatchBuiltInBrowserRequest/);
assert.match(browserAction, /UseShellExecute = false/);
assert.doesNotMatch(browserAction, /codex:\/\/browser/);
assert.doesNotMatch(browserAction.replace(/^\s*\/\/.*$/gm, ''), /InputSender\.|\.SetFocus\(|ValuePattern/);
assert.match(automation, /InputSender\.Chord\(Vk\.O, Vk\.LeftControl, Vk\.LeftAlt\)/);
assert.match(engine, /case 12: _chatGpt\.NewTask\(contextIsCurrent\)/);
assert.doesNotMatch(automation, /FindTopLevelNewTaskButton|InputSender\.Chord\(Vk\.N,/);
assert.match(readme, /分叉任务；长按 tmux 06/);
assert.match(readme, /快速聊天开 \/ 关；长按 tmux 05/);
assert.doesNotMatch(readme, /\| 9 \| 停止生成/);
console.log('PASS: manual syntax, all layer shapes, L8/L5 bindings, indicators, legacy layer=0 alias.');
