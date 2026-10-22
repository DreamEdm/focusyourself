// FocusYourself 扩展后台：连接桌面端，按专注/休息状态开关网站屏蔽规则。
import { BRIDGE_URL, buildRules, matchesDomain, pickLang, t } from "./shared.js";

const BLOCKED_PAGE = chrome.runtime.getURL("blocked.html");
const HEARTBEAT_MS = 20_000;
const RECONNECT_MS = 5_000;
// 与桌面端断开后，专注结束时间过了这么久仍未收到新状态，就自动解除屏蔽（防止永久屏蔽）
const STALE_GRACE_MS = 60_000;

let ws = null;
let heartbeatTimer = null;
let reconnectTimer = null;

function browserName() {
  const brands = navigator.userAgentData?.brands ?? [];
  const brand = brands.map((b) => b.brand).find((b) => !/not.?a.?brand|chromium/i.test(b));
  return brand ?? "Chromium";
}

function connect() {
  if (ws && (ws.readyState === WebSocket.CONNECTING || ws.readyState === WebSocket.OPEN)) return;
  clearTimeout(reconnectTimer);
  try {
    ws = new WebSocket(BRIDGE_URL);
  } catch {
    scheduleReconnect();
    return;
  }

  ws.onopen = () => {
    send({ type: "hello", browser: browserName(), version: chrome.runtime.getManifest().version });
    clearInterval(heartbeatTimer);
    // 心跳既告诉桌面端“扩展还在”，也让 MV3 service worker 保持活跃
    heartbeatTimer = setInterval(() => send({ type: "heartbeat" }), HEARTBEAT_MS);
    chrome.storage.local.set({ connected: true });
  };

  ws.onmessage = (event) => {
    let msg;
    try {
      msg = JSON.parse(event.data);
    } catch {
      return;
    }
    if (msg.type === "state") applyState(msg);
  };

  ws.onclose = () => {
    clearInterval(heartbeatTimer);
    ws = null;
    chrome.storage.local.set({ connected: false });
    scheduleReconnect();
  };

  ws.onerror = () => {
    // onclose 随后会被调用
  };
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connect, RECONNECT_MS);
}

function send(obj) {
  if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

let applying = Promise.resolve();

/** 串行应用状态，避免两条消息同时修改规则。 */
function applyState(msg) {
  applying = applying.then(() => doApplyState(msg)).catch((e) => console.error("FocusYourself:", e));
  return applying;
}

async function doApplyState(msg) {
  const { state: prev } = await chrome.storage.local.get("state");
  const state = {
    state: msg.state,
    domains: Array.isArray(msg.domains) ? msg.domains : [],
    endsAt: msg.endsAt ?? null,
    startedAt: msg.startedAt ?? null,
    rounds: msg.rounds ?? 0,
    // 桌面端暂停的时刻：倒计时冻结在这一刻，拦截照旧
    pausedAt: msg.pausedAt ?? null,
    // 断线时自动解除的 idle 状态不带语言/主题，沿用上一次的
    lang: msg.lang ?? prev?.lang ?? null,
    theme: msg.theme ?? prev?.theme ?? null,
    receivedAt: Date.now(),
  };
  await chrome.storage.local.set({ state });

  const existing = await chrome.declarativeNetRequest.getDynamicRules();
  const focusing = state.state === "focus";
  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: existing.map((r) => r.id),
    addRules: focusing ? buildRules(state.domains, BLOCKED_PAGE) : [],
  });

  if (focusing) await redirectOpenTabs(state.domains);
  await updateBadge(state);
}

/** 专注开始时，已经打开的被屏蔽网页也立即跳转。 */
async function redirectOpenTabs(domains) {
  if (domains.length === 0) return;
  const tabs = await chrome.tabs.query({});
  await Promise.all(
    tabs
      .filter((t) => t.url && matchesDomain(t.url, domains))
      .map((t) => chrome.tabs.update(t.id, { url: `${BLOCKED_PAGE}#${t.url}` }).catch(() => {}))
  );
}

async function updateBadge(state) {
  const focusing = state.state === "focus";
  await chrome.action.setBadgeText({ text: focusing ? "ON" : "" });
  await chrome.action.setBadgeBackgroundColor({ color: "#FF6B5B" });
  await chrome.action.setTitle({
    title: focusing ? t(pickLang(state), "badgeTitle", state.domains.length) : "FocusYourself",
  });
}

/** 断线时判断专注是否已过期：暂停中的状态按收到消息时还剩的时间往后算，避免永远不解除。 */
function effectiveEnd(state) {
  return state.pausedAt ? (state.receivedAt ?? Date.now()) + (state.endsAt - state.pausedAt) : state.endsAt;
}

async function checkStale() {
  if (ws?.readyState === WebSocket.OPEN) return;
  const { state } = await chrome.storage.local.get("state");
  if (state?.state === "focus" && state.endsAt && Date.now() > effectiveEnd(state) + STALE_GRACE_MS) {
    await applyState({ type: "state", state: "idle", domains: [], endsAt: null });
  }
}

// service worker 可能被浏览器随时挂起，用 alarm 兜底重连（最短间隔 30 秒）
chrome.alarms.create("focusyourself-tick", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name !== "focusyourself-tick") return;
  connect();
  checkStale();
});

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);

// 弹出页请求立即重连
chrome.runtime.onMessage.addListener((msg) => {
  if (msg?.type === "reconnect") connect();
});

connect();
