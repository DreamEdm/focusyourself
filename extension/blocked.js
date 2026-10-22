import { applyPage, clockNow, formatTime, quotesFor, t } from "./shared.js";

const $ = (id) => document.getElementById(id);
const CIRCUMFERENCE = 2 * Math.PI * 116;

// 原网址放在 # 后面（由屏蔽规则或标签页重定向写入）
const originalUrl = decodeOriginal(location.hash.slice(1));
let state = null;
let released = false;
let lang = applyPage(document, null);

function decodeOriginal(s) {
  if (!s) return "";
  try {
    const u = new URL(s);
    return u.protocol === "http:" || u.protocol === "https:" ? u.href : "";
  } catch {
    return "";
  }
}

function showSite() {
  if (!originalUrl) {
    $("sub").textContent = t(lang, "subNoSite");
    return;
  }
  $("site").textContent = new URL(originalUrl).hostname.replace(/^www\./, "");
}

function rotateQuote() {
  const q = $("quote");
  q.classList.add("fade");
  setTimeout(() => {
    const quotes = quotesFor(lang);
    q.textContent = `“${quotes[Math.floor(Math.random() * quotes.length)]}”`;
    q.classList.remove("fade");
  }, 600);
}

function render() {
  if (!state || released) return;
  if (state.state !== "focus") {
    release();
    return;
  }
  const now = clockNow(state);
  const remaining = state.endsAt ? state.endsAt - now : 0;
  $("phase").textContent = state.pausedAt ? t(lang, "phasePaused") : t(lang, "phaseFocus");
  $("until").textContent = state.pausedAt ? t(lang, "untilPaused") : t(lang, "until");
  document.body.classList.toggle("paused", !!state.pausedAt);
  $("time").textContent = state.endsAt ? formatTime(remaining) : "--:--";
  document.title = state.endsAt ? `${formatTime(remaining)} · ${t(lang, "titleSuffix")}` : t(lang, "pageTitle");

  if (state.endsAt && state.startedAt && state.endsAt > state.startedAt) {
    const progress = Math.min(1, Math.max(0, (now - state.startedAt) / (state.endsAt - state.startedAt)));
    $("progress").style.strokeDashoffset = String(CIRCUMFERENCE * (1 - progress));
  }
  $("rounds").textContent = t(lang, "rounds", (state.rounds ?? 0) + 1);
}

function release() {
  if (released) return;
  released = true;
  document.body.classList.add("released");
  $("phase").textContent = t(lang, "phaseBreak");
  $("title").textContent = t(lang, "releasedTitle");
  $("progress").style.strokeDashoffset = "0";
  $("release").hidden = false;
  setTimeout(() => {
    if (originalUrl) location.replace(originalUrl);
    else $("releaseBody").textContent = t(lang, "releaseFree");
  }, 1600);
}

/** 语言或主题变化时重新填充文字。 */
function localize() {
  lang = applyPage(document, state);
  showSite();
}

async function init() {
  ({ state } = await chrome.storage.local.get("state"));
  localize();
  rotateQuote();
  setInterval(rotateQuote, 9000);

  if (!state || state.state !== "focus") {
    // 打开时就不在专注期（例如残留的历史记录），直接回去
    if (originalUrl) location.replace(originalUrl);
    return;
  }
  render();
  setInterval(render, 500);

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === "local" && changes.state) {
      const prevLang = state?.lang;
      const prevTheme = state?.theme;
      state = changes.state.newValue;
      if (state?.lang !== prevLang || state?.theme !== prevTheme) localize();
      render();
    }
  });
}

$("close").addEventListener("click", async () => {
  const tab = await chrome.tabs.getCurrent();
  if (tab?.id != null) chrome.tabs.remove(tab.id);
  else window.close();
});

$("back").addEventListener("click", () => {
  if (history.length > 1) history.back();
  else location.href = "about:blank";
});

init();
