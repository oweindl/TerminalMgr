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

test('Copy and paste shortcuts are configurable; the old combination no longer copies', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'copy-me   \r\n'}));
  await expect(page.locator('.xterm-rows')).toContainText('copy-me');
  await page.evaluate(() => window.__hostMessage({type:'shortcuts', shortcuts:{dictation:'Ctrl+Y', copy:'Ctrl+Alt+C', paste:'Ctrl+Alt+V'}}));
  await page.locator('.xterm-rows > div').first().click({clickCount:3});
  await page.keyboard.press('Control+Shift+C');
  await page.waitForTimeout(150);
  expect(await messages(page, 'copy')).toHaveLength(0);
  await page.keyboard.press('Control+Alt+c');
  await expect.poll(async () => (await messages(page, 'copy')).length).toBe(1);
  expect((await messages(page, 'copy'))[0].text).toBe('copy-me');
  await page.keyboard.press('Control+Shift+V');
  await page.keyboard.press('Control+Alt+v');
  await expect.poll(async () => (await messages(page, 'pasteRequest')).length).toBe(1);
});

test('Default copy and paste shortcuts work before the host sends a configuration', async ({page}) => {
  await page.keyboard.press('Control+Shift+V');
  await expect.poll(async () => (await messages(page, 'pasteRequest')).length).toBe(1);
});

test('The session switch modifier is configurable: Alt+2 by default, Ctrl+Alt+2 after the change', async ({page}) => {
  await page.keyboard.press('Alt+2');
  await expect.poll(async () => (await messages(page, 'switch')).length).toBe(1);
  await page.evaluate(() => window.__hostMessage({type:'shortcuts', shortcuts:{dictation:'Ctrl+Y'}, sessionModifier:'Ctrl+Alt'}));
  await page.keyboard.press('Alt+3');
  await page.waitForTimeout(150);
  expect(await messages(page, 'switch')).toHaveLength(1); // Alt alone no longer switches
  await page.keyboard.press('Control+Alt+3');
  await expect.poll(async () => (await messages(page, 'switch')).length).toBe(2);
  expect((await messages(page, 'switch')).at(-1).index).toBe(2);
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

const b64 = text => Buffer.from(text, 'utf8').toString('base64');
const output = (page, data) => page.evaluate(d => window.__hostMessage({type:'output',id:'one',data:d}), data);

test('OSC 52 from tmux (Claude Code copy) reaches the host while mouse tracking is on; BEL and ST both work', async ({page}) => {
  await output(page, '\x1b[?1000h\x1b[?1006h\x1b[?25l● Claude Code\r\n> markierter Satz\r\n');
  await output(page, `\x1b]52;c;${b64('markierter Satz')}\x07`);
  await output(page, `\x1b]52;;${b64('zweiter Text mit Umlauten äöü')}\x1b\\`);
  await expect.poll(async () => (await messages(page, 'osc52')).length).toBe(2);
  const sent = await messages(page, 'osc52');
  expect(sent[0]).toMatchObject({id:'one', data:`c;${b64('markierter Satz')}`});
  expect(sent[1].data).toBe(`;${b64('zweiter Text mit Umlauten äöü')}`);
  await expect(page.locator('.xterm-rows')).toContainText('markierter Satz'); // the sequence itself stays invisible
  await expect(page.locator('.xterm-rows')).not.toContainText(']52');
});

test('OSC 52 read requests are never answered and the sequence can be switched off', async ({page}) => {
  await output(page, '\x1b]52;c;?\x07');
  await page.waitForTimeout(200);
  expect(await inputs(page)).toEqual([]); // no reply that would reveal the clipboard
  await page.evaluate(() => window.__hostMessage({type:'terminalSettings', osc52:false, debug:false}));
  await output(page, `\x1b]52;c;${b64('ignoriert')}\x07`);
  await page.waitForTimeout(200);
  const writes = (await messages(page, 'osc52')).filter(m => m.data.endsWith(b64('ignoriert')));
  expect(writes).toHaveLength(0);
  await page.evaluate(() => window.__hostMessage({type:'terminalSettings', osc52:true, debug:false}));
  await output(page, `\x1b]52;c;${b64('wieder an')}\x07`);
  await expect.poll(async () => (await messages(page, 'osc52')).some(m => m.data.endsWith(b64('wieder an')))).toBe(true);
});

test('Right click copies the selection even if output arrives between pressing and releasing the button', async ({page}) => {
  // Realistic: tmux keeps redrawing its status line and Claude Code keeps streaming while the user selects with Shift+drag.
  await output(page, '\x1b[?1000h\x1b[?1006h');
  const lines = Array.from({length: 12}, (_, i) => `Ausgabezeile ${i + 1} mit etwas Text`).join('\r\n');
  await output(page, lines + '\r\n');
  await expect(page.locator('.xterm-rows')).toContainText('Ausgabezeile 12');
  const row = page.locator('.xterm-rows > div').nth(2);
  const box = await row.boundingBox();
  const y = box.y + box.height / 2;
  await page.keyboard.down('Shift');
  await page.mouse.move(box.x + 2, y);
  await page.mouse.down();
  await output(page, '\x1b[s\x1b[1;1H\x1b[7m[main] 0:bash*\x1b[0m\x1b[u');       // status line redraw while dragging
  await page.mouse.move(box.x + 150, y, {steps:5});
  await page.mouse.up();
  await page.keyboard.up('Shift');
  await page.mouse.move(box.x + 60, y);
  await page.mouse.down({button:'right'});
  await output(page, '\x1b[s\x1b[1;1H\x1b[7m[main] 0:bash*\x1b[0m\x1b[u\x1b[?25l');   // more output before the button is released
  await page.mouse.up({button:'right'});
  await expect.poll(async () => (await messages(page, 'copy')).length).toBe(1);
  expect((await messages(page, 'copy'))[0].text).toMatch(/Ausgabezeile/);
  expect(await messages(page, 'contextPaste')).toHaveLength(0);
  expect(await inputs(page)).not.toContain('\x1b[<2;'); // the right button never reaches the program
});

test('Debug mode reports selection and mouse tracking mode of a right click', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'terminalSettings', osc52:true, debug:true}));
  await output(page, '\x1b[?1002h\x1b[?1006hzeile\r\n');
  await page.locator('.xterm-rows > div').first().click({button:'right'});
  await expect.poll(async () => (await messages(page, 'diag')).length).toBe(1);
  expect((await messages(page, 'diag'))[0].text).toMatch(/Markierung 0 Zeichen, Maus-Tracking drag/);
});

test('Remote output remains terminal text and cannot create page HTML', async ({page}) => {
  await page.evaluate(() => window.__hostMessage({type:'output',id:'one',data:'<img src=x onerror=alert(1)>'}));
  await expect(page.locator('.xterm-rows')).toContainText('<img');
  await expect(page.locator('img')).toHaveCount(0);
});

// The Web Speech dictation engine: the page runs the recognizer, the host only gets messages. A scripted recognizer stands in
// for the browser's (WebView2 has no speech service, see docs/dictation-engines.md).
async function installFakeRecognizer(page) {
  await page.evaluate(() => {
    window.__recognizers = [];
    window.SpeechRecognition = window.webkitSpeechRecognition = class {
      constructor() { window.__recognizers.push(this); this.calls = []; }
      start() { this.calls.push('start'); queueMicrotask(() => this.onstart?.()); }
      stop() { this.calls.push('stop'); queueMicrotask(() => this.onend?.()); }
      abort() { this.calls.push('abort'); queueMicrotask(() => this.onend?.()); }
      say(index, text, isFinal) { this.onresult?.({ resultIndex: index, results: Object.assign([Object.assign([{ transcript: text }], { isFinal })], { length: index + 1 }) }); }
    };
  });
}
const speech = page => page.evaluate(() => window.__messages.filter(m => m.type === 'speech'));

test('Web Speech engine: start, provisional and final results, stop and end go back to the host', async ({page}) => {
  await installFakeRecognizer(page);
  await page.evaluate(() => window.__hostMessage({type:'speech', cmd:'start', lang:'de-DE'}));
  await expect.poll(async () => (await speech(page)).map(m => m.event)).toEqual(['started']);
  const settings = await page.evaluate(() => { const r = window.__recognizers[0]; return { lang: r.lang, continuous: r.continuous, interim: r.interimResults }; });
  expect(settings).toEqual({ lang:'de-DE', continuous:true, interim:true });
  await page.evaluate(() => window.__recognizers[0].say(0, 'Starte den', false));
  await page.evaluate(() => window.__recognizers[0].say(0, 'Starte den Server', true));
  await page.evaluate(() => window.__hostMessage({type:'speech', cmd:'stop'}));
  await expect.poll(async () => (await speech(page)).map(m => m.event)).toEqual(['started', 'interim', 'final', 'end']);
  const messagesSeen = await speech(page);
  expect(messagesSeen[1]).toMatchObject({ index:0, text:'Starte den' });
  expect(messagesSeen[2]).toMatchObject({ index:0, text:'Starte den Server' });
  expect(await page.evaluate(() => window.__recognizers[0].calls)).toEqual(['start', 'stop']);
});

test('Web Speech engine: errors are reported, abort ends quietly, a missing API is reported as unsupported', async ({page}) => {
  await installFakeRecognizer(page);
  await page.evaluate(() => window.__hostMessage({type:'speech', cmd:'start', lang:'de-DE'}));
  await page.evaluate(() => window.__recognizers[0].onerror?.({ error:'network' }));
  expect((await speech(page)).some(m => m.event === 'error' && m.error === 'network')).toBe(true);
  await page.evaluate(() => window.__hostMessage({type:'speech', cmd:'abort'}));
  await expect.poll(async () => await page.evaluate(() => window.__recognizers[0].calls)).toEqual(['start', 'abort']);
  await page.evaluate(() => { delete window.webkitSpeechRecognition; delete window.SpeechRecognition; window.__messages.length = 0; });
  await page.evaluate(() => window.__hostMessage({type:'speech', cmd:'start'}));
  await expect.poll(async () => (await speech(page)).map(m => m.event + ':' + (m.error ?? ''))).toEqual(['error:unsupported', 'end:']);
});

// Keyboard navigation: the host sends the navigation shortcuts with the others; the page takes them before the session sees a key.
const navigationTable = {
  dictation:'Ctrl+Y', copy:'Ctrl+Shift+C', paste:'Ctrl+Shift+V',
  window0:'Ctrl+0', window1:'Ctrl+1', window2:'Ctrl+2', window3:'Ctrl+3', windowNext:'Ctrl+Tab', windowPrevious:'Ctrl+Shift+Tab', windowBack:'Ctrl+<',
  'favorite:f1':'Ctrl+Alt+A'
};
const shortcutActions = page => page.evaluate(() => window.__messages.filter(m => m.type === 'shortcut').map(m => m.action));
const typedInput = page => page.evaluate(() => window.__messages.filter(m => m.type === 'input').map(m => m.data));

test('Navigation shortcuts reach the host as actions and never the program', async ({page}) => {
  await page.evaluate(table => window.__hostMessage({type:'shortcuts', shortcuts:table, sessionModifier:'Alt'}), navigationTable);
  await page.keyboard.press('Control+2');
  await page.keyboard.press('Control+0');
  await page.keyboard.press('Control+Tab');
  await page.keyboard.press('Control+Shift+Tab');
  await page.keyboard.press('Control+Alt+A');
  await expect.poll(() => shortcutActions(page)).toEqual(['window2', 'window0', 'windowNext', 'windowPrevious', 'favorite:f1']);
  expect(await typedInput(page)).toEqual([]);
});

test('Ctrl+< (the ISO key next to the left Shift) is a navigation shortcut', async ({page}) => {
  await page.evaluate(table => window.__hostMessage({type:'shortcuts', shortcuts:table, sessionModifier:'Alt'}), navigationTable);
  await page.locator('.session.active .xterm-helper-textarea').evaluate(el => el.dispatchEvent(new KeyboardEvent('keydown', { code:'IntlBackslash', key:'<', ctrlKey:true, bubbles:true, cancelable:true })));
  await expect.poll(() => shortcutActions(page)).toEqual(['windowBack']);
  expect(await typedInput(page)).toEqual([]);
});

test('Ctrl+K and other readline keys still reach the shell; held keys fire once', async ({page}) => {
  await page.evaluate(table => window.__hostMessage({type:'shortcuts', shortcuts:table, sessionModifier:'Alt'}), navigationTable);
  await page.keyboard.press('Control+k');
  await expect.poll(() => typedInput(page)).toEqual(['\x0b']);
  await page.keyboard.down('Control');
  await page.keyboard.down('2'); await page.keyboard.down('2'); await page.keyboard.down('2');
  await page.keyboard.up('2'); await page.keyboard.up('Control');
  await expect.poll(() => shortcutActions(page)).toEqual(['window2']);
});

test('A changed shortcut table replaces the old one', async ({page}) => {
  await page.evaluate(table => window.__hostMessage({type:'shortcuts', shortcuts:table, sessionModifier:'Alt'}), { ...navigationTable, window2:'Alt+Shift+2' });
  await page.keyboard.press('Control+2');
  await page.waitForTimeout(150); // Ctrl+2 is no shortcut any more: the host hears nothing
  expect(await shortcutActions(page)).toEqual([]);
  await page.keyboard.press('Alt+Shift+2');
  await expect.poll(() => shortcutActions(page)).toEqual(['window2']);
});
