(() => {
  'use strict';
  const sessions = new Map();
  const container = document.getElementById('terminals');
  let active = null;
  let fontSize = 14;
  const send = message => window.chrome.webview.postMessage(message);
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
    term.parser.registerOscHandler(52, () => true);
    term.onData(data => send({ type:'input',id,data }));
    term.onResize(({cols,rows}) => send({ type:'resize',id,cols,rows }));
    term.attachCustomKeyEventHandler(e => {
      if (e.altKey && !e.ctrlKey && /^[1-9]$/.test(e.key)) {
        if (e.type === 'keydown') send({ type:'switch',index:Number(e.key)-1 });
        return false;
      }
      if (e.ctrlKey && e.shiftKey && e.code === 'KeyC') {
        if (e.type === 'keydown' && term.hasSelection()) {
          const text = trimTrailingSpaces(term.getSelection());
          term.clearSelection();
          if (text) send({ type:'copy',id,text });
        }
        return false;
      }
      if (e.ctrlKey && e.shiftKey && e.code === 'KeyV') {
        if (e.type === 'keydown') send({ type:'pasteRequest',id });
        return false;
      }
      return true; // Includes the tmux prefix Ctrl+B and all subsequent keys.
    });
    // PuTTY-style mouse: right click copies a selection (and clears it), otherwise pastes the active
    // clipboard entry. The right button is never forwarded to the session, even with mouse tracking
    // (tmux "mouse on", vim, htop). Shift + left drag still selects locally.
    for (const type of ['mousedown', 'mouseup', 'pointerdown', 'pointerup', 'auxclick'])
      element.addEventListener(type, e => { if (e.button === 2) e.stopPropagation(); }, true);
    element.addEventListener('contextmenu', e => {
      e.preventDefault();
      e.stopPropagation();
      if (term.hasSelection()) {
        const text = trimTrailingSpaces(term.getSelection());
        term.clearSelection();
        if (text) send({ type:'copy',id,text });
      } else send({ type:'contextPaste',id });
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
  window.chrome.webview.addEventListener('message', ({data:m}) => {
    const s = sessions.get(m.id);
    switch(m.type) {
      case 'create': create(m.id); break;
      case 'select': select(m.id); break;
      case 'output': s?.term.write(m.data); break;
      case 'paste': s?.term.paste(m.text); s?.term.focus(); break;
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
