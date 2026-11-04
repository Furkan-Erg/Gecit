#!/usr/bin/env node
// Translation coverage check (no dependencies): every Turkish UI text needs an English entry in
// src/Gecit/wwwroot/i18n/en.json, where the Turkish text itself is the key.
//
//   node tools/i18n-check.mjs            exit 0: nothing missing; exit 1: missing texts or bad placeholders
//
// What counts as a UI text:
//   - wwwroot/**/*.html: text nodes and placeholder/title/aria-label/alt (except inside translate="no")
//   - wwwroot/**/*.js and inline <script>s: every t("…") argument, and any other string with a Turkish letter
//   - src/Gecit/**/*.cs (and src/Gecit.Dns/DnsProviders.cs): every L.T("…") argument, and any other
//     string with a Turkish letter
// Comments and log lines are skipped. Unused dictionary entries are only a warning.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const APP = path.join(ROOT, "src", "Gecit");
const WEB = path.join(APP, "wwwroot");
const DICT_FILE = path.join(WEB, "i18n", "en.json");
const DNS_PROVIDERS = path.join(ROOT, "src", "Gecit.Dns", "DnsProviders.cs");

const TURKISH = /[çğıöşüÇĞİÖŞÜ]/;
const LETTER = /\p{L}/u;
const LOG_LINE = /AppendAllText\(|\bLog\(|\bDebug\.|\bTrace\.|\bConsole\.|console\.(log|warn|error|info)\(/;

// Same in both languages: names, keys, units, and match words that never reach the screen.
const ALLOW = new Set([
  "Geçit", "GEÇİT", "Geçit DNS", "Geçit DPI",
  "Ctrl", "Shift", "Tab", "Del", "F11", "Mbps", "ms", "bufferbloat", "Co-op", "Online co-op",
  "Cloudflare", "Google", "Discord", "YouTube", "Reddit", "GitHub", "Steam", "Epic", "Windows",
  "GoodbyeDPI-Turkey", "WinDivert", "GoodbyeDPI 0.2.3rc3 · WinDivert 2.2",
  "Arçelik",                      // brand (Net/OuiDatabase.cs)
  "akıllı ev", "wi-fi modülü",    // words matched in vendor names (Net/LanScanner.cs)
]);

// ---------------------------------------------------------------- files

function files(dir, exts) {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (!["bin", "obj", "node_modules"].includes(e.name)) out.push(...files(p, exts));
    } else if (exts.includes(path.extname(e.name))) out.push(p);
  }
  return out;
}

const rel = (p) => path.relative(ROOT, p).replaceAll("\\", "/");

function lineOf(src) {
  const starts = [0];
  for (let i = 0; i < src.length; i++) if (src[i] === "\n") starts.push(i + 1);
  return (pos) => {
    let lo = 0, hi = starts.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (starts[mid] <= pos) lo = mid; else hi = mid - 1;
    }
    return lo + 1;
  };
}

// ---------------------------------------------------------------- tokenizers
// Tokens: { type: "str" | "tpl" | "id" | "num" | "p", value, pos }; "tpl" has the literal text parts in value.

const JS_KEYWORDS = new Set(["return", "typeof", "case", "do", "else", "in", "of", "new", "delete", "void", "throw", "instanceof", "yield", "await"]);
const JS_ESC = { n: "\n", t: "\t", r: "\r", b: "\b", f: "\f", v: "\v", 0: "\0" };

function jsTokens(src, base = 0) {
  const out = [];
  let i = 0;
  const n = src.length;
  const push = (t) => out.push({ ...t, pos: t.pos + base });

  function escape() {   // at a backslash; returns the character
    const c = src[i + 1];
    if (c === "u" && src[i + 2] === "{") {
      const end = src.indexOf("}", i);
      const ch = String.fromCodePoint(parseInt(src.slice(i + 3, end), 16));
      i = end + 1;
      return ch;
    }
    if (c === "u") { const ch = String.fromCharCode(parseInt(src.slice(i + 2, i + 6), 16)); i += 6; return ch; }
    if (c === "x") { const ch = String.fromCharCode(parseInt(src.slice(i + 2, i + 4), 16)); i += 4; return ch; }
    i += 2;
    if (c === "\n") return "";
    return JS_ESC[c] ?? c;
  }

  function regexAllowed() {
    const last = out[out.length - 1];
    if (!last) return true;
    if (last.type === "id") return JS_KEYWORDS.has(last.value);
    if (last.type === "p") return !")]}".includes(last.value);
    return false;
  }

  // Scans until the end, or until the "}" closing a template hole when inHole.
  function scan(inHole) {
    let depth = 0;
    while (i < n) {
      const c = src[i];
      if (/\s/.test(c)) { i++; continue; }
      if (c === "/" && src[i + 1] === "/") { const e = src.indexOf("\n", i); i = e < 0 ? n : e; continue; }
      if (c === "/" && src[i + 1] === "*") { const e = src.indexOf("*/", i + 2); i = e < 0 ? n : e + 2; continue; }
      if (c === '"' || c === "'") {
        const pos = i;
        let s = "";
        i++;
        while (i < n && src[i] !== c && src[i] !== "\n") s += src[i] === "\\" ? escape() : src[i++];
        i++;
        push({ type: "str", value: s, pos });
        continue;
      }
      if (c === "`") {
        const pos = i;
        const parts = [];
        let s = "";
        i++;
        while (i < n && src[i] !== "`") {
          if (src[i] === "\\") s += escape();
          else if (src[i] === "$" && src[i + 1] === "{") {
            parts.push(s);
            s = "";
            i += 2;
            scan(true);
          } else s += src[i++];
        }
        i++;
        parts.push(s);
        push({ type: "tpl", value: parts, pos });
        continue;
      }
      if (c === "/" && regexAllowed()) {
        const pos = i;
        let inClass = false;
        i++;
        while (i < n && src[i] !== "\n") {
          if (src[i] === "\\") { i += 2; continue; }
          if (src[i] === "[") inClass = true;
          else if (src[i] === "]") inClass = false;
          else if (src[i] === "/" && !inClass) break;
          i++;
        }
        i++;
        while (i < n && /[a-z]/i.test(src[i])) i++;
        push({ type: "re", value: "", pos });
        continue;
      }
      if (/[A-Za-z_$]/.test(c)) {
        const pos = i;
        while (i < n && /[\w$]/.test(src[i])) i++;
        push({ type: "id", value: src.slice(pos, i), pos });
        continue;
      }
      if (/[0-9]/.test(c)) {
        const pos = i;
        while (i < n && /[\w.]/.test(src[i])) i++;
        push({ type: "num", value: src.slice(pos, i), pos });
        continue;
      }
      if (c === "{") depth++;
      if (c === "}") {
        if (inHole && depth === 0) { i++; return; }
        depth--;
      }
      push({ type: "p", value: c, pos: i });
      i++;
    }
  }
  scan(false);
  return out;
}

const CS_ESC = { n: "\n", t: "\t", r: "\r", 0: "\0", a: "\x07", b: "\b", f: "\f", v: "\v" };

function csTokens(src) {
  const out = [];
  let i = 0;
  const n = src.length;
  const push = (t) => out.push(t);

  function escape() {
    const c = src[i + 1];
    if (c === "u") { const ch = String.fromCharCode(parseInt(src.slice(i + 2, i + 6), 16)); i += 6; return ch; }
    if (c === "x") {
      let j = i + 2;
      while (j < i + 6 && /[0-9a-f]/i.test(src[j])) j++;
      const ch = String.fromCharCode(parseInt(src.slice(i + 2, j), 16));
      i = j;
      return ch;
    }
    i += 2;
    return CS_ESC[c] ?? c;
  }

  // Interpolated or plain string body starting after the opening quote(s).
  function body(pos, { verbatim, interpolated, raw }) {
    const parts = [];
    let s = "";
    while (i < n) {
      if (raw) {
        if (src.startsWith(raw, i)) { i += raw.length; break; }
        s += src[i++];
        continue;
      }
      const c = src[i];
      if (c === '"') {
        if (verbatim && src[i + 1] === '"') { s += '"'; i += 2; continue; }
        i++;
        break;
      }
      if (c === "\\" && !verbatim) { s += escape(); continue; }
      if (interpolated && c === "{") {
        if (src[i + 1] === "{") { s += "{"; i += 2; continue; }
        parts.push(s);
        s = "";
        i++;
        scan(true);
        continue;
      }
      if (interpolated && c === "}" && src[i + 1] === "}") { s += "}"; i += 2; continue; }
      if (c === "\n" && !verbatim) break;
      s += src[i++];
    }
    parts.push(s);
    push(interpolated ? { type: "tpl", value: parts, pos } : { type: "str", value: parts[0], pos });
  }

  function scan(inHole) {
    let depth = 0;
    while (i < n) {
      const c = src[i];
      if (/\s/.test(c)) { i++; continue; }
      if (c === "/" && src[i + 1] === "/") { const e = src.indexOf("\n", i); i = e < 0 ? n : e; continue; }
      if (c === "/" && src[i + 1] === "*") { const e = src.indexOf("*/", i + 2); i = e < 0 ? n : e + 2; continue; }
      if (c === "'") {   // char literal
        i++;
        while (i < n && src[i] !== "'") i += src[i] === "\\" ? 2 : 1;
        i++;
        continue;
      }
      const m = /^(\$+@?|@\$+|@)?("""+|")/.exec(src.slice(i, i + 12));
      if (m && (m[1] || c === '"')) {
        const pos = i;
        const prefix = m[1] ?? "";
        i += m[0].length;
        const quotes = m[2];
        body(pos, {
          verbatim: prefix.includes("@"),
          interpolated: prefix.includes("$") && quotes === '"',
          raw: quotes.length >= 3 ? quotes : null,
        });
        continue;
      }
      if (/[A-Za-z_@]/.test(c)) {
        const pos = i;
        i++;
        while (i < n && /\w/.test(src[i])) i++;
        push({ type: "id", value: src.slice(pos, i), pos });
        continue;
      }
      if (/[0-9]/.test(c)) {
        const pos = i;
        while (i < n && /[\w.]/.test(src[i])) i++;
        push({ type: "num", value: src.slice(pos, i), pos });
        continue;
      }
      if (c === "{") depth++;
      if (c === "}") {
        if (inHole && depth === 0) { i++; return; }
        depth--;
      }
      push({ type: "p", value: c, pos: i });
      i++;
    }
  }
  scan(false);
  return out;
}

// ---------------------------------------------------------------- collection

const dict = JSON.parse(fs.readFileSync(DICT_FILE, "utf8"));
const errors = [];
const warnings = [];
const seen = new Set();          // every text found in the sources (for the unused-entry warning)

const known = (s) => Object.prototype.hasOwnProperty.call(dict, s) || ALLOW.has(s);

function need(file, line, text, why) {
  if (!known(text)) errors.push(`${file}:${line}: ${why}: ${JSON.stringify(text)}`);
}

// Strings of a script: t("…") arguments always need an entry; other Turkish strings too.
function checkTokens(tokens, file, line, lines, isCall) {
  tokens.forEach((tok, k) => {
    if (tok.type !== "str" && tok.type !== "tpl") return;
    const ln = line(tok.pos);
    const parts = tok.type === "str" ? [tok.value] : tok.value;
    parts.forEach((p) => seen.add(p));
    if (LOG_LINE.test(lines[ln - 1] ?? "")) return;
    if (tok.type === "str" && isCall(tokens, k)) return need(file, ln, tok.value, "çeviri yok");
    if (tok.type === "tpl") {
      if (parts.some((p) => TURKISH.test(p)))
        errors.push(`${file}:${ln}: değişkenli Türkçe metin çeviriden geçmiyor (t / L.T ve {yer tutucu} kullan): ${JSON.stringify(parts.join("…"))}`);
      return;
    }
    if (TURKISH.test(tok.value)) need(file, ln, tok.value, "çeviri yok");
  });
}

// t("…") but not x.t("…")
const jsCall = (ts, k) => ts[k - 1]?.value === "(" && ts[k - 2]?.type === "id" && ts[k - 2].value === "t" && ts[k - 3]?.value !== ".";
// L.T("…")
const csCall = (ts, k) => ts[k - 1]?.value === "(" && ts[k - 2]?.value === "T" && ts[k - 3]?.value === "." && ts[k - 4]?.value === "L";

function scanJs(src, file, base = 0, fullSrc = src) {
  const line = lineOf(fullSrc);
  checkTokens(jsTokens(src, base), file, line, fullSrc.split("\n"), jsCall);
}

const VOID = new Set(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"]);
const ATTR = /\b(placeholder|title|aria-label|alt)\s*=\s*(?:"([^"]*)"|'([^']*)')/g;
const ENTITIES = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'", "#39": "'", nbsp: " " };
const decode = (s) => s.replace(/&(\w+|#\d+);/g, (m, e) => ENTITIES[e] ?? m);
const norm = (s) => decode(s).replace(/\s+/g, " ").trim();
const blank = (src, re) => src.replace(re, (m) => m.replace(/[^\n]/g, " "));

function htmlText(file, line, raw, pos, what) {
  const s = norm(raw);
  if (!LETTER.test(s) || [...s].length < 2) return;
  seen.add(s);
  need(file, line(pos), s, what);
}

function scanHtml(src, file) {
  const line = lineOf(src);
  for (const m of src.matchAll(/(<script\b[^>]*>)([\s\S]*?)<\/script>/gi))
    scanJs(m[2], file, m.index + m[1].length, src);
  let s = blank(src, /<!--[\s\S]*?-->|<![^>]*>/g);
  s = blank(s, /<(script|style)\b[^>]*>[\s\S]*?<\/\1>/gi);

  const stack = [];
  const off = () => stack.some((e) => e.no);
  let last = 0;
  for (const m of s.matchAll(/<(\/?)([a-zA-Z][\w-]*)([^>]*)>/g)) {
    if (!off()) htmlText(file, line, s.slice(last, m.index), last, "metin çevrilmemiş");
    last = m.index + m[0].length;
    const tag = m[2].toLowerCase();
    if (m[1]) {
      const at = stack.map((e) => e.tag).lastIndexOf(tag);
      if (at >= 0) stack.length = at;
      continue;
    }
    const attrs = m[3];
    const no = /\btranslate\s*=\s*["']no["']/i.test(attrs);
    if (!off() && !no)
      for (const a of attrs.matchAll(ATTR)) htmlText(file, line, a[2] ?? a[3], m.index, `${a[1]} çevrilmemiş`);
    if (!attrs.trimEnd().endsWith("/") && !VOID.has(tag)) stack.push({ tag, no });
  }
}

function scanCs(src, file) {
  const line = lineOf(src);
  checkTokens(csTokens(src), file, line, src.split("\n"), csCall);
}

for (const f of files(WEB, [".html"])) scanHtml(fs.readFileSync(f, "utf8"), rel(f));
for (const f of files(WEB, [".js"])) scanJs(fs.readFileSync(f, "utf8"), rel(f));
for (const f of files(APP, [".cs"])) scanCs(fs.readFileSync(f, "utf8"), rel(f));
// The DNS service is a separate process without the dictionary; the UI translates its provider texts.
scanCs(fs.readFileSync(DNS_PROVIDERS, "utf8"), rel(DNS_PROVIDERS));

// ---------------------------------------------------------------- the dictionary itself

const holes = (s) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(",");
for (const [tr, en] of Object.entries(dict)) {
  if (typeof en !== "string" || !en.trim()) {
    errors.push(`${rel(DICT_FILE)}: boş ya da metin olmayan karşılık: ${JSON.stringify(tr)}`);
    continue;
  }
  if (holes(tr) !== holes(en))
    errors.push(`${rel(DICT_FILE)}: yer tutucular farklı: ${JSON.stringify(tr)} {${holes(tr)}} ↔ ${JSON.stringify(en)} {${holes(en)}}`);
  if (!seen.has(tr)) warnings.push(`kullanılmayan çeviri: ${JSON.stringify(tr)}`);
}

for (const w of warnings) console.log("uyarı: " + w);
for (const e of errors) console.log(e);
const total = Object.keys(dict).length;
if (errors.length) {
  console.log(`i18n: ${errors.length} sorun (${total} çeviri)`);
  process.exit(1);
}
console.log(`i18n: eksik çeviri yok (${total} çeviri, ${warnings.length} uyarı)`);
