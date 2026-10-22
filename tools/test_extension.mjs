// 校验扩展的屏蔽规则：node tools/test_extension.mjs
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { buildRules, matchesDomain, formatTime, clockNow, STRINGS, t, pickLang, pickTheme, quotesFor } from "../extension/shared.js";

const page = "chrome-extension://gfjfpbehokfclllomgjpppgmkaghlgjo/blocked.html";
const rules = buildRules(["youtube.com", "bilibili.com"], page);
assert.equal(rules.length, 4);
const ids = new Set(rules.map((r) => r.id));
assert.equal(ids.size, 4, "rule ids must be unique");

const redirect = rules.filter((r) => r.action.type === "redirect");
const hit = (url) => redirect.some((r) => new RegExp(r.condition.regexFilter).test(url));

for (const url of [
  "https://youtube.com/",
  "https://www.youtube.com/watch?v=abc",
  "https://m.youtube.com",
  "http://www.bilibili.com/video/BV1?p=2#t=10",
  "https://live.bilibili.com:443/123",
]) assert.ok(hit(url), `should block ${url}`);

for (const url of [
  "https://notyoutube.com/",
  "https://youtube.com.evil.org/",
  "https://example.com/?q=youtube.com",
  "https://github.com/bilibili",
]) assert.ok(!hit(url), `should NOT block ${url}`);

// 模拟 regexSubstitution 的 \0：整段原网址放在 # 后面
const r = redirect[0];
const target = "https://www.youtube.com/watch?v=abc&t=1";
const substituted = target.replace(new RegExp(r.condition.regexFilter), r.action.redirect.regexSubstitution.replace(String.raw`\0`, () => "$&"));
assert.equal(substituted, `${page}#${target}`);

assert.equal(matchesDomain("https://www.bilibili.com/x", ["bilibili.com"]), "bilibili.com");
assert.equal(matchesDomain("https://bilibili.com.cn/", ["bilibili.com"]), null);
assert.equal(matchesDomain("chrome://extensions", ["bilibili.com"]), null);
assert.equal(formatTime(25 * 60 * 1000), "25:00");
assert.equal(formatTime(61_000), "01:01");
assert.equal(formatTime(3_725_000), "1:02:05");
// 暂停时倒计时冻结在暂停时刻
assert.equal(clockNow({ pausedAt: 1000 }, 99_999), 1000);
assert.equal(clockNow({ pausedAt: null }, 99_999), 99_999);
assert.equal(formatTime(61_000 + 1000 - clockNow({ pausedAt: 1000 }, 500_000)), "01:01");

// ---- 文字：两种语言键一致、占位符数量一致、页面引用的键都存在 ----
const en = Object.keys(STRINGS.en).sort();
const zh = Object.keys(STRINGS["zh-CN"]).sort();
assert.deepEqual(zh, en, "en / zh-CN keys must match");
const holes = (s) => (s.match(/\{\d+\}/g) ?? []).sort().join();
for (const k of en) assert.equal(holes(STRINGS["zh-CN"][k]), holes(STRINGS.en[k]), `placeholders differ: ${k}`);
for (const file of ["blocked.html", "popup.html", "blocked.js", "popup.js", "background.js"]) {
  const src = readFileSync(new URL(`../extension/${file}`, import.meta.url), "utf8");
  const used = [
    ...src.matchAll(/data-i18n(?:-label)?="(\w+)"/g),
    ...src.matchAll(/t\(\w+(?:\([^)]*\))?,\s*"(\w+)"/g),
  ].map((m) => m[1]);
  for (const k of used) assert.ok(k in STRINGS.en, `${file} uses missing key ${k}`);
}
assert.equal(t("zh-CN", "rounds", 3), "本次的第 3 个番茄");
assert.equal(t("en", "popupBlocking", "a.com"), "Blocking: a.com");
assert.equal(pickLang({ lang: "zh-CN" }), "zh-CN");
assert.equal(pickLang({ lang: "en" }), "en");
assert.equal(pickTheme({ theme: "light" }), "light");
assert.equal(quotesFor("en").length, quotesFor("zh-CN").length);
console.log("extension rule tests passed");
