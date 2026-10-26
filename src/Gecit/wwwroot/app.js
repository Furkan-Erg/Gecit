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
