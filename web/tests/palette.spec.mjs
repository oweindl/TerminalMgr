import { test, expect } from '@playwright/test';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

test.beforeEach(async ({page}) => {
  await page.addInitScript(() => {
    window.__messages = [];
    window.chrome ??= {};
    window.chrome.webview = {
      postMessage: message => window.__messages.push(message),
      addEventListener: (_, listener) => window.__hostMessage = message => listener({data:message})
    };
  });
  await page.goto(pathToFileURL(path.resolve('../src/TerminalMgr.App/Terminal/index.html')).href);
  await expect.poll(() => page.evaluate(() => window.__messages.some(m => m.type === 'ready'))).toBe(true);
  await page.evaluate(() => window.__hostMessage({type:'create',id:'one'}));
  await expect(page.locator('.session.active')).toHaveCount(1);
  await page.locator('.session.active .xterm-helper-textarea').focus();
});

// Items as the host builds them (PaletteBuilder in Core).
const items = [
  { id:'favorite:f1', group:'Favoriten', title:'Archyx', subtitle:'devbox › main › archyx', shortcut:'Ctrl+Alt+A', showWhenEmpty:true, searchable:true },
  { id:'window:p1|$0|@1', group:'Zuletzt benutzt', title:'devbox › main › shell', subtitle:'', shortcut:'', showWhenEmpty:true, searchable:false },
  { id:'window:p1|$0|@1', group:'tmux-Fenster', title:'devbox › main › 0: shell', subtitle:'aktives Fenster', shortcut:'Ctrl+0', showWhenEmpty:false, searchable:true },
  { id:'window:p1|$0|@4', group:'tmux-Fenster', title:'devbox › main › 2: archyx', subtitle:'', shortcut:'Ctrl+2', showWhenEmpty:false, searchable:true },
  { id:'window:p1|$1|@9', group:'tmux-Fenster', title:'devbox › Überwachung › 0: Protokolle', subtitle:'', shortcut:'', showWhenEmpty:false, searchable:true },
  { id:'clip:0', group:'Clipboard', title:'git status --short', subtitle:'Enter fügt ein', shortcut:'', showWhenEmpty:false, searchable:true },
  { id:'action:dictation', group:'Aktionen', title:'Diktat starten', subtitle:'', shortcut:'Ctrl+Y', showWhenEmpty:false, searchable:true },
  { id:'action:settings', group:'Aktionen', title:'Einstellungen öffnen', subtitle:'', shortcut:'', showWhenEmpty:false, searchable:true }
];
const table = { dictation:'Ctrl+Y', copy:'Ctrl+Shift+C', paste:'Ctrl+Shift+V', palette:'Ctrl+Shift+K' };
const sent = (page, type) => page.evaluate(t => window.__messages.filter(m => m.type === t), type);
const rows = page => page.locator('#palette .palette-item .palette-title').allTextContents();
const selectedTitle = page => page.locator('#palette [aria-selected="true"] .palette-title').textContent();

async function openPalette(page) {
  await page.evaluate(t => window.__hostMessage({type:'shortcuts', shortcuts:t, sessionModifier:'Alt'}), table);
  await page.evaluate(i => window.__hostMessage({type:'paletteItems', items:i}), items);
  await page.keyboard.press('Control+Shift+K');
  await expect(page.locator('#palette')).toBeVisible();
}

test('Opens at once on Ctrl+Shift+K, asks the host for a fresh list, shows favorites and recent windows for an empty input', async ({page}) => {
  await openPalette(page);
  expect((await sent(page, 'paletteOpened')).length).toBe(1);
  await expect(page.locator('#palette-input')).toBeFocused();
  expect(await rows(page)).toEqual(['Archyx', 'devbox › main › shell']);
  expect(await page.locator('#palette .palette-group').allTextContents()).toEqual(['Favoriten', 'Zuletzt benutzt']);
  expect(await page.locator('#palette .palette-item kbd').first().textContent()).toBe('Ctrl+Alt+A');
  expect(await sent(page, 'input')).toHaveLength(0);
});

test('Search "arch" finds window and favorite, Enter runs the best match, nothing reaches the terminal', async ({page}) => {
  await openPalette(page);
  await page.keyboard.type('arch');
  const titles = await rows(page);
  expect(titles).toContain('devbox › main › 2: archyx');
  expect(titles).toContain('Archyx');
  expect(titles.every(t => /arch/i.test(t))).toBe(true);
  await page.keyboard.press('Enter');
  const run = await sent(page, 'paletteRun');
  expect(run).toHaveLength(1);
  expect(run[0].query).toBe('arch');
  expect(['favorite:f1', 'window:p1|$0|@4']).toContain(run[0].id);
  await expect(page.locator('#palette')).toBeHidden();
  expect(await sent(page, 'input')).toHaveLength(0);
});

test('Search ignores case and umlauts, needs every word, and says so when nothing matches', async ({page}) => {
  await openPalette(page);
  await page.keyboard.type('UBERWACH');
  expect(await rows(page)).toEqual(['devbox › Überwachung › 0: Protokolle']);
  await page.locator('#palette-input').fill('devbox protokolle');
  expect(await rows(page)).toEqual(['devbox › Überwachung › 0: Protokolle']);
  await page.locator('#palette-input').fill('devbox xyz');
  expect(await rows(page)).toEqual([]);
  await expect(page.locator('.palette-empty')).toHaveText('Nichts gefunden.');
});

test('Search ranks word starts first; recent windows are not found twice; letters in order are enough', async ({page}) => {
  await openPalette(page);
  await page.locator('#palette-input').fill('shell');
  expect(await rows(page)).toEqual(['devbox › main › 0: shell']);
  await page.locator('#palette-input').fill('einst');
  expect((await rows(page))[0]).toBe('Einstellungen öffnen');
  await page.locator('#palette-input').fill('dk');
  expect(await rows(page)).toContain('Diktat starten');
});

test('Arrows move and wrap, Ctrl+D asks for a favorite, Esc closes and tells the host, Ctrl+Shift+K toggles', async ({page}) => {
  await openPalette(page);
  await page.keyboard.type('main');
  const first = await selectedTitle(page);
  await page.keyboard.press('ArrowDown');
  expect(await selectedTitle(page)).not.toBe(first);
  await page.keyboard.press('ArrowUp');
  expect(await selectedTitle(page)).toBe(first);
  await page.keyboard.press('ArrowUp'); // wraps to the last entry
  expect(await selectedTitle(page)).not.toBe(first);
  await page.locator('#palette-input').fill('archyx');
  await page.keyboard.press('Control+d');
  const favorite = await sent(page, 'paletteFavorite');
  expect(favorite).toHaveLength(1);
  expect(favorite[0].id).toMatch(/^(window|favorite):/);
  await page.keyboard.press('Escape');
  await expect(page.locator('#palette')).toBeHidden();
  expect((await sent(page, 'paletteClosed')).length).toBe(1);
  await page.keyboard.press('Control+Shift+K');
  await expect(page.locator('#palette')).toBeVisible();
  await page.keyboard.press('Escape');
  expect(await sent(page, 'input')).toHaveLength(0);
});

test('Ctrl+D is only offered for windows and favorites; a click runs an entry; a fresh list keeps query and selection', async ({page}) => {
  await openPalette(page);
  await page.locator('#palette-input').fill('diktat');
  await page.keyboard.press('Control+d');
  expect(await sent(page, 'paletteFavorite')).toHaveLength(0);
  await page.locator('#palette-input').fill('archyx');
  await page.keyboard.press('ArrowDown');
  const selectedBefore = await selectedTitle(page);
  const more = [...items, { id:'window:p2|$0|@7', group:'tmux-Fenster', title:'server1 › main › 1: archiv', subtitle:'', shortcut:'', showWhenEmpty:false, searchable:true }];
  await page.evaluate(i => window.__hostMessage({type:'paletteItems', items:i}), more);
  expect(await page.locator('#palette-input').inputValue()).toBe('archyx');
  expect(await selectedTitle(page)).toBe(selectedBefore);
  await page.locator('#palette-input').fill('');
  await page.locator('#palette .palette-item', { hasText: 'Archyx' }).click();
  expect((await sent(page, 'paletteRun'))[0].id).toBe('favorite:f1');
});

test('Keys typed into the palette never reach the shell; Ctrl+K still does outside of it', async ({page}) => {
  await openPalette(page);
  await page.keyboard.type('abc');
  await page.keyboard.press('Control+k');
  await page.keyboard.press('Escape');
  expect(await sent(page, 'input')).toHaveLength(0);
  await page.locator('.session.active .xterm-helper-textarea').focus();
  await page.keyboard.press('Control+k');
  await expect.poll(async () => (await sent(page, 'input')).map(m => m.data)).toEqual(['\x0b']);
});

test('The host can open and close it (from a WPF control); rank() and fold() behave', async ({page}) => {
  await page.evaluate(i => window.__hostMessage({type:'palette', cmd:'open', items:i}), items);
  await expect(page.locator('#palette')).toBeVisible();
  await page.evaluate(() => window.__hostMessage({type:'palette', cmd:'close'}));
  await expect(page.locator('#palette')).toBeHidden();
  const ranked = await page.evaluate(i => window.TerminalPalette.rank(i, 'archyx').map(x => x.title), items);
  expect(ranked.slice(0, 2).sort()).toEqual(['Archyx', 'devbox › main › 2: archyx'].sort());
  expect(await page.evaluate(() => window.TerminalPalette.fold('ÄÖÜ Élan'))).toBe('aou elan');
});
