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

test('Ctrl+B reaches the SSH bridge unchanged; Alt+2 switches sessions', async ({page}) => {
  await page.keyboard.press('Control+b');
  await expect.poll(() => page.evaluate(() => window.__messages.some(m => m.type === 'input' && m.data === '\x02'))).toBe(true);
  await page.keyboard.press('Alt+2');
  await expect.poll(() => page.evaluate(() => window.__messages.some(m => m.type === 'switch' && m.index === 1))).toBe(true);
});

test('Larger window sends more rows and columns; hidden session catches up on selection', async ({page}) => {
  await expect.poll(() => page.evaluate(() => window.__messages.filter(m => m.type === 'resize').length)).toBeGreaterThan(0);
  const initial = await page.evaluate(() => window.__messages.filter(m => m.type === 'resize').at(-1));
  await page.evaluate(() => window.__hostMessage({type:'create',id:'two'}));
  await page.setViewportSize({width:1400,height:900});
  await expect.poll(() => page.evaluate(() => window.__messages.filter(m => m.type === 'resize' && m.id === 'two').at(-1)?.rows)).toBeGreaterThan(initial.rows);
  await page.evaluate(() => window.__hostMessage({type:'select',id:'one'}));
  await expect.poll(() => page.evaluate(() => window.__messages.filter(m => m.type === 'resize' && m.id === 'one').at(-1)?.cols)).toBeGreaterThan(initial.cols);
});

test('Clipboard paste respects bracketed paste mode', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'\x1b[?2004hREADY'}));
  await expect(page.locator('.xterm-rows')).toContainText('READY');
  await page.evaluate(() => window.__hostMessage({type:'paste',id:'one',text:'echo first\necho second'}));
  await expect.poll(() => page.evaluate(() => window.__messages.filter(m => m.type === 'input').map(m => m.data).join('')))
    .toBe('\x1b[200~echo first\recho second\x1b[201~');
});

const messages = (page, type) => page.evaluate(t => window.__messages.filter(m => m.type === t), type);

test('Right click copies the selection without padding spaces and clears it; the next one pastes', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'alpha     \r\nbeta   '}));
  await expect(page.locator('.xterm-rows')).toContainText('alpha');
  await page.locator('.xterm-rows > div').first().click({clickCount:3});
  await page.locator('.xterm-rows > div').first().click({button:'right'});
  await expect.poll(async () => (await messages(page, 'copy')).length).toBe(1);
  expect((await messages(page, 'copy'))[0]).toMatchObject({id:'one', text:'alpha'});
  expect(await messages(page, 'contextPaste')).toHaveLength(0);
  await page.locator('.xterm-rows > div').first().click({button:'right'});
  await expect.poll(async () => (await messages(page, 'contextPaste')).length).toBe(1);
  expect(await messages(page, 'copy')).toHaveLength(1);
  expect((await messages(page, 'contextPaste'))[0]).toMatchObject({id:'one'});
  expect(await messages(page, 'input')).toHaveLength(0);
});

test('Right click is never forwarded to programs with mouse tracking; Shift+drag still selects', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'\x1b[?1000h\x1b[?1006hselect me please\r\n'}));
  await expect(page.locator('.xterm-rows')).toContainText('select me please');
  const row = page.locator('.xterm-rows > div').first();
  const box = await row.boundingBox();
  const y = box.y + box.height / 2;
  await page.mouse.click(box.x + 20, y);
  await expect.poll(async () => (await messages(page, 'input')).length).toBeGreaterThan(0); // tracking is really on
  const inputsBefore = (await messages(page, 'input')).length;
  await page.mouse.click(box.x + 20, y, {button:'right'});
  await expect.poll(async () => (await messages(page, 'contextPaste')).length).toBe(1);
  expect(await messages(page, 'input')).toHaveLength(inputsBefore);
  await page.keyboard.down('Shift');
  await page.mouse.move(box.x + 2, y);
  await page.mouse.down();
  await page.mouse.move(box.x + 90, y, {steps:4});
  await page.mouse.up();
  await page.keyboard.up('Shift');
  const inputsAfterDrag = (await messages(page, 'input')).length;
  await page.mouse.click(box.x + 40, y, {button:'right'});
  await expect.poll(async () => (await messages(page, 'copy')).length).toBe(1);
  expect((await messages(page, 'copy'))[0].text.length).toBeGreaterThan(3);
  expect(await messages(page, 'input')).toHaveLength(inputsAfterDrag);
});

test('Ctrl+Shift+C copies trimmed text and clears the selection', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'copy-me   \r\n'}));
  await expect(page.locator('.xterm-rows')).toContainText('copy-me');
  await page.locator('.xterm-rows > div').first().click({clickCount:3});
  await page.keyboard.press('Control+Shift+C');
  await expect.poll(async () => (await messages(page, 'copy')).length).toBe(1);
  expect((await messages(page, 'copy'))[0].text).toBe('copy-me');
  await page.keyboard.press('Control+Shift+C');
  await page.waitForTimeout(150);
  expect(await messages(page, 'copy')).toHaveLength(1); // selection is gone
});

const inputs = page => page.evaluate(() => window.__messages.filter(m => m.type === 'input').map(m => m.data));

test('Ctrl+Y is an app shortcut: reported to the host, never sent to the session', async ({page}) => {
  await page.keyboard.press('Control+y');
  await expect.poll(async () => (await messages(page, 'shortcut')).length).toBe(1);
  expect((await messages(page, 'shortcut'))[0]).toMatchObject({action:'dictation', id:'one'});
  expect(await inputs(page)).toEqual([]);
  await page.keyboard.press('a');
  await expect.poll(() => inputs(page)).toEqual(['a']); // normal typing still works
});

test('Shortcuts are configurable: the old key goes back to the session', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'shortcuts', shortcuts:{dictation:'Ctrl+Shift+D'}}));
  await page.keyboard.press('Control+y');
  await expect.poll(() => inputs(page)).toEqual(['\x19']); // readline "yank" works again
  await page.keyboard.press('Control+Shift+D');
  await expect.poll(async () => (await messages(page, 'shortcut')).length).toBe(1);
  expect(await inputs(page)).toEqual(['\x19']);
});

test('Held shortcut key reports once; Ctrl+Alt+Y is not the Ctrl+Y shortcut', async ({page}) => {
  await page.keyboard.down('Control');
  for (let i = 0; i < 4; i++) await page.keyboard.down('y');
  await page.keyboard.up('y');
  await page.keyboard.up('Control');
  await expect.poll(async () => (await messages(page, 'shortcut')).length).toBe(1);
  await page.keyboard.press('Control+Alt+y');
  await page.waitForTimeout(100);
  expect(await messages(page, 'shortcut')).toHaveLength(1);
});

test('Paste and run sends the text as one bracketed paste, then a single Enter', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'\x1b[?2004hREADY'}));
  await expect(page.locator('.xterm-rows')).toContainText('READY');
  await page.evaluate(() => window.__hostMessage({type:'paste',id:'one',text:'zeile eins\nzeile zwei',enter:true}));
  await expect.poll(async () => (await inputs(page)).join('')).toBe('\x1b[200~zeile eins\rzeile zwei\x1b[201~\r');
  const all = await inputs(page);
  expect(all.at(-1)).toBe('\r');
  expect(all.filter(d => d === '\r')).toHaveLength(1); // the line break inside the paste is not a separate Enter
});

test('Paste without run does not press Enter', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'paste',id:'one',text:'nur text'}));
  await expect.poll(async () => (await inputs(page)).join('')).toBe('nur text');
  await page.waitForTimeout(100);
  expect((await inputs(page)).join('')).toBe('nur text');
});

test('Remote output remains terminal text and cannot create page HTML', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'<img src=x onerror=alert(1)>'}));
  await expect(page.locator('.xterm-rows')).toContainText('<img');
  await expect(page.locator('img')).toHaveCount(0);
});
