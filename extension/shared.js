// 扩展各页面共用的工具函数（ES module）

export const BRIDGE_PORT = 47621;
export const BRIDGE_URL = `ws://127.0.0.1:${BRIDGE_PORT}/ws`;

/** 网址是否属于某个被屏蔽的域名（含所有子域名）。 */
export function matchesDomain(url, domains) {
  let host;
  try {
    const u = new URL(url);
    if (u.protocol !== "http:" && u.protocol !== "https:") return null;
    host = u.hostname.toLowerCase();
  } catch {
    return null;
  }
  return domains.find((d) => host === d || host.endsWith("." + d)) ?? null;
}

function escapeRegex(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * 为每个域名生成两条 declarativeNetRequest 规则：
 *  - 主页面：重定向到 blocked.html，并把原网址放在 # 后面，休息时用于跳回
 *  - 内嵌框架（例如别的网站里嵌入的 YouTube 播放器）：直接拦截
 */
export function buildRules(domains, blockedPageUrl, firstId = 1000) {
  const rules = [];
  domains.forEach((domain, i) => {
    rules.push({
      id: firstId + i * 2,
      priority: 1,
      action: { type: "redirect", redirect: { regexSubstitution: `${blockedPageUrl}#\\0` } },
      condition: {
        regexFilter: `^https?://([^/?#]*\\.)?${escapeRegex(domain)}([:/?#].*)?$`,
        resourceTypes: ["main_frame"],
      },
    });
    rules.push({
      id: firstId + i * 2 + 1,
      priority: 1,
      action: { type: "block" },
      condition: { requestDomains: [domain], resourceTypes: ["sub_frame"] },
    });
  });
  return rules;
}

/** 计时用的“当前时刻”：桌面端暂停时停在暂停的那一刻。 */
export function clockNow(state, now = Date.now()) {
  return state?.pausedAt ?? now;
}

export function formatTime(ms) {
  const total = Math.max(0, Math.ceil(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}

const QUOTES_ZH = [
  "此刻的专注，是给未来自己的礼物。",
  "先完成，再休息。视频不会跑掉。",
  "想打开它，只是大脑想逃避一点点困难。",
  "再坚持一会儿，休息时间马上就到。",
  "深呼吸，回到你正在做的那件事。",
  "分心只要一秒，找回状态却要很久。",
  "把注意力留给真正重要的事。",
  "今天的你，比昨天更坐得住。",
  "这一轮结束前，世界不会错过你。",
  "难的事做完了，轻松的事才更好玩。",
  "你不是缺时间，你在找回时间。",
  "一次只做一件事，然后把它做好。",
];

const QUOTES_EN = [
  "The focus you give now is a gift to your future self.",
  "Finish first, then rest. The videos aren't going anywhere.",
  "Wanting to open it is just your brain dodging a little difficulty.",
  "Hold on a bit longer — your break is almost here.",
  "Take a deep breath and return to what you were doing.",
  "Distraction takes a second; getting back into flow takes much longer.",
  "Save your attention for what really matters.",
  "You can sit with it longer today than yesterday.",
  "The world won't miss you before this round ends.",
  "Do the hard thing first; the fun things feel better afterwards.",
  "You're not short on time — you're taking it back.",
  "One thing at a time, and do it well.",
];

/** 扩展页面的文字。两种语言的键必须一致（tools/test_extension.mjs 会检查）。 */
export const STRINGS = {
  en: {
    pageTitle: "Focusing · FocusYourself",
    titleSuffix: "Focusing",
    ringLabel: "Time remaining",
    phaseFocus: "Focusing",
    phaseBreak: "Break",
    until: "until break",
    phasePaused: "Paused",
    untilPaused: "timer paused · still blocked",
    title: "This moment belongs to something more important",
    subBefore: "You were about to open ",
    subAfter: ". It'll be waiting for you on your break.",
    subNoSite: "This website will be waiting for you on your break.",
    thisSite: "this website",
    rounds: "Pomodoro #{0} this session",
    autoReturn: "You'll return to this page on your break",
    closeTab: "Close this tab",
    goBack: "Go back",
    releaseTitle: "Break time!",
    releaseBody: "Taking you back to the page you wanted…",
    releaseFree: "You're free to browse now.",
    releasedTitle: "Well done — take a break",
    popupFocus: "Focusing",
    popupBreak: "On a break",
    popupIdle: "Not started",
    popupPaused: "Paused",
    popupBlocking: "Blocking: {0}",
    popupNotBlocking: "No websites are blocked during breaks or when idle",
    popupConnected: "Connected to FocusYourself",
    popupDisconnected: "Desktop app not connected",
    popupHint: "Open FocusYourself. While disconnected, the last blocking state is kept.",
    badgeTitle: "FocusYourself · Focusing, blocking {0} websites",
    listSep: ", ",
  },
  "zh-CN": {
    pageTitle: "专注中 · FocusYourself",
    titleSuffix: "专注中",
    ringLabel: "剩余时间",
    phaseFocus: "专注中",
    phaseBreak: "休息",
    until: "距离休息",
    phasePaused: "已暂停",
    untilPaused: "计时已暂停 · 拦截仍有效",
    title: "这一刻，属于更重要的事",
    subBefore: "你刚才想打开 ",
    subAfter: "，它会在休息时等你。",
    subNoSite: "这个网站会在休息时等你。",
    thisSite: "这个网站",
    rounds: "本次的第 {0} 个番茄",
    autoReturn: "休息时自动回到这个网页",
    closeTab: "关闭这个标签页",
    goBack: "返回上一页",
    releaseTitle: "休息时间到！",
    releaseBody: "正在带你回到刚才的网页…",
    releaseFree: "现在可以自由浏览了。",
    releasedTitle: "辛苦了，休息一下吧",
    popupFocus: "专注中",
    popupBreak: "休息中",
    popupIdle: "未开始",
    popupPaused: "已暂停",
    popupBlocking: "正在屏蔽：{0}",
    popupNotBlocking: "休息和空闲时不屏蔽任何网站",
    popupConnected: "已连接 FocusYourself 桌面端",
    popupDisconnected: "未连接桌面端",
    popupHint: "请打开 FocusYourself。断开期间会保持最后一次的屏蔽状态。",
    badgeTitle: "FocusYourself · 专注中，屏蔽 {0} 个网站",
    listSep: "、",
  },
};

/** 桌面端下发的语言优先；还没连接过时按浏览器语言（中文 → 简体中文，其余英文）。 */
export function pickLang(state) {
  if (state?.lang === "zh-CN" || state?.lang === "en") return state.lang;
  const nav = (globalThis.navigator?.language ?? "en").toLowerCase();
  return nav.startsWith("zh") ? "zh-CN" : "en";
}

export function pickTheme(state) {
  if (state?.theme === "light" || state?.theme === "dark") return state.theme;
  return globalThis.matchMedia?.("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

export function t(lang, key, ...args) {
  const table = STRINGS[lang] ?? STRINGS.en;
  const text = table[key] ?? STRINGS.en[key] ?? key;
  return text.replace(/\{(\d+)\}/g, (_, i) => String(args[Number(i)] ?? ""));
}

export function quotesFor(lang) {
  return lang === "zh-CN" ? QUOTES_ZH : QUOTES_EN;
}

/** 填充带 data-i18n 的元素，并设置 <html lang> 与主题。 */
export function applyPage(doc, state) {
  const lang = pickLang(state);
  doc.documentElement.lang = lang;
  doc.documentElement.dataset.theme = pickTheme(state);
  for (const el of doc.querySelectorAll("[data-i18n]")) el.textContent = t(lang, el.dataset.i18n);
  for (const el of doc.querySelectorAll("[data-i18n-label]")) el.setAttribute("aria-label", t(lang, el.dataset.i18nLabel));
  return lang;
}
