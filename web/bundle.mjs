import { mkdir, copyFile } from 'node:fs/promises';
const dest = new URL('../src/TerminalMgr.App/Terminal/vendor/', import.meta.url);
await mkdir(dest, { recursive: true });
for (const [src, name] of [
  ['@xterm/xterm/lib/xterm.js', 'xterm.js'],
  ['@xterm/xterm/css/xterm.css', 'xterm.css'],
  ['@xterm/xterm/LICENSE', 'xterm.LICENSE'],
  ['@xterm/addon-fit/lib/addon-fit.js', 'addon-fit.js'],
  ['@xterm/addon-fit/LICENSE', 'addon-fit.LICENSE'],
  ['@xterm/addon-web-links/lib/addon-web-links.js', 'addon-web-links.js'],
  ['@xterm/addon-web-links/LICENSE', 'addon-web-links.LICENSE']
]) await copyFile(new URL(`node_modules/${src}`, import.meta.url), new URL(name, dest));
