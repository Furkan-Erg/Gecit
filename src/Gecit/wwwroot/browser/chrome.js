"use strict";

const host = window.chrome.webview;
const send = (cmd, extra = {}) => host.postMessage({ cmd, ...extra });
const $ = (id) => document.getElementById(id);
const address = $("address");
let state = null;
let editing = false;   // don't overwrite what the user is typing

host.addEventListener("message", (e) => {
  if (e.data.focusAddress) {
    address.focus();
    address.select();
  }
  if (e.data.state) render(e.data.state);
});

function render(s) {
  state = s;
  const box = $("tabs");
  box.replaceChildren(...s.tabs.map((t) => {
    const el = document.createElement("div");
    el.className = "tab" + (t.id === s.activeId ? " active" : "");
    el.title = t.title;
    el.innerHTML = `<span class="fav"></span><span class="title"></span>
      <button class="x" title="Sekmeyi kapat (Ctrl+W)"><svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg></button>`;
    // Titles come from web pages: only ever textContent here, this page can command the host.
    const fav = el.querySelector(".fav");
    if (t.loading) {
      fav.innerHTML = '<span class="spinner"></span>';
    } else if (t.favicon?.startsWith("data:image/png;base64,")) {
      const img = document.createElement("img");
      img.src = t.favicon;
      fav.append(img);
    } else {
      const letter = document.createElement("span");
      letter.className = "letter";
      letter.textContent = (t.title || "?").trim().charAt(0).toUpperCase() || "?";
      fav.append(letter);
    }
    el.querySelector(".title").textContent = t.url ? t.title : "Yeni sekme";
    el.addEventListener("mousedown", (ev) => {
      if (ev.button === 1) { ev.preventDefault(); send("close", { id: t.id }); }
    });
    el.addEventListener("click", (ev) => {
      if (ev.target.closest(".x")) send("close", { id: t.id });
      else send("activate", { id: t.id });
    });
    return el;
  }));

  const a = s.tabs.find((t) => t.id === s.activeId);
  $("back").disabled = !s.canBack;
  $("forward").disabled = !s.canForward;
  $("reload").classList.toggle("loading", !!a?.loading);
  $("reload").title = a?.loading ? "Durdur" : "Yenile (F5)";

  if (!editing) address.value = a ? displayUrl(a.url) : "";
  const lock = $("lock");
  lock.classList.toggle("hidden", !a?.url);
  lock.classList.toggle("insecure", !!a?.url && !a.url.startsWith("https:"));

  const shield = $("shield");
  $("blocked").textContent = s.adblock ? (a?.blocked ?? 0) : "kapalı";
  shield.classList.toggle("active", s.adblock && (a?.blocked ?? 0) > 0);
  shield.classList.toggle("off", !s.adblock);
}

// Show "discord.com/channels/..." rather than "https://discord.com/channels/...".
function displayUrl(url) {
  if (!url) return "";
  try {
    const u = new URL(url);
    if (u.protocol === "https:") return decodeURI(url.slice("https://".length)).replace(/\/$/, "");
  } catch { /* fall through */ }
  return url;
}

$("address-form").addEventListener("submit", (e) => {
  e.preventDefault();
  editing = false;
  send("go", { text: address.value });
});
address.addEventListener("focus", () => setTimeout(() => address.select(), 0));
address.addEventListener("input", () => { editing = true; });
address.addEventListener("blur", () => { editing = false; if (state) render(state); });
address.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    editing = false;
    render(state);
    send("focusPage");
  }
});

$("back").addEventListener("click", () => send("back"));
$("forward").addEventListener("click", () => send("forward"));
$("reload").addEventListener("click", () => send($("reload").classList.contains("loading") ? "stop" : "reload"));
$("home").addEventListener("click", () => send("home"));
$("newtab").addEventListener("click", () => send("new"));
$("clear").addEventListener("click", () => send("clear"));

// Shortcuts while the toolbar itself has focus (the page's own keys go through the host).
document.addEventListener("keydown", (e) => {
  const ctrl = e.ctrlKey && !e.altKey;
  const k = e.key.toLowerCase();
  let handled = true;
  if (ctrl && k === "t") send("new");
  else if (ctrl && k === "w") send("close", { id: state?.activeId });
  else if ((ctrl && k === "l") || e.key === "F6") { address.focus(); address.select(); }
  else if (e.key === "F5" || (ctrl && k === "r")) send("reload");
  else if (ctrl && e.key === "Tab") cycle(e.shiftKey ? -1 : 1);
  else if (ctrl && e.shiftKey && e.key === "Delete") send("clear");
  else handled = false;
  if (handled) e.preventDefault();
});

function cycle(delta) {
  if (!state || state.tabs.length < 2) return;
  const i = state.tabs.findIndex((t) => t.id === state.activeId);
  send("activate", { id: state.tabs[(i + delta + state.tabs.length) % state.tabs.length].id });
}

send("ready");
