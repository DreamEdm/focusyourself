import { applyPage, formatTime, t } from "./shared.js";

const $ = (id) => document.getElementById(id);
const LABELS = { focus: "popupFocus", break: "popupBreak", idle: "popupIdle" };

async function render() {
  const { state, connected } = await chrome.storage.local.get(["state", "connected"]);
  const lang = applyPage(document, state);
  const phase = state?.state ?? "idle";
  $("card").className = `card ${phase}`;
  $("phase").textContent = t(lang, LABELS[phase] ?? "popupIdle");
  $("time").textContent = state?.endsAt && phase !== "idle" ? formatTime(state.endsAt - Date.now()) : "--:--";
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
