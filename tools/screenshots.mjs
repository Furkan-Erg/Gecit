// Takes the README screenshots from a running Geçit through the WebView2 DevTools protocol.
//
//   "C:\Program Files\Gecit\Gecit.exe" --exit
//   $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9333"; & "C:\Program Files\Gecit\Gecit.exe"
//   node tools/screenshots.mjs --lang en --out docs/screenshots/en      (Turkish: --lang tr --out docs/screenshots)
//   then restart Geçit normally so the debugging port is closed.
//
// Each page is shot at 1120x1000 after running what makes it look alive (connection test, DNS benchmark,
// speed test, network scan). The second half of every MAC address is masked. The user's language
// setting is put back at the end. No dependencies (Node 22+: global fetch and WebSocket).
import fs from 'node:fs';
import path from 'node:path';

const arg = (name, def) => { const i = process.argv.indexOf('--' + name); return i > 0 ? process.argv[i + 1] : def; };
const PORT = Number(arg('port', 9333));
const LANG = arg('lang', 'en');
const OUT = arg('out', LANG === 'tr' ? 'docs/screenshots' : `docs/screenshots/${LANG}`);
const ONLY = arg('only', '');   // e.g. --only dpi,lan
// Personal names on the page (device names, Wi-Fi name) to replace: --redact "Old name=New name;Other=Another".
// Pass them on the command line; don't write real names into this file.
const REDACT = arg('redact', '').split(';').filter(Boolean).map(p => p.split('='));
const sleep = ms => new Promise(r => setTimeout(r, ms));

// page id -> [what to click first (selector or null), how long to wait in ms]
const PAGES = [
  ['dpi', '#probe-run', 9000],
  ['dns', '#dns-bench', 12000],
  ['browser', null, 1500],
  ['game', null, 4000],
  ['net', '#sp-start', 32000],
  ['lan', null, 15000],
  ['demos', null, 9000],
  ['free', null, 7000],
  ['boot', null, 4000],
];

const targets = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json().catch(() => {
  throw new Error(`No WebView2 debugging port on ${PORT}. Start Geçit with WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=${PORT}`);
});
const page = targets.find(t => t.type === 'page' && /gecit\.example\/index\.html/.test(t.url));
if (!page) throw new Error('Main Geçit window not found among: ' + targets.map(t => t.url).join(', '));

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let nextId = 1;
const waiting = new Map();
ws.onmessage = e => {
  const m = JSON.parse(e.data);
  const w = waiting.get(m.id);
  if (!w) return;
  waiting.delete(m.id);
  m.error ? w.rej(new Error(m.error.message)) : w.res(m.result);
};
const send = (method, params = {}) => new Promise((res, rej) => {
  const id = nextId++;
  waiting.set(id, { res, rej });
  ws.send(JSON.stringify({ id, method, params }));
});
const evaluate = async expr => {
  const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
};
const waitReady = async () => {
  for (let i = 0; i < 50; i++) {
    try { if (await evaluate('document.readyState === "complete" && typeof state !== "undefined" && !!state')) return; } catch { /* reloading */ }
    await sleep(200);
  }
  throw new Error('Page did not become ready');
};

await waitReady();
const before = await evaluate('state.language');
const setLang = async lang => {
  if (await evaluate('state.lang') === lang && before === lang) return;
  try { await evaluate(`call("app.language", { language: ${JSON.stringify(lang)} }).then(() => true)`); }
  catch { /* the page reloads under us */ }
  await sleep(1500);
  await waitReady();
};

// Masks the second half of every MAC address (vendor half stays) and replaces --redact names. A MutationObserver
// re-applies it whenever the page re-renders, so a late scan result can't undo it.
const SCRUB = `window.__scrub = window.__scrub || (() => {
  const pairs = ${JSON.stringify(REDACT)};
  const mac = /\\b([0-9A-Fa-f]{2}[:-]){2}[0-9A-Fa-f]{2}[:-][0-9A-Fa-f]{2}[:-][0-9A-Fa-f]{2}[:-][0-9A-Fa-f]{2}\\b/g;
  const fix = () => {
    const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n; (n = w.nextNode());) {
      let v = n.nodeValue.replace(mac, m => m.slice(0, 8) + ':••:••:••');
      for (const [a, b] of pairs) v = v.split(a).join(b);
      if (v !== n.nodeValue) n.nodeValue = v;
    }
  };
  new MutationObserver(fix).observe(document.body, { childList: true, subtree: true, characterData: true });
  fix();
  return fix;
})()`;

fs.mkdirSync(OUT, { recursive: true });
try {
  await setLang(LANG);
  await send('Emulation.setDeviceMetricsOverride', { width: 1120, height: 1000, deviceScaleFactor: 1, mobile: false });
  const only = ONLY ? ONLY.split(',') : null;
  for (const [id, click, wait] of PAGES) {
    if (only && !only.includes(id)) continue;
    await evaluate(`document.querySelector('[data-page="${id}"]').click(); scrollTo(0, 0); document.querySelector('.main')?.scrollTo(0, 0); true`);
    await sleep(800);
    if (click) await evaluate(`(b => { if (b && !b.disabled) b.click(); return !!b; })(document.querySelector(${JSON.stringify(click)}))`);
    await sleep(wait);
    // Store pages: wait (up to 20 s) until the cover images in view have loaded.
    for (let i = 0; i < 40; i++) {
      const loaded = await evaluate(`[...document.querySelectorAll('.page.active img')]
        .filter(im => { const r = im.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight && r.width > 0; })
        .every(im => im.complete && im.naturalWidth > 0)`);
      if (loaded) break;
      await sleep(500);
    }
    // The network page re-renders when a scan finishes, so wait for it and scrub again right before the shot.
    for (let i = 0; i < 60 && await evaluate(`document.querySelector('#lan-hero')?.dataset.state === 'scanning'`); i++) await sleep(500);
    await evaluate(`${SCRUB}; window.__scrub(); document.activeElement?.blur(); true`);
    await sleep(300);
    await evaluate('window.__scrub(); true');
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = path.join(OUT, id + '.png');
    fs.writeFileSync(file, Buffer.from(shot.data, 'base64'));
    console.log('saved', file);
  }
} finally {
  await send('Emulation.clearDeviceMetricsOverride').catch(() => {});
  if (before && before !== LANG) {
    try { await evaluate(`call("app.language", { language: ${JSON.stringify(before)} }).then(() => true)`); } catch { /* reload */ }
    console.log('language setting restored:', before);
  }
  ws.close();
}
