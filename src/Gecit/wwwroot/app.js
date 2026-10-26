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

function toast(message, level = "info") {
  const el = document.createElement("div");
  el.className = "toast " + level;
  el.textContent = message;
  $("toasts").append(el);
  setTimeout(() => el.remove(), level === "error" ? 7000 : 3500);
}

call("getState").then(render);
