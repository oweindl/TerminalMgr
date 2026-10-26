// Command palette (Ctrl+Shift+K): an overlay in the terminal page, in HTML and CSS, because a WPF element would be hidden behind the WebView2.
// The host hands over the items (favorites, tmux windows of all connected servers, clipboard entries, actions); this file filters,
// ranks and shows them and reports what the user chose. While it is open every key goes to its input, none to the terminal.
(() => {
  'use strict';

  // Umlauts and accents are ignored ("ae" is not "ä", but "a" finds "ä"), case does not matter.
  const fold = text => String(text).normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
  const boundary = /[\s›:|/_\-.()]/;

  // Score of one search word in a text (both folded), or -1 when the word is not in it. A contiguous hit beats scattered letters,
  // hits at the start of a word beat hits inside a word, and earlier beats later.
  function scoreWord(word, text) {
    const at = text.indexOf(word);
    if (at >= 0) {
      const wordStart = at === 0 || boundary.test(text[at - 1]);
      return 100 + (wordStart ? 40 : 0) + (at === 0 ? 20 : 0) - Math.min(at, 30) * 0.5 + word.length;
    }
    let score = 0, last = -2, from = 0;
    for (const c of word) {
      const index = text.indexOf(c, from);
      if (index < 0) return -1;
      score += 1 + (index === last + 1 ? 4 : 0) + (index === 0 || boundary.test(text[index - 1]) ? 3 : 0);
      last = index; from = index + 1;
    }
    return score;
  }

  // Items that match every word of the query, best first. Words are looked for in the title first, then (with a penalty) in the subtitle.
  function rank(items, query) {
    const words = fold(query).split(/\s+/).filter(Boolean);
    if (words.length === 0) return [];
    const ranked = [];
    items.forEach((item, order) => {
      if (item.searchable === false) return;
      const title = fold(item.title), both = title + ' ' + fold(item.subtitle ?? '');
      let total = 0;
      for (const word of words) {
        const inTitle = scoreWord(word, title);
        const score = inTitle >= 0 ? inTitle : (() => { const s = scoreWord(word, both); return s < 0 ? -1 : s * 0.6; })();
        if (score < 0) return;
        total += score;
      }
      ranked.push({ item, score: total, order });
    });
    ranked.sort((a, b) => b.score - a.score || a.order - b.order);
    return ranked.map(r => r.item);
  }

  const groupOrder = ['Favoriten', 'Zuletzt benutzt', 'tmux-Fenster', 'Clipboard', 'Aktionen'];
  // Empty input: favorites and the windows used last, by group.
  function whenEmpty(items) {
    const shown = items.filter(i => i.showWhenEmpty);
    return groupOrder.flatMap(group => shown.filter(i => i.group === group)).concat(shown.filter(i => !groupOrder.includes(i.group)));
  }

  function create({ send, onClosed }) {
    const root = document.createElement('div');
    root.id = 'palette';
    root.hidden = true;
    root.innerHTML = `
      <div class="palette-box" role="dialog" aria-modal="true" aria-label="Befehlspalette">
        <input id="palette-input" type="text" autocomplete="off" spellcheck="false" role="combobox" aria-expanded="true"
               aria-controls="palette-list" aria-autocomplete="list" placeholder="Fenster, Favorit, Clipboard oder Aktion …">
        <ul id="palette-list" role="listbox"></ul>
        <div class="palette-foot"><span id="palette-count" aria-live="polite"></span><span>↑↓ wählen · Enter ausführen · Ctrl+D als Favorit · Esc schließen</span></div>
      </div>`;
    document.body.append(root);
    const input = root.querySelector('#palette-input');
    const list = root.querySelector('#palette-list');
    const count = root.querySelector('#palette-count');
    let items = [], shown = [], selected = 0, opened = false;

    const isOpen = () => opened;
    function visible() { return input.value.trim() ? rank(items, input.value) : whenEmpty(items); }

    function render(keepId) {
      shown = visible();
      selected = Math.max(0, keepId ? shown.findIndex(i => i.id === keepId) : 0);
      list.replaceChildren();
      let group = null;
      const searching = input.value.trim().length > 0;
      shown.forEach((item, index) => {
        if (!searching && item.group !== group) {
          group = item.group;
          const heading = document.createElement('li');
          heading.className = 'palette-group'; heading.setAttribute('role', 'presentation'); heading.textContent = group;
          list.append(heading);
        }
        const row = document.createElement('li');
        row.id = 'palette-item-' + index; row.className = 'palette-item'; row.setAttribute('role', 'option');
        row.dataset.index = String(index);
        const text = document.createElement('span'); text.className = 'palette-text';
        const title = document.createElement('span'); title.className = 'palette-title'; title.textContent = item.title;
        text.append(title);
        if (item.subtitle) { const sub = document.createElement('span'); sub.className = 'palette-sub'; sub.textContent = item.subtitle; text.append(sub); }
        row.append(text);
        if (searching) { const tag = document.createElement('span'); tag.className = 'palette-tag'; tag.textContent = item.group; row.append(tag); }
        if (item.shortcut) { const key = document.createElement('kbd'); key.textContent = item.shortcut; row.append(key); }
        list.append(row);
      });
      if (shown.length === 0) {
        const none = document.createElement('li');
        none.className = 'palette-empty'; none.setAttribute('role', 'presentation');
        none.textContent = items.length === 0 ? 'Keine Einträge. Server verbinden oder Favoriten anlegen.' : searching ? 'Nichts gefunden.' : 'Noch keine Favoriten oder zuletzt benutzten Fenster. Tippen zum Suchen.';
        list.append(none);
      }
      count.textContent = shown.length ? `${shown.length} Treffer` : '';
      mark();
    }

    function mark() {
      list.querySelectorAll('.palette-item').forEach(row => row.setAttribute('aria-selected', String(Number(row.dataset.index) === selected)));
      const row = list.querySelector(`#palette-item-${selected}`);
      if (row) { input.setAttribute('aria-activedescendant', row.id); row.scrollIntoView({ block: 'nearest' }); }
      else input.removeAttribute('aria-activedescendant');
    }

    function move(delta) {
      if (shown.length === 0) return;
      selected = (selected + delta + shown.length) % shown.length;
      mark();
    }

    function hide() { opened = false; root.hidden = true; input.value = ''; }
    function close() { if (!opened) return; hide(); send({ type: 'paletteClosed' }); onClosed?.(); }
    function run() {
      const item = shown[selected];
      if (!item) return;
      const query = input.value;
      hide();
      send({ type: 'paletteRun', id: item.id, query });
      onClosed?.();
    }
    function favorite() {
      const item = shown[selected];
      if (!item || !/^(window|favorite):/.test(item.id)) return;
      send({ type: 'paletteFavorite', id: item.id });
    }

    input.addEventListener('keydown', e => {
      e.stopPropagation();
      if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
      else if (e.key === 'PageDown') { e.preventDefault(); move(8); }
      else if (e.key === 'PageUp') { e.preventDefault(); move(-8); }
      else if (e.key === 'Enter') { e.preventDefault(); if (!e.isComposing) run(); }
      else if (e.key === 'Escape') { e.preventDefault(); close(); }
      else if (e.key === 'Tab') e.preventDefault();
      else if (e.ctrlKey && !e.altKey && !e.shiftKey && e.code === 'KeyD') { e.preventDefault(); favorite(); }
    });
    input.addEventListener('keyup', e => e.stopPropagation());
    input.addEventListener('keypress', e => e.stopPropagation());
    input.addEventListener('input', () => render());
    list.addEventListener('mousemove', e => {
      const row = e.target.closest?.('.palette-item');
      if (row && Number(row.dataset.index) !== selected) { selected = Number(row.dataset.index); mark(); }
    });
    list.addEventListener('click', e => {
      const row = e.target.closest?.('.palette-item');
      if (row) { selected = Number(row.dataset.index); run(); }
    });
    // A click on the dimmed background closes the palette.
    root.addEventListener('mousedown', e => { if (e.target === root) { e.preventDefault(); close(); } });

    return {
      isOpen,
      open(newItems) {
        items = newItems ?? items;
        opened = true; root.hidden = false; input.value = '';
        render();
        input.focus();
      },
      // The host's fresh list arrives a moment after opening: keep what the user typed and the entry that is selected.
      update(newItems) {
        items = newItems;
        if (opened) render(shown[selected]?.id);
      },
      close
    };
  }

  window.TerminalPalette = { create, rank, fold, whenEmpty };
})();
