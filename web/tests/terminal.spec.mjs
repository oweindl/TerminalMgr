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

test('Remote output remains terminal text and cannot create page HTML', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'<img src=x onerror=alert(1)>'}));
  await expect(page.locator('.xterm-rows')).toContainText('<img');
  await expect(page.locator('img')).toHaveCount(0);
});
