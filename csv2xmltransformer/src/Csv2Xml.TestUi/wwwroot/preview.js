// Renders the preview embedded in #data: XML/XSD and JSON as foldable, coloured trees; CSV as a table with column numbers.
// All content goes in via textContent, never innerHTML, so file contents cannot inject markup.
(() => {
  'use strict';
  const data = JSON.parse(document.getElementById('data').textContent);
  const root = document.getElementById('root');

  const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  };
  const span = (cls, text) => el('span', cls, text);
  const button = (label, title, onclick) => { const b = el('button', '', label); b.title = title; b.onclick = onclick; return b; };

  // Whitespace and invisible characters made visible (header mismatches are often a trailing space, NBSP or BOM).
  const WS = { ' ': '·', '\t': '→', ' ': '⍽', '﻿': '[BOM]', '\r': '␍', '\n': '␊' };
  let showWs = false;
  function textNode(s) {
    if (!showWs) return document.createTextNode(s);
    const f = document.createDocumentFragment();
    for (const part of s.split(/([ \t ﻿\r\n])/)) {
      if (!part) continue;
      f.append(WS[part] ? span('ws', WS[part]) : document.createTextNode(part));
    }
    return f;
  }

  // ---------- toolbar ----------
  const bar = el('div', 'pv-bar');
  bar.append(el('h1', '', data.title), span('badge', data.format.toUpperCase()));
  bar.append(el('div', 'spacer'));
  const find = el('input'); find.type = 'text'; find.placeholder = 'Find…';
  bar.append(find);
  const body = el('div', 'pv-body');
  root.append(bar);
  if (data.note) root.append(el('div', 'pv-note' + (data.format === 'text' && !data.text ? ' err' : ''), data.note));
  root.append(body);

  let wrap = false, raw = false;
  const theme = () => {
    const next = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem('c2x-theme', next); } catch { /* storage unavailable: theme still switches */ }
  };

  // ---------- XML ----------
  function xmlTree(text) {
    const doc = new DOMParser().parseFromString(text, 'application/xml');
    const err = doc.querySelector('parsererror');
    if (err) return { error: 'Not well-formed XML: ' + err.textContent.trim().split('\n')[0] };
    const tree = el('div', 'tree');
    const decl = /^﻿?\s*(<\?xml[^?]*\?>)/.exec(text);
    if (decl) tree.append(line([span('x-decl', decl[1])]));
    for (const n of doc.childNodes) { const r = xmlNode(n); if (r) tree.append(r); }
    return { tree };
  }

  function line(parts, tog) {
    const l = el('div', 'line');
    if (tog) l.append(tog);
    l.append(...parts);
    return l;
  }

  function openTag(n, selfClose) {
    const parts = [span('x-p', '<'), span('x-tag', n.nodeName)];
    for (const a of n.attributes) parts.push(' ', span('x-attr', a.name), span('x-p', '="'), span('x-val', a.value), span('x-p', '"'));
    parts.push(span('x-p', selfClose ? '/>' : '>'));
    return parts;
  }
  const closeTag = n => [span('x-p', '</'), span('x-tag', n.nodeName), span('x-p', '>')];

  function xmlNode(n) {
    switch (n.nodeType) {
      case 1: {
        const kids = [...n.childNodes].filter(c => !(c.nodeType === 3 && !c.nodeValue.trim()));
        if (kids.length === 0) return line(openTag(n, true));
        if (kids.length === 1 && (kids[0].nodeType === 3 || kids[0].nodeType === 4)) {
          const t = span('x-text'); t.append(textNode(kids[0].nodeValue));
          return line([...openTag(n), t, ...closeTag(n)]);
        }
        return foldable(openTag(n), kids.map(xmlNode).filter(Boolean), closeTag(n), `${kids.length} child${kids.length === 1 ? '' : 'ren'}`);
      }
      case 3: { const t = span('x-text'); t.append(textNode(n.nodeValue.trim())); return line([t]); }
      case 4: return line([span('x-p', '<![CDATA['), span('x-text', n.nodeValue), span('x-p', ']]>')]);
      case 7: return line([span('x-decl', `<?${n.target} ${n.data}?>`)]);
      case 8: return line([span('x-comment', `<!--${n.nodeValue}-->`)]);
      default: return null;
    }
  }

  function foldable(head, children, tail, summary) {
    const node = el('div', 'node');
    const tog = el('span', 'tog', '−');
    const flip = () => { node.classList.toggle('collapsed'); tog.textContent = node.classList.contains('collapsed') ? '+' : '−'; };
    tog.onclick = flip;
    // shown only while collapsed: "… 3 children </Order>" on the opening line
    const ell = span('ellipsis', ` … ${summary} `);
    ell.append(...tail.map(t => t.cloneNode(true)));
    ell.onclick = flip;
    const kids = el('div', 'kids');
    kids.append(...children);
    const close = line(tail);
    close.classList.add('close');
    node.append(line([...head, ell], tog), kids, close);
    return node;
  }

  // ---------- JSON ----------
  function jsonTree(text) {
    let v;
    try { v = JSON.parse(text); } catch (e) { return { error: 'Not valid JSON: ' + e.message }; }
    const tree = el('div', 'tree');
    tree.append(jsonNode(v, null, true));
    return { tree };
  }

  function jsonScalar(v) {
    if (typeof v === 'string') { const s = span('j-str'); s.append('"', textNode(v), '"'); return s; }
    if (typeof v === 'number') return span('j-num', String(v));
    return span('j-lit', String(v));
  }

  function jsonNode(v, key, last) {
    const keyParts = key === null ? [] : [span('j-key', JSON.stringify(key)), span('x-p', ': ')];
    const comma = last ? [] : [span('x-p', ',')];
    if (v === null || typeof v !== 'object') return line([...keyParts, jsonScalar(v), ...comma]);
    const arr = Array.isArray(v);
    const entries = arr ? v.map((x, i) => [null, x]) : Object.entries(v);
    const [o, c] = arr ? ['[', ']'] : ['{', '}'];
    if (entries.length === 0) return line([...keyParts, span('x-p', o + c), ...comma]);
    return foldable([...keyParts, span('x-p', o)],
      entries.map(([k, x], i) => jsonNode(x, k, i === entries.length - 1)),
      [span('x-p', c), ...comma], `${entries.length} ${arr ? 'item' : 'key'}${entries.length === 1 ? '' : 's'}`);
  }

  // ---------- CSV ----------
  function csvTable(rows) {
    const t = el('table', 'csv');
    if (!rows.length) return { error: 'The file is empty.' };
    const [head, ...data] = rows;
    const counts = new Map();
    head.forEach(h => counts.set(h.trim().toLowerCase(), (counts.get(h.trim().toLowerCase()) || 0) + 1));
    const thead = el('thead'), hr = el('tr');
    hr.append(el('th', 'rn', 'row'));
    head.forEach((h, i) => {
      const th = el('th', counts.get(h.trim().toLowerCase()) > 1 || !h.trim() ? 'dup' : '');
      th.append(span('num', `#${i + 1}`), textNode(h));
      if (th.className) th.title = h.trim() ? 'duplicate header' : 'blank header';
      hr.append(th);
    });
    thead.append(hr); t.append(thead);
    const tb = el('tbody');
    data.forEach((r, i) => {
      const tr = el('tr');
      const blank = r.every(c => !c.trim());
      if (!blank && r.length !== head.length) { tr.className = 'bad'; tr.title = `${r.length} fields; the header has ${head.length}`; }
      tr.append(el('td', 'rn', String(i + 2)));
      r.forEach(c => { const td = el('td', c ? '' : 'empty'); td.append(textNode(c)); tr.append(td); });
      tb.append(tr);
    });
    t.append(tb);
    return { tree: t, summary: `${data.length} data rows · ${head.length} columns` };
  }

  // ---------- assembly ----------
  function render() {
    body.replaceChildren();
    if (raw) {
      const p = el('pre', 'raw' + (wrap ? ' wrap' : ''));
      p.append(textNode(data.text));
      body.append(p);
      return;
    }
    const r = data.format === 'xml' ? xmlTree(data.text)
      : data.format === 'json' ? jsonTree(data.text)
      : data.format === 'csv' ? csvTable(data.rows || [])
      : null;
    if (!r) { const p = el('pre', 'raw' + (wrap ? ' wrap' : '')); p.append(textNode(data.text)); body.append(p); return; }
    if (r.error) {
      body.append(el('div', 'msg err', r.error));
      const p = el('pre', 'raw'); p.append(textNode(data.text)); body.append(p);
      return;
    }
    if (wrap) r.tree.classList.add('wrap');
    if (r.summary) body.append(el('div', 'muted', r.summary));
    body.append(r.tree);
    applyFind();
  }

  function setDepth(d) {
    // d = levels left open; Infinity expands everything
    body.querySelectorAll('.node').forEach(n => {
      let depth = 0;
      for (let p = n.parentElement; p && p !== body; p = p.parentElement) if (p.classList.contains('node')) depth++;
      const collapse = depth >= d;
      n.classList.toggle('collapsed', collapse);
      n.querySelector(':scope > .line > .tog').textContent = collapse ? '+' : '−';
    });
  }

  function applyFind() {
    const q = find.value.trim().toLowerCase();
    if (data.format === 'csv') {
      body.querySelectorAll('tbody tr').forEach(tr => { tr.hidden = !!q && !tr.textContent.toLowerCase().includes(q); });
      return;
    }
    body.querySelectorAll('.line').forEach(l => l.classList.toggle('hit', !!q && l.textContent.toLowerCase().includes(q)));
    if (q) {
      // open every ancestor of a hit so it is visible
      body.querySelectorAll('.line.hit').forEach(l => {
        for (let p = l.parentElement; p && p !== body; p = p.parentElement)
          if (p.classList.contains('node') && p.firstChild !== l) { p.classList.remove('collapsed'); p.querySelector(':scope > .line > .tog').textContent = '−'; }
      });
      body.querySelector('.line.hit')?.scrollIntoView({ block: 'center' });
    }
  }
  find.oninput = applyFind;

  const tools = el('div', 'group');
  if (data.format === 'xml' || data.format === 'json') {
    tools.append(
      button('+', 'Expand all', () => setDepth(Infinity)),
      button('−', 'Collapse all', () => setDepth(0)),
      button('1', 'Open one level', () => setDepth(1)),
      button('2', 'Open two levels', () => setDepth(2)),
      button('3', 'Open three levels', () => setDepth(3)),
      button('Raw', 'Toggle raw text', e => { raw = !raw; e.target.classList.toggle('primary', raw); render(); }));
  }
  tools.append(
    button('␣', 'Show whitespace, NBSP and BOM', e => { showWs = !showWs; e.target.classList.toggle('primary', showWs); render(); }),
    button('↵', 'Wrap long lines', e => { wrap = !wrap; e.target.classList.toggle('primary', wrap); render(); }));
  if (data.text) tools.append(button('Copy', 'Copy the text', () => navigator.clipboard.writeText(data.text)));
  tools.append(button('◐', 'Light / dark', theme));
  bar.append(tools);

  document.title = data.title + ' — preview';
  render();
})();
