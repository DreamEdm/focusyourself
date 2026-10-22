import { applyPage, clockNow, formatTime, t } from "./shared.js";

const $ = (id) => document.getElementById(id);
const LABELS = { focus: "popupFocus", break: "popupBreak", idle: "popupIdle" };

async function render() {
  const { state, connected } = await chrome.storage.local.get(["state", "connected"]);
  const lang = applyPage(document, state);
  const phase = state?.state ?? "idle";
  const paused = !!state?.pausedAt && phase !== "idle";
  $("card").className = `card ${phase}${paused ? " paused" : ""}`;
  $("phase").textContent = paused ? t(lang, "popupPaused") : t(lang, LABELS[phase] ?? "popupIdle");
  $("time").textContent = state?.endsAt && phase !== "idle" ? formatTime(state.endsAt - clockNow(state)) : "--:--";
  $("domains").textContent =
    phase === "focus" && state.domains?.length
      ? t(lang, "popupBlocking", state.domains.join(t(lang, "listSep")))
      : t(lang, "popupNotBlocking");

  $("dot").classList.toggle("on", !!connected);
  $("conn").textContent = t(lang, connected ? "popupConnected" : "popupDisconnected");
  $("hint").textContent = connected ? "" : t(lang, "popupHint");
}

chrome.runtime.sendMessage({ type: "reconnect" }).catch(() => {});
render();
setInterval(render, 1000);
