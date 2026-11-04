"use strict";

// UI language. Turkish is the source: the HTML and the scripts keep their Turkish text, which is also
// the key into i18n/en.json (shared with the C# side). A text without a translation stays Turkish.
//   t("Oyun modu açık · {game}", { game })  ->  translated text with {placeholders} filled in
//   applyI18n(root)                          ->  translates text nodes and placeholder/title/aria-label/alt
// The host injects window.__gecitI18n = { lang, dict } before any page script runs; a page opened in a
// browser tab (newtab.html) only gets "?l=en" and loads the dictionary itself.
(() => {
  const boot = window.__gecitI18n;
  const lang = boot?.lang ?? (new URLSearchParams(location.search).get("l") === "en" ? "en" : "tr");
  let dict = boot?.dict ?? {};
  const ATTRS = ["placeholder", "title", "aria-label", "alt"];
  const norm = (s) => s.replace(/\s+/g, " ").trim();
  const lookup = (s) => (lang === "en" && Object.prototype.hasOwnProperty.call(dict, s) ? dict[s] : null);

  function t(s, vars) {
    let out = lookup(s) ?? s;
    if (vars) out = out.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m));
    return out;
  }

  function translateText(node) {
    const raw = node.nodeValue;
    const en = lookup(norm(raw));
    if (en == null) return;
    node.nodeValue = raw.match(/^\s*/)[0] + en + raw.match(/\s*$/)[0];
  }

  function walk(node) {
    if (node.nodeType === Node.TEXT_NODE) return translateText(node);
    if (node.nodeType !== Node.ELEMENT_NODE) return;
    if (node.getAttribute("translate") === "no" || node.tagName === "SCRIPT" || node.tagName === "STYLE") return;
    for (const a of ATTRS) {
      const v = node.getAttribute(a);
      const en = v == null ? null : lookup(norm(v));
      if (en != null) node.setAttribute(a, en);
    }
    for (const c of node.childNodes) walk(c);
  }

  function applyI18n(root = document.documentElement) {
    if (lang === "en") walk(root);
  }

  window.t = t;
  window.applyI18n = applyI18n;
  window.LANG = lang;                                   // for localeCompare / toLocaleLowerCase
  window.LOCALE = lang === "en" ? "en-US" : "tr-TR";    // for dates and numbers
  document.documentElement.lang = lang;

  if (lang !== "en") {
    window.i18nReady = Promise.resolve();
    return;
  }
  // Hidden until translated, so the Turkish text never flashes.
  document.documentElement.style.visibility = "hidden";
  const dictReady = boot ? Promise.resolve() : fetch("/i18n/en.json").then((r) => r.json()).then((d) => { dict = d; }).catch(() => {});
  const domReady = document.readyState === "loading"
    ? new Promise((resolve) => document.addEventListener("DOMContentLoaded", resolve, { once: true }))
    : Promise.resolve();
  window.i18nReady = Promise.all([dictReady, domReady]).then(() => {
    applyI18n();
    document.documentElement.style.visibility = "";
  });
})();
