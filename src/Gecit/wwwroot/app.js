"use strict";

// ---- Bridge to the C# host ----
const host = window.chrome?.webview;
const pending = new Map();
let nextId = 1;

function call(cmd, args) {
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    host.postMessage({ id, cmd, args });
  });
}

host?.addEventListener("message", (e) => {
  const m = e.data;
  if (m.event === "state") return render(m.data);
  if (m.event === "speed") return showSpeed(m.data);
  if (m.event === "toast") return toast(m.data.message, m.data.level);
  if (m.event === "update") return showUpdateProgress(m.data.fraction);
  if (m.event === "lan") return renderLan(m.data);
  if (m.event === "boot") return bootVisible && loadBoot();
  if (m.event === "demos") return refreshDemoWatch();
  if (m.event === "free") return freeVisited && loadFree();
  const p = pending.get(m.id);
  if (!p) return;
  pending.delete(m.id);
  m.ok ? p.resolve(m.result) : p.reject(new Error(m.error));
});

const $ = (id) => document.getElementById(id);
let state = null;

// ---- Navigation ----
document.querySelectorAll(".nav-item").forEach((btn) => {
  btn.addEventListener("click", () => {
    document.querySelectorAll(".nav-item").forEach((b) => b.classList.toggle("active", b === btn));
    document.querySelectorAll(".page").forEach((p) => p.classList.toggle("active", p.id === "page-" + btn.dataset.page));
  });
});

document.addEventListener("click", (e) => {
  const a = e.target.closest("a[data-ext]");
  if (!a) return;
  e.preventDefault();
  call("app.open", { url: a.href });
});

// ---- Rendering ----
const STATUS = {
  notinstalled: ["Kapalı", "Açmak için düğmeye bas. İlk açılışta hizmet bir kez kurulur (yönetici izni ister)."],
  stopped: ["Kapalı", "Engelli siteler şu an açılmaz. Açmak için düğmeye bas."],
  starting: ["Başlatılıyor…", "WinDivert sürücüsü yükleniyor."],
  stopping: ["Durduruluyor…", ""],
  running: ["Açık", "Engelli siteler açılıyor. Pencereyi kapatsan bile arka planda çalışmaya devam eder."],
};

function render(s) {
  state = s;
  const d = s.dpi;
  const busy = d.busy || d.state === "starting" || d.state === "stopping";
  const heroState = busy ? "busy" : d.state;

  $("hero").dataset.state = heroState;
  const [title, detail] = d.busy && d.state !== "running"
    ? ["Hazırlanıyor…", "Hizmet kuruluyor veya başlatılıyor."]
    : STATUS[d.state] ?? STATUS.stopped;
  $("hero-status").textContent = title;
  $("hero-detail").textContent = detail;
  $("power").disabled = busy;

  const preset = s.presets.find((p) => p.id === s.presetId);
  $("hero-preset").textContent = preset ? "Yöntem: " + preset.name : "";
  $("hero-dns").textContent = s.dns.mode === "auto" ? "DNS: Yandex yönlendirmesi" : "DNS: Akıllı DNS";

  const pill = $("side-pill");
  pill.classList.toggle("on", d.state === "running");
  pill.classList.toggle("busy", busy);
  $("side-pill-text").textContent = busy ? "bekle" : d.state === "running" ? "açık" : "kapalı";

  const banner = $("conflict-banner");
  banner.hidden = d.conflicts.length === 0;
  banner.textContent = d.conflicts.length
    ? `Başka bir engel aşma aracı çalışıyor (${d.conflicts.join(", ")}). İkisi aynı anda çalışırsa ikisi de bozulur; önce onu kapat veya kaldır.`
    : "";

  renderPresets(s, busy);
  renderDns(s);
  renderBrowser(s.browser);
  renderGame(s.game);
  $("autostart").checked = s.autostart;
  $("uninstall").disabled = s.busy;
  $("version").textContent = "v" + s.version;
  $("lan-watch").checked = s.lanWatch;
  renderUpdate(s.update);
}

function renderPresets(s, busy) {
  const box = $("presets");
  box.replaceChildren(...s.presets.map((p, i) => {
    const el = document.createElement("button");
    el.className = "preset" + (p.id === s.presetId ? " selected" : "");
    el.disabled = busy;
    el.innerHTML = `
      <div class="preset-top">
        <span class="preset-radio"></span>
        <span class="preset-name"></span>
        ${i === 0 ? '<span class="preset-badge">önerilen</span>' : ""}
      </div>
      <div class="preset-desc"></div>
      <div class="preset-args"></div>`;
    el.querySelector(".preset-name").textContent = p.name;
    el.querySelector(".preset-desc").textContent = p.description;
    el.querySelector(".preset-args").textContent = p.args;
    el.addEventListener("click", () => {
      if (p.id === s.presetId) return;
      call("dpi.preset", { id: p.id }).then(render, (e) => toast(e.message, "error"));
    });
    return el;
  }));
}

// ---- Actions ----
$("power").addEventListener("click", async () => {
  const on = state?.dpi.state !== "running";
  try {
    render(await call("dpi.set", { on }));
    if (on && state.dpi.state === "running") toast("Engel aşma açık.", "ok");
  } catch (e) {
    toast(e.message, "error");
  }
});

$("autostart").addEventListener("change", (e) => {
  call("app.autostart", { on: e.target.checked }).then(render, (err) => toast(err.message, "error"));
});

$("uninstall").addEventListener("click", async () => {
  if (!confirm("Geçit'in hizmetleri kaldırılsın ve DNS ayarların eski haline getirilsin mi?")) return;
  try {
    render(await call("app.uninstall"));
    toast("Geçit sistemden temizlendi.", "ok");
  } catch (e) {
    toast(e.message, "error");
  }
});

// ---- Akıllı DNS ----
const MODE_NAMES = { auto: "Otomatik", plain: "Hızlı DNS", doh: "Şifreli DNS" };
let dnsSel = null;          // what the user picked on the page: { mode, provider }
let bench = null;           // id -> { pingMs, dohMs, error }
let diagnosis = null;

function providerName(id) {
  return state.dnsProviders.find((p) => p.id === id)?.name ?? id;
}

function renderDns(s) {
  const cur = s.dns;
  if (!dnsSel) dnsSel = { mode: cur.mode, provider: cur.providerId ?? "cloudflare" };

  $("dns-status").dataset.mode = cur.mode;
  $("dns-current").textContent = cur.mode === "auto"
    ? "Otomatik (modem / operatör)"
    : `${MODE_NAMES[cur.mode]} · ${providerName(cur.providerId)}`;
  $("dns-servers").textContent = cur.adapters.map((a) => `${a.name}: ${a.servers.join(", ") || "—"}`).join("   ");

  document.querySelectorAll("#dns-modes .mode").forEach((el) => {
    el.classList.toggle("selected", el.dataset.mode === dnsSel.mode);
    el.classList.toggle("current", el.dataset.mode === cur.mode);
    el.disabled = s.busy;
  });

  renderProviders(s);

  const changed = dnsSel.mode !== cur.mode || (dnsSel.mode !== "auto" && dnsSel.provider !== cur.providerId);
  $("dns-apply-bar").hidden = !changed;
  $("dns-apply").disabled = s.busy;
  $("dns-apply").textContent = s.busy ? "Uygulanıyor…" : "Uygula";
  $("dns-apply-text").textContent = dnsSel.mode === "auto"
    ? "Otomatik DNS'e dön"
    : `${MODE_NAMES[dnsSel.mode]} · ${providerName(dnsSel.provider)} kullan`;
}

function renderProviders(s) {
  const box = $("dns-providers");
  box.classList.toggle("disabled", dnsSel.mode === "auto");
  const fastest = bench
    ? Object.entries(bench).filter(([, b]) => b.dohMs != null).sort((a, b) => a[1].dohMs - b[1].dohMs)[0]?.[0]
    : null;
  box.replaceChildren(...s.dnsProviders.map((p) => {
    const b = bench?.[p.id];
    const el = document.createElement("button");
    el.className = "prov" + (p.id === dnsSel.provider ? " selected" : "");
    el.innerHTML = `
      <span class="preset-radio"></span>
      <span class="prov-name"><span class="n"></span><small></small></span>
      <span class="prov-tag"><span class="t"></span>${p.id === fastest ? '<span class="fast">en hızlı</span>' : ""}</span>
      <span class="metric ping"><small>ping</small><span class="v"></span></span>
      <span class="metric doh"><small>sorgu</small><span class="v"></span></span>`;
    el.querySelector(".n").textContent = p.name;
    el.querySelector("small").textContent = p.servers.join(" · ");
    el.querySelector(".t").textContent = p.tag;
    const fmt = (v) => (v == null ? "—" : v + " ms");
    el.querySelector(".ping .v").textContent = b ? fmt(b.pingMs) : "…";
    el.querySelector(".doh .v").textContent = b ? (b.error ? "hata" : fmt(b.dohMs)) : "…";
    if (b?.error) {
      el.querySelector(".doh").classList.add("err");
      el.title = b.error;
    }
    el.addEventListener("click", () => {
      dnsSel.provider = p.id;
      renderDns(state);
    });
    return el;
  }));
}

function renderChecks() {
  const box = $("dns-checks");
  if (!diagnosis) return;
  const row = (cls, html) => `<div class="check ${cls}"><span class="dot"></span><span>${html}</span></div>`;
  const parts = [
    diagnosis.systemDnsBlocked
      ? row("bad", "<b>DNS engeli var:</b> Discord sahte adrese yönleniyor")
      : row("ok", "<b>DNS engeli yok:</b> engelli siteler doğru adrese çözülüyor"),
  ];
  if (diagnosis.ispHijacksPort53 === true)
    parts.push(row("bad", "<b>Operatör DNS'e müdahale ediyor:</b> Hızlı DNS yetmez, Şifreli DNS kullan"));
  else if (diagnosis.ispHijacksPort53 === false)
    parts.push(row("ok", "<b>Operatör DNS'e karışmıyor:</b> Hızlı DNS de engeli aşar"));
  else
    parts.push(row("warn", "Operatör müdahalesi test edilemedi (engel aşmanın DNS yönlendirmesi açık)"));
  box.innerHTML = parts.join("");
}

async function runDiagnosis() {
  try {
    diagnosis = await call("dns.diagnose");
    renderChecks();
  } catch (e) {
    toast(e.message, "error");
  }
}

async function runBench() {
  const btn = $("dns-bench");
  btn.disabled = true;
  btn.textContent = "Ölçülüyor…";
  bench = null;
  renderDns(state);
  try {
    const results = await call("dns.bench");
    bench = Object.fromEntries(results.map((r) => [r.id, r]));
    renderDns(state);
  } catch (e) {
    toast(e.message, "error");
  } finally {
    btn.disabled = false;
    btn.textContent = "Hızları ölç";
  }
}

document.querySelectorAll("#dns-modes .mode").forEach((el) => {
  el.addEventListener("click", () => {
    dnsSel.mode = el.dataset.mode;
    renderDns(state);
  });
});

$("dns-bench").addEventListener("click", runBench);

$("dns-apply").addEventListener("click", async () => {
  try {
    render(await call("dns.apply", { mode: dnsSel.mode, provider: dnsSel.provider }));
    runDiagnosis();
  } catch (e) {
    toast(e.message, "error");
  }
});

// ---- Gizli Tarayıcı ----
function renderBrowser(b) {
  document.querySelector(".browser-hero").dataset.state = b.open ? "running" : "stopped";
  $("browser-status").textContent = b.open ? "Açık" : "Kapalı";
  $("browser-open").textContent = b.open ? "Pencereye git" : "Gizli pencere aç";
  const date = b.listDate ? new Date(b.listDate).toLocaleDateString("tr-TR") : null;
  $("browser-list").textContent = b.listCount.toLocaleString("tr-TR") + " alan adı" + (date ? `, liste ${date}` : "");
  $("browser-adblock").checked = b.adblock;
  $("browser-engines").replaceChildren(...b.engines.map((e) => {
    const chip = document.createElement("button");
    chip.className = "chip" + (e.id === b.search ? " selected" : "");
    chip.textContent = e.name;
    chip.addEventListener("click", () => call("browser.settings", { search: e.id }).then(render));
    return chip;
  }));
}

$("browser-open").addEventListener("click", () => call("browser.open"));
$("browser-adblock").addEventListener("change", (e) => call("browser.settings", { adblock: e.target.checked }).then(render));
$("browser-shortcut").addEventListener("click", () =>
  call("browser.shortcut").then(() => toast("Masaüstüne \"Gizli Tarayıcı\" kısayolu eklendi.", "ok"), (e) => toast(e.message, "error")));

// ---- Oyun Modu ----
const gb = (bytes) => (bytes / 1024 ** 3).toFixed(1);
let apps = null;   // running app candidates

function minutesSince(iso) {
  return Math.max(1, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
}

function renderGame(g) {
  const a = g.active;
  $("game-hero").dataset.state = a ? "active" : "idle";
  $("game-status").textContent = a ? (a.manual ? "Açık" : a.name) : "Beklemede";
  $("game-detail").textContent = a
    ? `${minutesSince(a.started)} dakikadır açık. Oyun kapanınca (ya da düğmeye basınca) her şey eski haline döner.`
    : g.settings.autoDetect
      ? "Bir oyun açıldığında kendiliğinden başlar. Şimdi açmak için düğmeye bas."
      : "Otomatik algılama kapalı. Açmak için düğmeye bas.";
  const meta = $("game-meta");
  meta.replaceChildren();
  const tag = (text, cls = "tag") => { const t = document.createElement("span"); t.className = cls; t.textContent = text; meta.append(t); };
  if (a) {
    if (a.frozen) tag(`${a.frozen} uygulama donduruldu`, "tag pink");
    if (a.closed) tag(`${a.closed} uygulama kapatıldı`, "tag pink");
    if (a.freedBytes > 0) tag(`${gb(a.freedBytes)} GB RAM boşaldı`);
    a.notes.forEach((n) => tag(n, "tag ghost"));
  } else {
    const n = Object.keys(g.targets).length;
    tag(n ? `${n} uygulama dondurulacak/kapatılacak` : "Dondurulacak uygulama seçilmedi", n ? "tag" : "tag ghost");
  }

  const last = $("game-last");
  last.hidden = !g.last || !!a;
  if (g.last) {
    const r = g.last;
    last.innerHTML = `<span class="ls-title">Son oturum</span>`;
    const item = (label, value) => {
      const s = document.createElement("span");
      s.className = "ls-item";
      s.append(label + " ");
      const b = document.createElement("b");
      b.textContent = value;
      s.append(b);
      last.append(s);
    };
    item("Oyun", r.name);
    item("Süre", r.minutes + " dk");
    if (r.freedBytes > 0) item("Boşalan RAM", gb(r.freedBytes) + " GB");
    if (r.peakGpuTemp > 0) item("En yüksek GPU", r.peakGpuTemp + " °C");
    item("Durdurulan", String(r.frozen + r.closed));
  }

  document.querySelectorAll("[data-game-setting]").forEach((el) => { el.checked = g.settings[el.dataset.gameSetting]; });
  $("game-known").textContent = `Hazır listede ${g.knownCount} popüler oyun var (CS2, VALORANT, LoL, Roblox, Metin2…). Steam ve Epic kütüphanen de otomatik taranır.`;

  $("game-list").replaceChildren(...(g.games.length ? g.games.map((x) => {
    const chip = document.createElement("span");
    chip.className = "game-chip";
    chip.textContent = x.name + " ";
    const src = document.createElement("small");
    src.textContent = { steam: "Steam", epic: "Epic", custom: "eklendi" }[x.source] ?? x.source;
    chip.append(src);
    if (x.source === "custom") {
      const del = document.createElement("button");
      del.title = "Listeden çıkar";
      del.textContent = "×";
      del.addEventListener("click", () => call("game.removeGame", { exe: x.exes[0] }).then(render));
      chip.append(del);
    }
    return chip;
  }) : [Object.assign(document.createElement("div"), { className: "empty", textContent: "Steam/Epic'te kurulu oyun bulunamadı. Hazır listedeki oyunlar yine de algılanır." })]));

  if (apps) renderApps(g.targets);
}

function renderApps(targets) {
  const box = $("game-apps");
  // Targets that aren't running right now still get a row so they can be removed.
  const rows = [...apps];
  for (const name of Object.keys(targets))
    if (!rows.some((r) => r.process.toLowerCase() === name.toLowerCase())) rows.push({ process: name, name, memoryMb: null, count: 0 });
  if (!rows.length) {
    box.innerHTML = '<div class="empty">Dondurulabilecek açık uygulama yok.</div>';
    return;
  }
  box.replaceChildren(...rows.map((r) => {
    const current = Object.entries(targets).find(([k]) => k.toLowerCase() === r.process.toLowerCase())?.[1] ?? "none";
    const row = document.createElement("div");
    row.className = "app-row";
    row.innerHTML = `<div class="app-name"><span class="n"></span><small></small></div><div class="app-mem"></div>
      <div class="seg"><button data-a="none">Dokunma</button><button data-a="suspend">Dondur</button><button data-a="close" class="close">Kapat</button></div>`;
    row.querySelector(".n").textContent = r.name;
    row.querySelector("small").textContent = r.process + (r.count > 1 ? ` · ${r.count} işlem` : "") + (r.count === 0 ? " · şu an kapalı" : "");
    row.querySelector(".app-mem").textContent = r.memoryMb == null ? "" : r.memoryMb >= 1024 ? (r.memoryMb / 1024).toFixed(1) + " GB" : r.memoryMb + " MB";
    row.querySelectorAll(".seg button").forEach((b) => {
      b.classList.toggle("on", b.dataset.a === current);
      b.addEventListener("click", () => call("game.target", { process: r.process, action: b.dataset.a }).then(render, (e) => toast(e.message, "error")));
    });
    return row;
  }));
}

async function loadApps() {
  try {
    apps = await call("game.apps");
    renderApps(state.game.targets);
  } catch (e) {
    toast(e.message, "error");
  }
}

function setBar(id, percent, hot = false) {
  const el = $(id);
  el.style.width = Math.min(100, Math.max(0, percent)) + "%";
  el.classList.toggle("hot", hot);
}

async function refreshStats() {
  try {
    const s = await call("game.stats");
    const usedPct = 100 * (1 - s.ramAvailable / s.ramTotal);
    $("st-ram").innerHTML = `${gb(s.ramAvailable)} <small>/ ${gb(s.ramTotal)} GB</small>`;
    setBar("st-ram-bar", usedPct, usedPct > 85);
    $("st-cpu").textContent = s.cpu + "%";
    setBar("st-cpu-bar", s.cpu, s.cpu > 90);
    if (s.gpu) {
      $("st-gpu").innerHTML = `${s.gpu.utilPercent}% <small>${(s.gpu.memUsedMb / 1024).toFixed(1)}/${(s.gpu.memTotalMb / 1024).toFixed(0)} GB</small>`;
      setBar("st-gpu-bar", s.gpu.utilPercent);
      $("st-temp").textContent = s.gpu.tempC + " °C";
      setBar("st-temp-bar", (s.gpu.tempC - 25) * 100 / 65, s.gpu.tempC >= 80);
    } else {
      $("st-gpu").textContent = "—";
      $("st-temp").textContent = "—";
    }
  } catch { /* the page may be switching; next tick retries */ }
}

let statsTimer = null;
function onGamePage(visible) {
  clearInterval(statsTimer);
  statsTimer = null;
  if (!visible) return;
  refreshStats();
  statsTimer = setInterval(refreshStats, 2000);
  if (!apps) loadApps();
}

document.querySelectorAll(".nav-item").forEach((btn) =>
  btn.addEventListener("click", () => {
    onGamePage(btn.dataset.page === "game");
    onNetPage(btn.dataset.page === "net");
    onLanPage(btn.dataset.page === "lan");
    onBootPage(btn.dataset.page === "boot");
    onDemosPage(btn.dataset.page === "demos");
    onFreePage(btn.dataset.page === "free");
  }));

$("game-power").addEventListener("click", () =>
  call("game.toggle", { on: !state.game.active }).then(render, (e) => toast(e.message, "error")));
$("game-apps-refresh").addEventListener("click", loadApps);
document.querySelectorAll("[data-game-setting]").forEach((el) =>
  el.addEventListener("change", () => call("game.settings", { [el.dataset.gameSetting]: el.checked }).then(render)));
$("add-game").addEventListener("submit", (e) => {
  e.preventDefault();
  const exe = $("add-game-exe").value.trim();
  if (!exe) return;
  call("game.addGame", { exe }).then((s) => { $("add-game-exe").value = ""; render(s); toast(exe + " oyun listesine eklendi.", "ok"); });
});

// ---- İnternet ----
let netTimer = null;
let trafficTimer = null;
let netTarget = "1.1.1.1";

function fmtRate(bytesPerSec) {
  const kbit = bytesPerSec * 8 / 1000;
  return kbit >= 1000 ? (kbit / 1000).toFixed(1) + " Mbps" : Math.round(kbit) + " kbps";
}
function fmtBytes(b) {
  return b >= 1024 ** 3 ? (b / 1024 ** 3).toFixed(2) + " GB" : b >= 1024 ** 2 ? (b / 1024 ** 2).toFixed(1) + " MB" : Math.round(b / 1024) + " KB";
}

function verdict(inet, gw) {
  if (gw && (gw.lossPercent > 5 || (gw.avgMs ?? 0) > 20))
    return ["bad", "Sorun evde: modem ya da Wi-Fi bağlantısı zayıf. Kabloyla bağlanmayı veya modeme yaklaşmayı dene."];
  if (inet.lossPercent > 2)
    return ["bad", `%${inet.lossPercent.toFixed(0)} paket kaybı var. Modem iyi, sorun operatör hattında olabilir.`];
  if ((inet.jitterMs ?? 0) > 15 || (inet.avgMs ?? 0) > 120)
    return ["warn", "Bağlantı dalgalı. Oyunlarda ara ara gecikme hissedebilirsin."];
  if ((inet.avgMs ?? 99) < 40 && (inet.jitterMs ?? 9) < 5)
    return ["ok", "Bağlantı oyun için çok iyi."];
  return ["ok", "Bağlantı oyun için iyi."];
}

function drawGraph(inet, gw) {
  const svg = $("net-graph");
  const W = 600, H = 150, N = 120;
  const values = [...inet.history, ...(gw?.history ?? [])].filter((v) => v != null);
  const top = Math.max(50, Math.ceil(Math.max(...values, 0) * 1.25 / 25) * 25);
  const y = (v) => H - (v / top) * (H - 14) - 2;
  const x = (i, len) => W - (len - 1 - i) * (W / (N - 1));
  const path = (hist) => {
    let d = "", pen = false;
    hist.forEach((v, i) => {
      if (v == null) { pen = false; return; }
      d += (pen ? "L" : "M") + x(i, hist.length).toFixed(1) + "," + y(v).toFixed(1);
      pen = true;
    });
    return d;
  };
  let html = "";
  for (const f of [0.25, 0.5, 0.75, 1]) {
    const yy = y(top * f);
    html += `<line class="grid" x1="0" x2="${W}" y1="${yy}" y2="${yy}"/><text class="gl" x="4" y="${yy + 11}">${Math.round(top * f)} ms</text>`;
  }
  if (gw) html += `<path class="line pink" d="${path(gw.history)}"/>`;
  html += `<path class="line cyan" d="${path(inet.history)}"/>`;
  inet.history.forEach((v, i) => { if (v == null) html += `<rect class="loss" x="${x(i, inet.history.length) - 2}" y="${H - 6}" width="4" height="6"/>`; });
  svg.innerHTML = html;
}

async function pollPing() {
  try {
    const { internet: inet, gateway: gw } = await call("net.ping");
    const ms = (v) => (v == null ? "—" : v);
    $("net-target-label").textContent = inet.target;
    $("net-ms").innerHTML = `${ms(inet.lastMs)} <small>ms</small>`;
    $("net-sub").textContent = inet.avgMs == null ? "yanıt yok" :
      `ort ${Math.round(inet.avgMs)} · dalgalanma ${inet.jitterMs?.toFixed(1) ?? "—"} · kayıp %${inet.lossPercent.toFixed(0)}`;
    $("net-gw-label").textContent = gw?.target ?? "";
    $("gw-ms").innerHTML = gw ? `${ms(gw.lastMs)} <small>ms</small>` : "—";
    $("gw-sub").textContent = gw ? `kayıp %${gw.lossPercent.toFixed(0)}` : "modem bulunamadı";
    if (inet.history.length >= 3) {
      const [cls, text] = verdict(inet, gw);
      const v = $("net-verdict");
      v.className = "net-verdict " + cls;
      v.lastElementChild.textContent = text;
    }
    drawGraph(inet, gw);
    document.querySelectorAll("#net-targets .chip").forEach((c) => c.classList.toggle("selected", c.dataset.target === inet.target));
  } catch { /* next tick */ }
}

async function pollTraffic() {
  try {
    const t = await call("net.traffic");
    $("tr-toggle").textContent = t.running ? "İzlemeyi durdur" : "İzlemeyi başlat";
    if (!t.running && !t.apps.length) return;
    const head = '<div class="tr-row head"><span>Uygulama</span><span class="num">İndirme</span><span class="num">Yükleme</span><span class="num">Toplam</span></div>';
    const list = $("tr-list");
    list.innerHTML = head;
    if (!t.apps.length) {
      list.insertAdjacentHTML("beforeend", '<div class="empty">Henüz trafik yok…</div>');
      return;
    }
    for (const a of t.apps) {
      const row = document.createElement("div");
      row.className = "tr-row";
      row.innerHTML = `<span class="tr-name"><span class="n"></span><small></small></span>
        <span class="num rx"></span><span class="num tx"></span><span class="num tot"></span>`;
      row.querySelector(".n").textContent = a.name;
      row.querySelector("small").textContent = a.process;
      row.querySelector(".rx").textContent = a.rxRate ? fmtRate(a.rxRate) : "—";
      row.querySelector(".rx").classList.toggle("active", a.rxRate > 0);
      row.querySelector(".tx").textContent = a.txRate ? fmtRate(a.txRate) : "—";
      row.querySelector(".tx").classList.toggle("active", a.txRate > 0);
      row.querySelector(".tot").textContent = fmtBytes(a.rxTotal + a.txTotal);
      list.append(row);
    }
  } catch { /* next tick */ }
}

function onNetPage(visible) {
  clearInterval(netTimer);
  clearInterval(trafficTimer);
  netTimer = trafficTimer = null;
  if (!visible) {
    // The elevated monitor only runs while the page is open.
    call("net.traffic.stop").catch(() => {});
    $("tr-toggle").textContent = "İzlemeyi başlat";
    return;
  }
  pollPing();
  netTimer = setInterval(pollPing, 1000);
}

$("tr-toggle").addEventListener("click", async () => {
  const btn = $("tr-toggle");
  btn.disabled = true;
  try {
    const t = await call("net.traffic");
    if (t.running) {
      await call("net.traffic.stop");
      clearInterval(trafficTimer);
      btn.textContent = "İzlemeyi başlat";
    } else {
      btn.textContent = "İzin bekleniyor…";
      await call("net.traffic.start");
      btn.textContent = "İzlemeyi durdur";
      pollTraffic();
      trafficTimer = setInterval(pollTraffic, 1000);
    }
  } catch (e) {
    toast(e.message, "error");
    btn.textContent = "İzlemeyi başlat";
  } finally {
    btn.disabled = false;
  }
});

document.querySelectorAll("#net-targets .chip").forEach((c) =>
  c.addEventListener("click", () => call("net.target", { target: c.dataset.target }).then(pollPing)));
$("net-custom").addEventListener("submit", (e) => {
  e.preventDefault();
  const host = $("net-custom-host").value.trim();
  if (host) call("net.target", { target: host }).then(pollPing);
});

// Speed test progress arrives as "speed" events.
const PHASES = { ping: "Boşta ping ölçülüyor…", download: "İndirme hızı ölçülüyor…", upload: "Yükleme hızı ölçülüyor…", done: "Bitti." };
function showSpeed(p) {
  const f = (v) => (v == null ? "–" : v >= 100 ? Math.round(v) : v.toFixed(1));
  $("sp-down").textContent = f(p.downMbps);
  $("sp-up").textContent = f(p.upMbps);
  $("sp-idle").textContent = p.idleMs ?? "–";
  $("sp-loaded").textContent = p.loadedMs ?? "–";
  $("sp-phase").textContent = PHASES[p.phase] ?? "";
  $("sp-progress").style.width = Math.round(p.fraction * 100) + "%";
}

$("sp-start").addEventListener("click", async () => {
  const btn = $("sp-start");
  if (btn.dataset.running) {
    call("net.speed.stop");
    return;
  }
  btn.dataset.running = "1";
  btn.textContent = "Durdur";
  $("sp-grade").textContent = "–";
  $("sp-grade").className = "grade";
  showSpeed({ phase: "ping", fraction: 0 });
  try {
    const r = await call("net.speed");
    if (r) {
      showSpeed({ phase: "done", downMbps: r.downMbps, upMbps: r.upMbps, idleMs: r.idleMs, loadedMs: Math.max(r.downLoadedMs, r.upLoadedMs), fraction: 1 });
      const g = $("sp-grade");
      g.textContent = r.grade;
      g.className = "grade g-" + r.grade[0].toLowerCase();
      $("sp-grade-sub").textContent = `yükte +${Math.max(r.downLoadedMs, r.upLoadedMs) - r.idleMs} ms`;
      $("sp-phase").textContent = {
        A: "Hat dolsa bile ping neredeyse artmıyor: oyun oynarken indirme yapabilirsin.",
        B: "Hat dolunca ping biraz artıyor; büyük indirmeler sırasında oyunda hafif gecikme olabilir.",
        C: "Hat dolunca ping belirgin artıyor. Oyun sırasında indirme/yükleme yapma.",
        D: "Hat dolunca ping çok artıyor. Modemde QoS / SQM açmak çok fayda eder.",
        F: "Hat dolunca bağlantı neredeyse kilitleniyor. Modemde QoS / SQM ayarı şart.",
      }[r.grade[0]];
    } else {
      $("sp-phase").textContent = "Test durduruldu.";
    }
  } catch (e) {
    toast(e.message, "error");
    $("sp-phase").textContent = "Test tamamlanamadı.";
  } finally {
    delete btn.dataset.running;
    btn.textContent = "Testi başlat";
  }
});

// Measure once, the first time the DNS page is opened.
let dnsVisited = false;
document.querySelector('[data-page="dns"]').addEventListener("click", () => {
  if (dnsVisited) return;
  dnsVisited = true;
  runDiagnosis();
  runBench();
});

$("probe-run").addEventListener("click", async () => {
  const btn = $("probe-run");
  btn.disabled = true;
  $("probe-list").innerHTML = '<div class="probe-row wait"><span class="dot"></span><span class="probe-name">Test ediliyor…</span><span></span><span></span></div>';
  try {
    const results = await call("probe.run");
    $("probe-list").replaceChildren(...results.map(probeRow));
    $("probe-time").textContent = "Son test: " + new Date().toLocaleTimeString("tr-TR");
  } catch (e) {
    toast(e.message, "error");
  } finally {
    btn.disabled = false;
  }
});

function probeRow(r) {
  const row = document.createElement("div");
  row.className = "probe-row " + (r.ok ? "ok" : "fail");
  row.innerHTML = `<span class="dot"></span>
    <span class="probe-name"><span class="n"></span><span class="probe-host"></span></span>
    <span class="probe-detail"></span>
    <span class="probe-ms"></span>`;
  row.querySelector(".n").textContent = r.name;
  row.querySelector(".probe-host").textContent = r.host;
  row.querySelector(".probe-detail").textContent = r.ok ? "Açılıyor · " + r.detail : r.detail;
  row.querySelector(".probe-ms").textContent = r.ms != null ? r.ms + " ms" : "—";
  return row;
}

// ---- Güncelleme ----
function renderUpdate(u) {
  const a = u.available;
  $("update-bar").hidden = !a || (u.dismissed && !u.installing);
  if (a) {
    $("update-title").textContent = u.installing ? `Geçit ${a.version} indiriliyor…` : `Geçit ${a.version} çıktı`;
    if (!u.installing)
      $("update-sub").textContent = `Şu an ${u.current} kullanıyorsun · ${fmtBytes(a.size)} · ${new Date(a.published).toLocaleDateString("tr-TR")}`;
    $("update-install").disabled = u.installing;
    $("update-install").textContent = u.installing ? "Bekle…" : "Güncelle";
    $("update-later").hidden = u.installing;
    $("update-progress-bar").hidden = !u.installing;
  }
  $("update-current").textContent = "v" + u.current;
  $("update-auto").checked = u.auto;
  $("update-check").disabled = u.checking || u.installing;
  $("update-check").textContent = u.checking ? "Denetleniyor…" : "Güncellemeleri denetle";
  const at = u.lastCheck ? new Date(u.lastCheck).toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" }) : null;
  $("update-status").textContent = u.error
    ?? (a ? `Yeni sürüm var: ${a.version}.` : at ? `En son sürümü kullanıyorsun (denetlendi: ${at}).` : "Güncellemeler GitHub'daki sürüm sayfasından gelir.");
}

function showUpdateProgress(f) {
  const pct = Math.round(f * 100);
  $("update-progress").style.width = pct + "%";
  $("update-sub").textContent = pct < 100
    ? `%${pct} indirildi. İndirme bitince kurulum başlar; Geçit kapanıp yeni sürümle açılır.`
    : "Doğrulanıyor, kurulum başlıyor… Yönetici izni isteyecek.";
}

$("update-install").addEventListener("click", () => call("update.install").then(render, (e) => toast(e.message, "error")));
$("update-later").addEventListener("click", () => call("update.dismiss").then(render));
$("update-notes").addEventListener("click", () => state.update.available && call("app.open", { url: state.update.available.url }));
$("update-auto").addEventListener("change", (e) => call("update.auto", { on: e.target.checked }).then(render));
$("update-check").addEventListener("click", async () => {
  try {
    const s = await call("update.check");
    render(s);
    if (!s.update.available && !s.update.error) toast("En son sürümü kullanıyorsun.", "ok");
  } catch (e) {
    toast(e.message, "error");
  }
});

// ---- Ağım ----
const KIND_ICONS = {
  router: '<svg viewBox="0 0 24 24"><rect x="3" y="14" width="18" height="6" rx="2"/><path d="M7 17h.01M11 17h.01"/><path d="M8.5 10.5a5 5 0 0 1 7 0M5.8 7.8a9 9 0 0 1 12.4 0"/></svg>',
  pc: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg>',
  phone: '<svg viewBox="0 0 24 24"><rect x="7" y="2.5" width="10" height="19" rx="2.5"/><path d="M11 18.5h2"/></svg>',
  tv: '<svg viewBox="0 0 24 24"><rect x="2.5" y="4" width="19" height="13" rx="2"/><path d="M8 21h8M12 17v4"/></svg>',
  console: '<svg viewBox="0 0 24 24"><path d="M6 8h12a4 4 0 0 1 4 4v1a4 4 0 0 1-7 2.6L14 15h-4l-1 .6A4 4 0 0 1 2 13v-1a4 4 0 0 1 4-4z"/><path d="M7 11v3M5.5 12.5h3"/></svg>',
  printer: '<svg viewBox="0 0 24 24"><path d="M7 9V3h10v6"/><rect x="3" y="9" width="18" height="8" rx="2"/><path d="M7 14h10v7H7z"/></svg>',
  iot: '<svg viewBox="0 0 24 24"><path d="M9 18h6M10 21h4"/><path d="M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2.1h5c0-.9.4-1.6 1-2.1A6 6 0 0 0 12 3z"/></svg>',
  unknown: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6v.6M12 17h.01"/></svg>',
};
const KIND_NAMES = { router: "Modem / ağ cihazı", pc: "Bilgisayar", phone: "Telefon / tablet", tv: "Televizyon", console: "Oyun konsolu", printer: "Yazıcı", iot: "Akıllı ev cihazı" };
let lanVisited = false;

function ago(iso) {
  const min = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (min < 2) return "az önce";
  if (min < 60) return min + " dk önce";
  if (min < 48 * 60) return Math.round(min / 60) + " sa önce";
  return Math.round(min / 1440) + " gün önce";
}

function renderLan(r) {
  if (!r) return;
  $("lan-hero").dataset.state = "idle";
  $("lan-count").textContent = r.online.length;
  $("lan-net-label").textContent = r.kind === "wifi" ? "Wi-Fi ağı" : "Kablolu ağ";
  $("lan-net").textContent = r.ssid ?? r.adapter;
  const gw = r.online.find((d) => d.isGateway);
  const modem = gw ? (gw.model ?? gw.vendor) : null;
  $("lan-detail").textContent = `${r.cidr} · modem ${r.gateway}${modem ? " (" + modem + ")" : ""} · son tarama ${new Date(r.at).toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" })}`;
  $("lan-online").replaceChildren(...r.online.map((d) => devRow(d, false)));
  $("lan-offline-title").hidden = $("lan-offline").hidden = !r.offline.length;
  $("lan-offline").replaceChildren(...r.offline.map((d) => devRow(d, true)));
}

function devRow(d, offline) {
  const row = document.createElement("div");
  row.className = "dev-row" + (offline ? " offline" : "") + (d.isNew ? " new" : "");
  row.dataset.kind = d.kind;
  row.innerHTML = `<div class="dev-icon">${KIND_ICONS[d.kind] ?? KIND_ICONS.unknown}</div>
    <div class="dev-main"><div class="dev-name"><span class="n"></span></div><div class="dev-sub"></div></div>
    <div class="dev-addr"><span class="ip"></span><small class="mac"></small></div>
    <div class="dev-ping"></div>`;
  const nm = row.querySelector(".n");
  nm.textContent = d.name;
  nm.title = "Ad vermek için tıkla";
  const names = row.querySelector(".dev-name");
  const badge = (text, cls) => {
    const b = document.createElement("span");
    b.className = "badge " + cls;
    b.textContent = text;
    names.append(b);
  };
  if (d.isSelf) badge("bu bilgisayar", "me");
  if (d.isGateway) badge("modem", "gw");
  if (d.isNew) badge("yeni", "new");
  if (d.randomMac) badge("gizli MAC", "");

  const sub = [KIND_NAMES[d.kind], d.model ?? d.vendor, offline ? "en son " + ago(d.lastSeen) : d.isNew ? "ilk görülme " + ago(d.firstSeen) : null];
  row.querySelector(".dev-sub").textContent = sub.filter(Boolean).join(" · ") || "Ne olduğu anlaşılamadı";
  row.querySelector(".ip").textContent = d.ip;
  row.querySelector(".mac").textContent = d.mac;

  const ping = row.querySelector(".dev-ping");
  if (offline) {
    const forget = document.createElement("button");
    forget.className = "forget";
    forget.textContent = "unut";
    forget.title = "Listeden çıkar; tekrar bağlanırsa yeni cihaz olarak görünür";
    forget.addEventListener("click", () => call("lan.forget", { mac: d.mac }).then(renderLan));
    ping.append(forget);
  } else if (!d.isSelf) {
    ping.textContent = d.pingMs != null ? d.pingMs + " ms" : "—";
    if (d.pingMs == null) ping.title = "Ping'e yanıt vermiyor (birçok telefon ve TV böyledir)";
  }

  nm.addEventListener("click", () => {
    const input = document.createElement("input");
    input.value = d.customName ?? "";
    input.placeholder = d.name;
    input.maxLength = 40;
    input.spellcheck = false;
    nm.replaceWith(input);
    input.focus();
    let done = false;
    const finish = (save) => {
      if (done) return;
      done = true;
      if (save) call("lan.rename", { mac: d.mac, name: input.value }).then(renderLan, (e) => toast(e.message, "error"));
      else input.replaceWith(nm);
    };
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") finish(true);
      if (e.key === "Escape") finish(false);
    });
    input.addEventListener("blur", () => finish(true));
  });
  return row;
}

async function scanLan() {
  const btn = $("lan-scan");
  btn.disabled = true;
  btn.textContent = "Taranıyor…";
  $("lan-hero").dataset.state = "scanning";
  $("lan-detail").textContent = "Ağdaki her adres yoklanıyor, cihazların adları soruluyor…";
  try {
    renderLan(await call("lan.scan"));
  } catch (e) {
    toast(e.message, "error");
    $("lan-hero").dataset.state = "idle";
  } finally {
    btn.disabled = false;
    btn.textContent = "Yeniden tara";
  }
}

function onLanPage(visible) {
  if (!visible || lanVisited) return;
  lanVisited = true;
  call("lan.last").then((r) => (r ? renderLan(r) : scanLan()));
}

$("lan-scan").addEventListener("click", scanLan);
$("lan-watch").addEventListener("change", (e) => call("lan.watch", { on: e.target.checked }).then(render));

// ---- Açılış ----
const IMPACT = { high: "Yüksek", medium: "Orta", low: "Düşük", none: "Çalışmadı", unknown: "Ölçülmedi", off: "Kapalı" };
const IMPACT_ORDER = { high: 0, medium: 1, low: 2, none: 3, unknown: 4 };
const SOURCES = {
  UserRun: "kayıt defteri", MachineRun: "kayıt defteri · tüm kullanıcılar", MachineRun32: "kayıt defteri · tüm kullanıcılar",
  UserFolder: "Başlangıç klasörü", CommonFolder: "Başlangıç klasörü · tüm kullanıcılar",
};
let bootVisible = false;

function onBootPage(visible) {
  bootVisible = visible;
  if (visible) loadBoot();
}

async function loadBoot() {
  try {
    renderBoot(await call("boot.list"));
  } catch (e) {
    toast(e.message, "error");
  }
}

const sec = (s) => (s >= 100 ? Math.round(s) : s.toFixed(1));

function renderBoot(b) {
  const h = b.history;
  const last = h[h.length - 1];
  const prev = h[h.length - 2];
  const t = $("boot-time");
  const sub = $("boot-time-sub");
  if (!last) {
    t.className = "boot-time none";
    t.textContent = "?";
    sub.textContent = "Henüz ölçülmedi.";
  } else {
    if (last.readySeconds == null) {
      t.className = "boot-time slow";
      t.innerHTML = "3 dk<small>+</small>";
      sub.textContent = "Masaüstünden sonra 3 dakika boyunca meşguldü";
    } else {
      t.className = "boot-time" + (last.readySeconds > 60 ? " slow" : "");
      t.innerHTML = `${sec(last.readySeconds)}<small> sn</small>`;
      sub.textContent = "Masaüstü göründükten sonra kullanıma hazır";
    }
    sub.append(" · " + new Date(last.desktopAt).toLocaleString("tr-TR", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" }));
    if (prev?.readySeconds != null && last.readySeconds != null && Math.abs(last.readySeconds - prev.readySeconds) >= 2) {
      const d = last.readySeconds - prev.readySeconds;
      const span = document.createElement("div");
      span.className = d < 0 ? "delta-good" : "delta-bad";
      span.textContent = `Önceki açılıştan ${sec(Math.abs(d))} sn ${d < 0 ? "hızlı" : "yavaş"}`;
      sub.append(span);
    }
  }
  drawBootChart(h);

  const cta = $("boot-cta");
  if (b.measuring) cta.innerHTML = '<span class="ok">● Bu açılış şu an ölçülüyor…</span>';
  else if (b.autostart) cta.textContent = "Geçit Windows'la açıldığı için her açılışı kendiliğinden ölçer.";
  else if (b.scheduled) cta.innerHTML = '<span class="ok">✓ Bir sonraki açılışta ölçülecek</span><button class="link-btn" data-schedule="0">iptal</button>';
  else cta.innerHTML = `<span>${last ? "Değişikliklerin etkisini görmek için:" : "Bilgisayarı yeniden başlatınca ölçülür."}</span><button class="btn" data-schedule="1">Bir sonraki açılışta ölç</button>`;

  const on = b.items.filter((i) => i.enabled);
  const heavy = on.filter((i) => i.impact === "high").length;
  const facts = $("boot-facts");
  facts.replaceChildren();
  const fact = (text, cls = "tag") => { const s = document.createElement("span"); s.className = cls; s.textContent = text; facts.append(s); };
  fact(`${on.length} program açılışta başlıyor`);
  if (heavy) fact(`${heavy} tanesi açılışı ciddi yavaşlatıyor`, "tag pink");
  if (b.items.length > on.length) fact(`${b.items.length - on.length} program kapatıldı`, "tag ghost");
  if (b.bios != null) fact(`BIOS: ${sec(b.bios)} sn${b.bios > 8 ? " · BIOS'ta \"Fast Boot\" açmak birkaç saniye kazandırabilir" : ""}`, "tag ghost");

  // The same program started from two places (e.g. "for me" and "for everyone"): one is enough.
  const seen = new Map();
  for (const i of b.items.filter((x) => x.enabled)) {
    const key = i.command.toLowerCase();
    seen.set(key, (seen.get(key) ?? 0) + 1);
  }
  b.items.forEach((i) => { i.duplicate = i.enabled && seen.get(i.command.toLowerCase()) > 1; });

  const items = [...b.items].sort((x, y) => (y.enabled - x.enabled)
    || (IMPACT_ORDER[x.impact ?? "unknown"] - IMPACT_ORDER[y.impact ?? "unknown"])
    || x.name.localeCompare(y.name, "tr"));
  $("boot-items").replaceChildren(...(items.length ? items.map(bootRow)
    : [Object.assign(document.createElement("div"), { className: "empty", textContent: "Açılışta başlayan program yok." })]));
}

function drawBootChart(h) {
  const svg = $("boot-chart");
  const W = 300, H = 80, N = 12;
  const data = h.slice(-N);
  if (!data.length) {
    svg.innerHTML = "";
    return;
  }
  const val = (r) => r.readySeconds ?? 180;
  const top = Math.max(60, ...data.map(val)) * 1.15;
  const bw = W / N - 6;
  svg.innerHTML = data.map((r, i) => {
    const bh = Math.max(3, (val(r) / top) * (H - 12));
    const x = W - (data.length - i) * (W / N) + 3;
    const cls = i === data.length - 1 ? "last" : r.readySeconds == null ? "capped" : "";
    const label = r.readySeconds == null ? "3 dk+" : sec(r.readySeconds) + " sn";
    const when = new Date(r.desktopAt).toLocaleString("tr-TR", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
    return `<rect class="${cls}" x="${x}" y="${H - bh}" width="${bw}" height="${bh}" rx="2"><title>${when}: ${label}</title></rect>`;
  }).join("") + `<text x="0" y="9">son ${data.length} açılış</text>`;
}

function bootRow(i) {
  const row = document.createElement("div");
  row.className = "boot-row" + (i.enabled ? "" : " off");
  row.innerHTML = `${i.icon ? '<img class="boot-icon" alt="">' : '<div class="boot-icon"></div>'}
    <div class="app-name"><span class="n"></span><small></small><div class="boot-hint" hidden></div></div>
    <div class="impact"></div>
    <input type="checkbox" class="switch">`;
  if (i.icon) row.querySelector("img").src = i.icon;
  const name = row.querySelector(".app-name");
  name.querySelector(".n").textContent = i.name;
  const badge = (text, cls) => {
    const b = document.createElement("span");
    b.className = "badge " + cls;
    b.textContent = text;
    name.insertBefore(b, name.querySelector("small"));
  };
  if (i.suggested && i.enabled) badge("kapatılabilir", "good");
  if (i.duplicate) badge("iki kez kayıtlı", "new");
  if (i.isWindows) badge("Windows", "");
  if (i.missing) badge("dosyası yok", "new");
  const small = name.querySelector("small");
  small.textContent = [i.publisher, SOURCES[i.source]].filter(Boolean).join(" · ");
  small.title = i.command;
  const hintText = i.hint ?? (i.duplicate ? "Aynı program iki ayrı yerden başlatılıyor; birini kapatman yeter." : null);
  if (hintText) {
    const hint = name.querySelector(".boot-hint");
    hint.hidden = false;
    hint.textContent = hintText;
  }

  const imp = row.querySelector(".impact");
  const key = !i.enabled ? "off" : i.impact ?? "unknown";
  imp.className = "impact " + (key === "off" ? "none" : key);
  imp.textContent = IMPACT[key];
  if (i.enabled && i.cpuMs != null && i.impact !== "none") {
    const s = document.createElement("small");
    s.textContent = `${(i.cpuMs / 1000).toFixed(1)} sn işlemci · ${fmtBytes(i.ioBytes)}`;
    imp.append(s);
  } else if (i.impact === "none") {
    imp.title = "Son açılışta çalışırken görülmedi (açılıp hemen kapanan programlar böyledir)";
  }

  const sw = row.querySelector(".switch");
  sw.checked = i.enabled;
  sw.title = i.needsAdmin ? "Tüm kullanıcılar için; yönetici izni ister" : "";
  sw.addEventListener("change", async () => {
    sw.disabled = true;
    try {
      renderBoot(await call("boot.set", { id: i.id, on: sw.checked }));
      toast(sw.checked ? `${i.name} açılışta yine başlayacak.` : `${i.name} artık açılışta başlamayacak.`, "ok");
    } catch (e) {
      sw.checked = !sw.checked;
      sw.disabled = false;
      toast(e.message, "error");
    }
  });
  return row;
}

$("boot-refresh").addEventListener("click", loadBoot);
$("boot-cta").addEventListener("click", (e) => {
  const b = e.target.closest("[data-schedule]");
  if (b) call("boot.schedule", { on: b.dataset.schedule === "1" }).then(renderBoot, (err) => toast(err.message, "error"));
});

// ---- Demolar ----
const demoLists = {};          // "list|players" -> { items, total, error, loading, loadingMore }
let demoList = "new";
let demoPlayers = "all";       // all | coop | online | multi; filtered by Steam itself, remembered by the host
const demoKey = () => demoList + "|" + demoPlayers;
// The same filter for the watch list, which is filtered here.
const PLAYER_MATCH = {
  coop: (p) => p.some((x) => x.includes("co-op")),
  online: (p) => p.includes("Online co-op"),
  multi: (p) => p.length > 0,
};
let demoWatch = { steam: false, watch: [] };
let demoQuery = "";
const demoFilters = new Set();  // "tr", "deck" and tag names
let demosVisited = false;

const DAY = 86400000;
function dayDiff(date) {
  const d = new Date(date), t = new Date();
  return Math.round((new Date(d.getFullYear(), d.getMonth(), d.getDate()) - new Date(t.getFullYear(), t.getMonth(), t.getDate())) / DAY);
}

function demoWhen(d) {
  if (!d.date) return d.dateText ?? "";
  const t = new Date(d.date), diff = dayDiff(t);
  const hm = t.toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" });
  const day = t.toLocaleDateString("tr-TR", { day: "numeric", month: "short", year: t.getFullYear() === new Date().getFullYear() ? undefined : "numeric" });
  if (d.comingSoon) return diff <= 0 ? "bugün " + hm : diff === 1 ? "yarın " + hm : day + " " + hm;
  return diff === 0 ? "bugün" : diff === -1 ? "dün" : diff > -7 ? -diff + " gün önce" : day;
}

function demoGroup(d) {
  if (!d.date) return d.comingSoon ? "Tarihi kesinleşmemiş" : "Daha önce";
  const diff = dayDiff(d.date);
  if (diff === 0) return "Bugün";
  if (diff === 1) return "Yarın";
  if (diff === -1) return "Dün";
  if (Math.abs(diff) < 7) return diff > 0 ? "Bu hafta" : "Son bir hafta";
  if (diff < 0) return "Daha önce";
  if (diff < 31) return "Önümüzdeki haftalar";
  return new Date(d.date).toLocaleDateString("tr-TR", { month: "long", year: "numeric" });
}

function onDemosPage(visible) {
  if (!visible || demosVisited) return;
  demosVisited = true;
  call("demos.watched").then((w) => { demoWatch = w; demoPlayers = w.players; }).finally(() => loadDemos());
}

async function loadDemos({ more = false, refresh = false } = {}) {
  if (demoList === "watch") return refreshDemoWatch();
  // Each tab loads on its own; switching tabs mid-load must not leave the other one empty.
  const list = demoList, players = demoPlayers, key = demoKey();
  const cur = demoLists[key];
  if (cur?.loading || cur?.loadingMore || (cur && !more && !refresh && !cur.error)) return renderDemos();
  if (more) cur.loadingMore = true;
  else demoLists[key] = { items: [], total: 0, loading: true };
  renderDemos();
  const start = more ? cur.items.length : 0;
  try {
    const r = await call("demos.list", { list, players, start, refresh });
    demoWatch = r.watched;
    const prev = more ? cur.items : [];
    const seen = new Set(prev.map((d) => d.id));
    demoLists[key] = { items: [...prev, ...r.page.items.filter((d) => !seen.has(d.id))], total: r.page.total };
  } catch (e) {
    demoLists[key] = more ? { ...cur, loadingMore: false, error: e.message } : { items: [], total: 0, error: e.message };
  } finally {
    if (key === demoKey()) renderDemos();
  }
}

async function refreshDemoWatch() {
  try {
    demoWatch = await call("demos.watched");
    renderDemos();
  } catch { /* shown next time the page loads */ }
}

function currentDemos() {
  if (demoList !== "watch") return demoLists[demoKey()]?.items ?? [];
  return demoWatch.watch
    .map((w) => ({ ...w.demo, comingSoon: !w.released, released: w.released }))
    .filter((d) => demoPlayers === "all" || PLAYER_MATCH[demoPlayers](d.players ?? []))
    .sort((a, b) => (b.released - a.released) || (new Date(a.date ?? 8.64e15) - new Date(b.date ?? 8.64e15)));
}

function demoMatches(d) {
  if (demoFilters.has("tr") && !d.turkish) return false;
  if (demoFilters.has("deck") && d.deck < 2) return false;
  for (const f of demoFilters) if (f !== "tr" && f !== "deck" && !d.tags.includes(f)) return false;
  if (!demoQuery) return true;
  const hay = [d.name, d.description ?? "", ...d.tags].join(" ").toLocaleLowerCase("tr");
  return demoQuery.split(/\s+/).every((w) => hay.includes(w));
}

function renderDemoFilters(items) {
  const count = new Map();
  for (const d of items) for (const t of d.tags) count.set(t, (count.get(t) ?? 0) + 1);
  const tags = [...count].filter(([, n]) => n > 1).sort((a, b) => b[1] - a[1]).slice(0, 9).map(([t]) => t);
  for (const f of demoFilters) if (f !== "tr" && f !== "deck" && !tags.includes(f)) tags.push(f);
  const chip = (key, label) => {
    const c = document.createElement("button");
    c.className = "chip" + (demoFilters.has(key) ? " selected" : "");
    c.textContent = label;
    c.addEventListener("click", () => {
      demoFilters.has(key) ? demoFilters.delete(key) : demoFilters.add(key);
      renderDemos();
    });
    return c;
  };
  $("demo-filters").replaceChildren(chip("tr", "Türkçe"), chip("deck", "Steam Deck'te oynanır"), ...tags.map((t) => chip(t, t)));
}

function renderDemos() {
  document.querySelectorAll("#demo-tabs button").forEach((b) => b.classList.toggle("on", b.dataset.list === demoList));
  $("demo-watch-count").textContent = demoWatch.watch.length ? demoWatch.watch.length : "";
  const grid = $("demo-grid");
  const data = demoList === "watch" ? null : demoLists[demoKey()];
  document.querySelectorAll("#demo-players button").forEach((b) => b.classList.toggle("on", b.dataset.players === demoPlayers));
  const all = currentDemos();
  renderDemoFilters(all);
  $("demo-refresh").disabled = !data || data.loading || data.loadingMore;

  const msg = (text) => grid.replaceChildren(Object.assign(document.createElement("div"), { className: "empty", textContent: text }));
  if (data?.loading) msg("Steam'den demolar alınıyor…");
  else if (data?.error && !all.length) msg("Steam'e ulaşılamadı: " + data.error);
  else if (demoList === "watch" && !demoWatch.watch.length) msg("Henüz takip ettiğin demo yok. \"Yakında\" sekmesinde beğendiğin demonun yıldızına bas; açılınca haber veririz.");
  else {
    const shown = all.filter(demoMatches);
    if (!shown.length) msg("Bu filtreye uyan demo yok.");
    else {
      const grouped = demoList === "new" || demoList === "soon";
      const out = [];
      let group = null;
      for (const d of shown) {
        const g = demoList === "watch" ? (d.released ? "Çıktı, oynanabilir" : "Bekleniyor") : grouped ? demoGroup(d) : null;
        if (g && g !== group) {
          group = g;
          out.push(Object.assign(document.createElement("div"), { className: "demo-group", textContent: g }));
        }
        out.push(demoCard(d));
      }
      grid.replaceChildren(...out);
    }
  }

  const row = $("demo-more-row");
  row.hidden = !data || data.loading || !data.items.length;
  if (data && !data.loading) {
    $("demo-count").textContent = data.error ? "Yüklenemedi: " + data.error
      : `${data.items.length.toLocaleString("tr-TR")} / ${data.total.toLocaleString("tr-TR")} demo gösteriliyor`;
    $("demo-more").hidden = data.items.length >= data.total;
    $("demo-more").disabled = !!data.loadingMore;
    $("demo-more").textContent = data.loadingMore ? "Yükleniyor…" : "Daha fazla göster";
  }
}

function demoCard(d) {
  const watched = demoWatch.watch.some((w) => w.demo.id === d.id);
  const playable = !d.comingSoon || d.released;
  const card = document.createElement("div");
  card.className = "demo-card" + (playable ? "" : " upcoming") + (d.released ? " released" : "");
  card.innerHTML = `<button class="demo-img"><span class="demo-when"></span></button>
    <div class="demo-body">
      <div class="demo-name"></div>
      <div class="demo-desc"></div>
      <div class="demo-tags"></div>
      <div class="demo-foot"><div class="demo-badges"></div><button class="demo-star"></button><button class="btn small demo-act"></button></div>
    </div>`;
  const img = card.querySelector(".demo-img");
  if (d.image) {
    const i = document.createElement("img");
    i.loading = "lazy";
    i.alt = "";
    i.src = d.image;
    i.addEventListener("error", () => i.remove());
    img.prepend(i);
  }
  img.title = "Mağaza sayfasını aç";
  img.addEventListener("click", () => call("app.open", { url: d.url }));
  const when = card.querySelector(".demo-when");
  when.textContent = d.released ? "çıktı" : demoWhen(d);
  when.hidden = !when.textContent;
  card.querySelector(".demo-name").textContent = d.name;
  card.querySelector(".demo-name").title = d.name;
  card.querySelector(".demo-desc").textContent = d.description ?? "";
  card.querySelector(".demo-tags").textContent = d.tags.join(" · ");

  const badges = card.querySelector(".demo-badges");
  const badge = (text, cls, title) => {
    const b = document.createElement("span");
    b.className = "badge " + cls;
    b.textContent = text;
    if (title) b.title = title;
    badges.append(b);
  };
  for (const p of d.players ?? []) badge(p, "coop");
  if (d.turkish) badge("Türkçe", "good");
  if (d.deck === 3) badge("Deck ✓", "me", "Steam Deck: Doğrulandı");
  else if (d.deck === 2) badge("Deck", "", "Steam Deck: Oynanabilir");
  if (d.reviews > 0) badge("%" + d.reviewPercent, d.reviewPercent >= 80 ? "good" : "", `${d.reviewLabel ?? ""} · ${d.reviews.toLocaleString("tr-TR")} inceleme`);
  if (d.gameDate && playable && /\d/.test(d.gameDate)) badge("Oyun: " + d.gameDate, "", "Tam oyunun çıkış tarihi");

  const star = card.querySelector(".demo-star");
  // Watching only makes sense for demos that aren't out yet; the watch list can always remove.
  star.hidden = playable && !watched;
  star.classList.toggle("on", watched);
  star.textContent = watched ? "★" : "☆";
  star.title = watched ? "Takibi bırak" : "Takip et: demo açılınca haber ver";
  star.addEventListener("click", async () => {
    try {
      demoWatch = await call("demos.watch", { demo: d, on: !watched });
      renderDemos();
      if (!watched) toast(`${d.name} takipte. Demo açılınca bildirim gelecek.`, "ok");
    } catch (e) {
      toast(e.message, "error");
    }
  });

  const act = card.querySelector(".demo-act");
  if (playable && demoWatch.steam) {
    act.textContent = "Yükle";
    act.title = "Steam'de demoyu yükle";
    act.addEventListener("click", () => call("demos.install", { id: d.id }).catch((e) => toast(e.message, "error")));
  } else {
    act.textContent = playable ? "Steam'de aç" : "Mağaza";
    act.addEventListener("click", () => call("app.open", { url: d.url }));
  }
  return card;
}

document.querySelectorAll("#demo-tabs button").forEach((b) =>
  b.addEventListener("click", () => {
    demoList = b.dataset.list;
    $("page-demos").scrollIntoView();
    loadDemos();
  }));
document.querySelectorAll("#demo-players button").forEach((b) =>
  b.addEventListener("click", () => {
    demoPlayers = b.dataset.players;
    call("demos.players", { players: demoPlayers });
    loadDemos();
  }));
$("demo-more").addEventListener("click", () => loadDemos({ more: true }));
$("demo-refresh").addEventListener("click", () => loadDemos({ refresh: true }));
$("demo-search").addEventListener("input", (e) => {
  demoQuery = e.target.value.trim().toLocaleLowerCase("tr");
  renderDemos();
});

// ---- Bedava ----
let freeVisited = false;

function onFreePage(visible) {
  if (!visible || freeVisited) return;
  freeVisited = true;
  loadFree();
}

async function loadFree(refresh = false) {
  const btn = $("free-refresh");
  btn.disabled = true;
  try {
    renderFree(await call("free.list", { refresh }));
  } catch (e) {
    $("free-now").innerHTML = "";
    $("free-now").append(Object.assign(document.createElement("div"), { className: "empty", textContent: "Yüklenemedi: " + e.message }));
  } finally {
    btn.disabled = false;
  }
}

function freeLeft(end) {
  const ms = new Date(end) - Date.now();
  if (ms <= 0) return "bitti";
  const d = Math.floor(ms / DAY), h = Math.floor((ms % DAY) / 3600000);
  return d > 0 ? `${d} gün ${h} sa kaldı` : h > 0 ? `${h} sa kaldı` : "son dakikalar";
}

function renderFree(r) {
  $("free-notify").checked = r.notify;
  const now = r.games.filter((g) => !g.upcoming), next = r.games.filter((g) => g.upcoming);
  $("free-sub").textContent = (r.errors.length ? r.errors.join(" · ") + " · " : "") +
    "son bakış " + new Date(r.at).toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" });
  const empty = (text) => Object.assign(document.createElement("div"), { className: "empty", textContent: text });
  $("free-now").replaceChildren(...(now.length ? now.map(freeCard) : [empty("Şu an bedava verilen oyun yok.")]));
  $("free-next-title").hidden = $("free-next").hidden = !next.length;
  $("free-next").replaceChildren(...next.map(freeCard));
}

function freeCard(g) {
  const card = document.createElement("div");
  card.className = "demo-card" + (g.upcoming ? " upcoming" : " free");
  card.innerHTML = `<button class="demo-img"><span class="demo-when"></span><span class="free-store"></span></button>
    <div class="demo-body">
      <div class="demo-name"></div>
      <div class="demo-desc"></div>
      <div class="demo-foot"><div class="demo-badges"></div><button class="btn small demo-act"></button></div>
    </div>`;
  const img = card.querySelector(".demo-img");
  if (g.image) {
    const i = document.createElement("img");
    i.loading = "lazy";
    i.alt = "";
    i.src = g.image;
    i.addEventListener("error", () => i.remove());
    img.prepend(i);
  }
  img.addEventListener("click", () => call("app.open", { url: g.url }));
  const store = card.querySelector(".free-store");
  store.textContent = g.store;
  store.dataset.store = g.store.toLowerCase();
  const when = card.querySelector(".demo-when");
  if (g.upcoming) when.textContent = demoWhen({ date: g.start, comingSoon: true });
  else if (g.end) {
    when.textContent = freeLeft(g.end);
    when.title = new Date(g.end).toLocaleString("tr-TR", { day: "numeric", month: "long", hour: "2-digit", minute: "2-digit" }) + " saatine kadar";
  }
  when.hidden = !when.textContent;
  card.querySelector(".demo-name").textContent = g.title;
  card.querySelector(".demo-name").title = g.title;
  card.querySelector(".demo-desc").textContent = g.description ?? "";

  const badges = card.querySelector(".demo-badges");
  const badge = (text, cls, title) => {
    const b = document.createElement("span");
    b.className = "badge " + cls;
    b.textContent = text;
    if (title) b.title = title;
    badges.append(b);
  };
  if (g.originalPrice) badge(g.originalPrice, "price", "Normal fiyatı");
  if (g.dlcFor != null) badge("DLC", "new", g.dlcFor ? `Oynamak için ${g.dlcFor} gerekir` : "Ana oyun gerekir");
  for (const p of g.players) badge(p, "coop");

  const act = card.querySelector(".demo-act");
  act.textContent = g.upcoming ? "Mağaza" : "Mağazada al";
  act.addEventListener("click", () => call("app.open", { url: g.url }));
  return card;
}

$("free-refresh").addEventListener("click", () => loadFree(true));
$("free-notify").addEventListener("change", (e) => call("free.notify", { on: e.target.checked }));

function toast(message, level = "info") {
  const el = document.createElement("div");
  el.className = "toast " + level;
  el.textContent = message;
  $("toasts").append(el);
  setTimeout(() => el.remove(), level === "error" ? 7000 : 3500);
}

call("getState").then(render);
