(() => {
  'use strict';
  const sessions = new Map();
  const container = document.getElementById('terminals');
  let active = null;
  let fontSize = 14;
  const send = message => window.chrome.webview.postMessage(message);
  // App shortcuts (for example dictation on Ctrl+Y) are taken here and never reach the session. The host sends the
  // configured ones as canonical text ("Ctrl+Shift+D"); key names match Shortcut.FromDomCode in TerminalMgr.Core.
  const keyNames = { Escape:'Esc', Comma:',', Period:'.', Minus:'-', Slash:'/', Semicolon:';', Quote:"'", BracketLeft:'[', BracketRight:']',
    Backquote:'`', Backslash:'\\', Equal:'=', IntlBackslash:'<', ArrowUp:'Up', ArrowDown:'Down', ArrowLeft:'Left', ArrowRight:'Right', NumpadEnter:'Enter',
    Enter:'Enter', Space:'Space', Tab:'Tab', Backspace:'Backspace', Delete:'Delete', Insert:'Insert', Home:'Home', End:'End', PageUp:'PageUp', PageDown:'PageDown' };
  function keyOf(code) {
    let m;
    if ((m = /^Key([A-Z])$/.exec(code)) || (m = /^Digit([0-9])$/.exec(code)) || (m = /^Numpad([0-9])$/.exec(code))) return m[1];
    if (/^F([1-9]|1[0-2])$/.test(code)) return code;
    return keyNames[code] ?? null;
  }
  function parseShortcut(text, action) {
    const parts = String(text).split('+').map(p => p.trim());
    const s = { action, ctrl:false, alt:false, shift:false, key:parts[parts.length - 1] };
    for (const p of parts.slice(0, -1)) { const l = p.toLowerCase(); if (l === 'ctrl') s.ctrl = true; else if (l === 'alt') s.alt = true; else if (l === 'shift') s.shift = true; }
    return s;
  }
  // Command palette: opens at once with the last list the host sent; the host answers "paletteOpened" with a fresh one.
  const palette = window.TerminalPalette.create({ send, onClosed: () => sessions.get(active)?.term.focus() });
  function togglePalette() {
    if (palette.isOpen()) { palette.close(); return; }
    palette.open(null);
    send({ type:'paletteOpened' });
  }
  const parseModifier = text => { const s = parseShortcut(text + '+X'); return { ctrl:s.ctrl, alt:s.alt, shift:s.shift }; };
  // Defaults until the host sends the configured ones: dictation, copy, paste, and Alt+1..9 for switching sessions.
  const terminalSettings = { osc52:true, debug:false };
  let shortcuts = [parseShortcut('Ctrl+Y', 'dictation'), parseShortcut('Ctrl+Shift+C', 'copy'), parseShortcut('Ctrl+Shift+V', 'paste')];
  let sessionModifier = parseModifier('Alt');
  // Terminals pad lines with spaces up to the window width; copied text must not carry them.
  const trimTrailingSpaces = text => text.replace(/\r\n?/g, '\n').split('\n').map(l => l.replace(/ +$/, '')).join('\n');
  function fitActive() {
    const s = sessions.get(active);
    if (!s || container.clientWidth < 10 || container.clientHeight < 10) return;
    s.fit.fit();
    send({ type:'resize', id:active, cols:s.term.cols, rows:s.term.rows });
  }
  function select(id) {
    active = sessions.has(id) ? id : null;
    for (const [key,s] of sessions) s.element.classList.toggle('active', key === active);
    document.getElementById('empty').style.display = active ? 'none' : 'flex';
    requestAnimationFrame(() => { fitActive(); sessions.get(active)?.term.focus(); });
  }
  function create(id) {
    if (sessions.has(id)) return;
    const element = document.createElement('div');
    element.className = 'session';
    container.append(element);
    const term = new Terminal({
      cursorBlink:true, fontFamily:'Cascadia Mono, Consolas, monospace',
      fontSize, scrollback:10000, allowProposedApi:false,
      theme:{ background:'#0b1014',foreground:'#dbe7ed',cursor:'#37d5b5',selectionBackground:'#28554f',
        black:'#172129',red:'#f27886',green:'#63d6a0',yellow:'#dfc378',blue:'#76abef',magenta:'#c19aea',cyan:'#66cbd5',white:'#dce5ed' }
    });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(element);
    // Clipboard is only changed through an explicit copy action.
    // OSC 52 ("set clipboard"): tmux forwards what Claude Code or copy-mode copies this way. Write requests go to the host,
    // which decodes and validates them; read requests ("?") are never answered, so a program can not read the clipboard.
    term.parser.registerOscHandler(52, data => {
      if (terminalSettings.osc52 && data.length <= 4_000_000) send({ type:'osc52', id, data });
      return true;
    });
    term.onData(data => send({ type:'input',id,data }));
    term.onResize(({cols,rows}) => send({ type:'resize',id,cols,rows }));
    // Copies the selection (without padding spaces) to the host and clears it, so the next right click pastes.
    const copySelection = () => {
      if (!term.hasSelection()) return;
      const text = trimTrailingSpaces(term.getSelection());
      term.clearSelection();
      if (text) send({ type:'copy',id,text });
    };
    term.attachCustomKeyEventHandler(e => {
      if (!e.metaKey) {
        const key = keyOf(e.code);
        const fire = action => { e.preventDefault(); if (e.type === 'keydown' && !e.repeat) action(); return false; };
        if (key && /^[1-9]$/.test(key) && e.ctrlKey === sessionModifier.ctrl && e.altKey === sessionModifier.alt && e.shiftKey === sessionModifier.shift)
          return fire(() => send({ type:'switch', index:Number(key) - 1 }));
        const hit = key && shortcuts.find(s => s.ctrl === e.ctrlKey && s.alt === e.altKey && s.shift === e.shiftKey && s.key === key);
        if (hit) {
          if (hit.action === 'copy') return fire(copySelection);
          if (hit.action === 'paste') return fire(() => send({ type:'pasteRequest', id }));
          if (hit.action === 'palette') return fire(togglePalette);
          return fire(() => send({ type:'shortcut', action:hit.action, id }));
        }
      }
      return true; // Includes the tmux prefix Ctrl+B and all subsequent keys.
    });
    // PuTTY-style mouse: right click copies a selection (and clears it), otherwise pastes the active
    // clipboard entry. The right button is never forwarded to the session, even with mouse tracking
    // (tmux "mouse on", vim, htop). Shift + left drag still selects locally.
    // The selection is taken when the right button goes down: later output (tmux status line, redraws) may clear it
    // before the contextmenu event arrives.
    let rightSelection = '';
    for (const type of ['mousedown', 'mouseup', 'pointerdown', 'pointerup', 'auxclick'])
      element.addEventListener(type, e => {
        if (e.button !== 2) return;
        if (e.type === 'mousedown' || e.type === 'pointerdown') rightSelection = term.hasSelection() ? term.getSelection() : '';
        e.stopPropagation();
      }, true);
    element.addEventListener('contextmenu', e => {
      e.preventDefault();
      e.stopPropagation();
      const text = trimTrailingSpaces(rightSelection || (term.hasSelection() ? term.getSelection() : ''));
      rightSelection = '';
      if (terminalSettings.debug)
        send({ type:'diag', id, text:`Rechtsklick: Markierung ${text.length} Zeichen, Maus-Tracking ${term.modes.mouseTrackingMode}` });
      if (text) { term.clearSelection(); send({ type:'copy',id,text }); }
      else send({ type:'contextPaste',id });
    }, true);
    element.addEventListener('dragover', e => e.preventDefault());
    element.addEventListener('drop', e => {
      e.preventDefault();
      const text = e.dataTransfer?.getData('text/plain');
      if (text) term.paste(text);
    });
    sessions.set(id,{term,fit,element});
    select(id);
    fitActive();
    send({ type:'created',id,cols:term.cols,rows:term.rows });
  }
  // Dictation engine "Web Speech API": the host starts and stops the recognizer, the results go back as messages.
  let recognition = null;
  function speechCommand(m) {
    const post = event => send({ type:'speech', ...event });
    if (m.cmd === 'start') {
      const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
      if (!Recognition) { post({ event:'error', error:'unsupported' }); post({ event:'end' }); return; }
      recognition?.abort();
      const r = recognition = new Recognition();
      r.lang = m.lang || 'de-DE'; r.continuous = true; r.interimResults = true;
      r.onstart = () => post({ event:'started' });
      r.onresult = e => {
        for (let i = e.resultIndex; i < e.results.length; i++)
          post({ event: e.results[i].isFinal ? 'final' : 'interim', index:i, text:e.results[i][0].transcript });
      };
      r.onerror = e => post({ event:'error', error:e.error });
      r.onend = () => { if (recognition === r) recognition = null; post({ event:'end' }); };
      try { r.start(); } catch (x) { post({ event:'error', error:String(x) }); post({ event:'end' }); }
    }
    else if (m.cmd === 'stop') recognition?.stop();
    else if (m.cmd === 'abort') { const r = recognition; recognition = null; r?.abort(); }
  }
  window.chrome.webview.addEventListener('message', ({data:m}) => {
    const s = sessions.get(m.id);
    switch(m.type) {
      case 'speech': speechCommand(m); break;
      case 'palette':
        if (m.cmd === 'open') { if (!palette.isOpen()) { palette.open(m.items ?? null); send({ type:'paletteOpened' }); } }
        else if (m.cmd === 'close') palette.close();
        break;
      case 'paletteItems': palette.update(m.items ?? []); break;
      case 'create': create(m.id); break;
      case 'select': select(m.id); break;
      case 'output': s?.term.write(m.data); break;
      case 'paste':
        s?.term.paste(m.text);
        // "Paste and run": Enter goes through the same input path, so it always follows the pasted text.
        if (m.enter) s?.term.input('\r', true);
        s?.term.focus();
        break;
      case 'terminalSettings':
        terminalSettings.osc52 = m.osc52 !== false;
        terminalSettings.debug = m.debug === true;
        break;
      case 'shortcuts':
        shortcuts = Object.entries(m.shortcuts ?? {}).map(([action, text]) => parseShortcut(text, action));
        if (m.sessionModifier) sessionModifier = parseModifier(m.sessionModifier);
        break;
      case 'font':
        fontSize = m.size;
        for (const session of sessions.values()) session.term.options.fontSize = fontSize;
        fitActive(); break;
      case 'remove':
        if (s) { s.term.dispose();s.element.remove();sessions.delete(m.id); }
        if (active === m.id) select(null);
        break;
      case 'focus': s?.term.focus();break;
    }
  });
  let resizeTimer;
  new ResizeObserver(() => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(fitActive, 40);
  }).observe(container);
  send({type:'ready'});
})();
