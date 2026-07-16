const icon = (name: "arrow" | "check" | "download" | "globe" | "history" | "image" | "refresh" | "shield" | "windows" | "android") => {
  const paths = {
    arrow: '<path d="M5 12h14M13 6l6 6-6 6"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    download: '<path d="M12 3v12m0 0 5-5m-5 5-5-5M5 21h14"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a15 15 0 0 1 0 18M12 3a15 15 0 0 0 0 18"/>',
    history: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5M12 7v5l3 2"/>',
    image: '<rect x="3" y="4" width="18" height="16" rx="3"/><circle cx="9" cy="10" r="2"/><path d="m5 18 4-4 3 3 3-4 4 5"/>',
    refresh: '<path d="M20 7V3l-2 2a9 9 0 1 0 2.2 9"/><path d="M20 3h-4"/>',
    shield: '<path d="M12 3 4.5 6v5.5c0 4.6 3.1 7.8 7.5 9.5 4.4-1.7 7.5-4.9 7.5-9.5V6L12 3Z"/><path d="m8.5 12 2.2 2.2 4.8-5"/>',
    windows: '<path d="M3 5.2 10.5 4v7H3V5.2ZM12 3.8 21 2.5V11h-9V3.8ZM3 13h7.5v7L3 18.8V13Zm9 0h9v8.5L12 20.2V13Z" fill="currentColor" stroke="none"/>',
    android: '<path d="M7 9h10a2 2 0 0 1 2 2v7H5v-7a2 2 0 0 1 2-2Z"/><path d="m8 9-2-3m10 3 2-3M8 18v3m8-3v3M8.5 13h.01m6.99 0h.01"/>',
  } as const;
  return `<svg class="icon" viewBox="0 0 24 24" aria-hidden="true">${paths[name]}</svg>`;
};

export function landingPage(userAgent = ""): string {
  const isAndroid = /android/i.test(userAgent);
  const primary = isAndroid
    ? { label: "下载 Android 版", meta: "v1.8 · Android 10+ · 约 2.4 MB", href: "https://oss.520skx.com/latest/LuodongChat-1.8-android.apk", file: "LuodongChat-1.8-android.apk", platform: "android" as const }
    : { label: "下载 Windows 版", meta: "v1.8 · Windows 10/11 x64 · 约 62 MB", href: "https://oss.520skx.com/latest/LuodongChat-1.8-win-x64-setup.exe", file: "LuodongChat-1.8-win-x64-setup.exe", platform: "windows" as const };
  const secondary = isAndroid
    ? { label: "Windows 版", href: "https://oss.520skx.com/latest/LuodongChat-1.8-win-x64-setup.exe", file: "LuodongChat-1.8-win-x64-setup.exe", platform: "windows" as const }
    : { label: "Android 版", href: "https://oss.520skx.com/latest/LuodongChat-1.8-android.apk", file: "LuodongChat-1.8-android.apk", platform: "android" as const };

  return `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
  <meta name="description" content="泺栋 Chat 是登录即用的独立 AI 对话客户端，支持自动联网、图片生成、多会话和本地历史，无需配置 API Key。">
  <title>泺栋 Chat - 登录即用的独立 AI 对话客户端</title>
  <link rel="stylesheet" href="/assets/landing.css">
</head>
<body>
  <main class="page-shell">
    <header class="topbar">
      <a class="brand" href="/" aria-label="泺栋 Chat 首页"><span class="brand-mark" aria-hidden="true">›_</span><span>泺栋 Chat</span></a>
      <nav class="topnav" aria-label="主导航"><a href="#features">功能</a><a href="#privacy">安全</a><a href="https://github.com/SunKeXu01/LuodongChat">GitHub</a><a href="/healthz">运行状态 <span class="status-dot"></span></a></nav>
    </header>

    <section class="hero">
      <div class="hero-copy">
        <p class="eyebrow"><span></span> Windows 与 Android · 官方免费下载</p>
        <h1>办公、学习与创作的<br><em>AI 对话助手</em></h1>
        <p class="lead">登录即可使用的全平台 AI 客户端。自动联网、支持图片生成，聊天历史仅保存在本机，无需安装官方客户端，也无需配置 API Key。</p>
        <div class="hero-actions" aria-label="客户端下载">
          <a class="primary-download" data-download data-file="${primary.file}" href="${primary.href}">
            <span class="platform-icon">${icon(primary.platform)}</span><span class="download-copy"><strong>${primary.label}</strong><small>${primary.meta}</small></span>${icon("arrow")}
          </a>
          <a class="secondary-download" data-download data-file="${secondary.file}" href="${secondary.href}">${icon(secondary.platform)}<span>${secondary.label}</span></a>
        </div>
        <div class="quick-links"><a href="https://oss.520skx.com/latest/LuodongChat-1.8-win-x64-portable.zip">便携版</a><span>·</span><a href="https://github.com/SunKeXu01/LuodongChat/releases/latest">历史版本</a><span class="download-feedback" aria-live="polite"></span></div>
        <p class="install-note">Windows 版本暂未代码签名，首次安装可能出现系统保护提示。<a href="https://github.com/SunKeXu01/LuodongChat#Windows-使用方式">查看安装指引 →</a></p>
      </div>

      <div class="product-stage" aria-label="泺栋 Chat 客户端界面预览">
        <div class="product-window">
          <div class="window-bar"><span class="window-logo">›_</span><strong>泺栋 Chat</strong><span class="window-controls">—　□　×</span></div>
          <div class="app-layout">
            <aside class="app-sidebar"><button>＋ 新建对话</button><p>今天</p><div class="conversation active">帮我规划周末旅行</div><div class="conversation">生成一张产品海报</div><div class="conversation">整理会议重点</div></aside>
            <section class="chat-panel">
              <header><span>GPT-5.6</span><small>${icon("globe")} 已联网</small></header>
              <div class="messages">
                <div class="bubble user">帮我搜索北京周末天气，并规划一天行程。</div>
                <div class="search-state">${icon("globe")} 已搜索实时天气与开放信息</div>
                <div class="bubble assistant"><strong>周六天气晴朗，适合户外活动。</strong><br>上午可以游览景山公园，中午前往什刹海，下午参观国家博物馆。我已经按距离整理好路线。</div>
                <div class="sources"><span>天气来源</span><span>场馆开放时间</span></div>
              </div>
              <div class="composer"><span>继续提问，或描述你想生成的图片…</span><button>${icon("arrow")}</button></div>
            </section>
          </div>
        </div>
        <div class="preview-caption"><span>${icon("check")} 客户端界面预览</span><span class="typing" aria-label="AI 正在回复"><i></i><i></i><i></i></span></div>
      </div>
    </section>

    <section class="features" id="features" aria-label="核心功能">
      <article>${icon("globe")}<div><strong>自动联网搜索</strong><span>需要最新信息时自动检索</span></div></article>
      <article>${icon("image")}<div><strong>支持图片生成</strong><span>在对话中描述即可生成</span></div></article>
      <article>${icon("history")}<div><strong>多会话与本地历史</strong><span>记录只保存在你的设备</span></div></article>
      <article>${icon("refresh")}<div><strong>后台自动更新</strong><span>自动获取稳定版本</span></div></article>
    </section>

    <section class="download-details" id="downloads">
      <div><span class="section-kicker">更多下载</span><strong>选择适合你的版本</strong></div>
      <div class="download-links"><a href="https://oss.520skx.com/latest/LuodongChat-1.8-win-x64-setup.exe" data-download data-file="LuodongChat-1.8-win-x64-setup.exe">Windows 安装版</a><a href="https://oss.520skx.com/latest/LuodongChat-1.8-win-x64-portable.zip" data-download data-file="LuodongChat-1.8-win-x64-portable.zip">Windows 便携版</a><a href="https://oss.520skx.com/latest/LuodongChat-1.8-android.apk" data-download data-file="LuodongChat-1.8-android.apk">Android APK</a><a href="https://oss.520skx.com/latest/LuodongChat-1.8-win-x64-setup.exe.sha256">SHA-256 校验</a></div>
    </section>

    <aside class="privacy-note" id="privacy">${icon("shield")}<div><strong>隐私与账号安全</strong><p>历史对话不会在泺栋 Chat 服务器持久化；发送消息时，内容仍需传输至模型服务以生成回复。</p></div><a href="https://github.com/SunKeXu01/LuodongChat/blob/main/docs/DATA_MODEL.md">了解数据处理方式 →</a></aside>

    <footer class="footer"><nav><a href="https://github.com/SunKeXu01/LuodongChat/blob/main/docs/DATA_MODEL.md">隐私说明</a><a href="https://github.com/SunKeXu01/LuodongChat#Windows-使用方式">安装帮助</a><a href="https://github.com/SunKeXu01/LuodongChat/releases/latest">更新记录</a><a href="https://github.com/SunKeXu01/LuodongChat/issues">联系我们</a></nav><span>v1.8 · 服务正常 <i class="status-dot"></i></span></footer>
  </main>
  <script src="/assets/landing.js" defer></script>
</body>
</html>`;
}

export const LANDING_JS = `document.querySelectorAll("[data-download]").forEach((link)=>link.addEventListener("click",()=>{const feedback=document.querySelector(".download-feedback");if(!feedback)return;const file=link.dataset.file||"安装包";feedback.textContent="正在开始下载："+file;window.setTimeout(()=>{feedback.textContent=""},5000)}));`;

export const LANDING_CSS = `
:root{font-family:Inter,ui-sans-serif,system-ui,-apple-system,"Segoe UI",sans-serif;-webkit-text-size-adjust:100%;--ink:#111827;--muted:#667085;--line:#e2e8f0;--surface:#fff;--soft:#f4f7fa;--brand:#142033;--accent:#12b886}
*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;min-height:100vh;background:#f5f7fa;color:var(--ink)}a{color:inherit}.icon{width:20px;height:20px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
.page-shell{width:min(1180px,calc(100% - 32px));margin:20px auto;padding:0 32px 20px;border:1px solid var(--line);border-radius:24px;background:var(--surface);box-shadow:0 12px 34px #14203309}.topbar{height:68px;display:flex;align-items:center;justify-content:space-between;border-bottom:1px solid var(--line)}.brand{display:flex;align-items:center;gap:10px;text-decoration:none;font-size:18px;font-weight:750;letter-spacing:-.02em}.brand-mark,.window-logo{display:grid;place-items:center;background:var(--brand);color:#6ee7dc;font:800 14px/1 ui-monospace,SFMono-Regular,Menlo,monospace;letter-spacing:-1px}.brand-mark{width:36px;height:36px;border-radius:11px}.topnav{display:flex;align-items:center;gap:26px;color:var(--muted);font-size:13px;font-weight:600}.topnav a{display:flex;align-items:center;gap:8px;text-decoration:none}.topnav a:hover{color:var(--ink)}.status-dot{display:inline-block;width:7px;height:7px;border-radius:50%;background:var(--accent);box-shadow:0 0 0 3px #12b88618}
.hero{display:grid;grid-template-columns:minmax(0,.92fr) minmax(520px,1.08fr);align-items:center;gap:48px;padding:50px 0 38px}.eyebrow{display:flex;align-items:center;gap:9px;margin:0 0 14px;color:#08785d;font-size:13px;font-weight:700}.eyebrow span{width:20px;height:2px;background:var(--accent)}h1{margin:0;font-size:clamp(39px,4.1vw,56px);line-height:1.08;letter-spacing:-.045em}h1 em{color:#08785d;font-style:normal}.lead{max-width:560px;margin:18px 0 0;color:var(--muted);font-size:16px;line-height:1.75}.hero-actions{display:flex;align-items:stretch;gap:12px;margin-top:28px}.primary-download{display:flex;align-items:center;gap:12px;min-width:355px;padding:12px 15px;border-radius:14px;background:var(--brand);box-shadow:0 8px 18px #14203320;color:#fff;text-decoration:none;transition:transform .2s ease,box-shadow .2s ease}.primary-download:hover{transform:translateY(-2px);box-shadow:0 12px 24px #14203329}.primary-download:active,.secondary-download:active{transform:scale(.99)}.platform-icon{display:grid;place-items:center;width:38px;height:38px;border-radius:10px;background:#ffffff13;color:#79e3d5}.download-copy{display:flex;flex:1;flex-direction:column;gap:4px}.download-copy strong{font-size:15px}.download-copy small{color:#cbd5e1;font-size:12px}.primary-download>.icon{width:18px}.secondary-download{display:flex;align-items:center;justify-content:center;gap:8px;min-width:122px;padding:0 14px;border:1px solid var(--line);border-radius:14px;background:#fff;color:var(--brand);font-size:13px;font-weight:700;text-decoration:none;transition:transform .2s ease,border-color .2s ease}.secondary-download:hover{border-color:#9aa8b9}.secondary-download .icon{width:18px}.quick-links{display:flex;align-items:center;gap:9px;min-height:21px;margin:10px 2px 0;color:var(--muted);font-size:13px}.quick-links a{text-decoration:none}.quick-links a:hover{text-decoration:underline}.download-feedback{margin-left:8px;color:#08785d}.install-note{margin:13px 0 0;color:#7a8494;font-size:12px;line-height:1.5}.install-note a{color:#475467;font-weight:650;text-decoration:none}.install-note a:hover{text-decoration:underline}
.product-stage{position:relative;padding:10px 0 0}.product-stage:before{position:absolute;inset:0 7% 5%;content:"";border-radius:50%;background:#12b88618;filter:blur(45px)}.product-window{position:relative;overflow:hidden;border:1px solid #d9e0e8;border-radius:16px;background:#fff;box-shadow:0 24px 50px #1420331a,0 2px 6px #14203310;transform:perspective(1200px) rotateY(-1.4deg)}.window-bar{height:42px;display:flex;align-items:center;gap:8px;padding:0 12px;border-bottom:1px solid #e7ebf0;background:#fbfcfd;font-size:11px}.window-logo{width:23px;height:23px;border-radius:7px;font-size:9px}.window-controls{margin-left:auto;color:#98a2b3;letter-spacing:2px}.app-layout{display:grid;grid-template-columns:142px 1fr;height:330px}.app-sidebar{padding:12px 9px;border-right:1px solid #e5e7eb;background:#f7f7f8}.app-sidebar button{width:100%;padding:9px;border:0;border-radius:8px;background:#142033;color:#fff;font-size:9px;font-weight:700}.app-sidebar p{margin:15px 7px 6px;color:#98a2b3;font-size:8px}.conversation{margin:3px 0;padding:8px;border-radius:7px;color:#667085;font-size:8px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.conversation.active{border:1px solid #e1e7ed;background:#fff;color:#142033;font-weight:700}.chat-panel{display:grid;grid-template-rows:38px 1fr auto;min-width:0}.chat-panel>header{display:flex;align-items:center;justify-content:space-between;padding:0 15px;border-bottom:1px solid #e7ebf0;font-size:10px}.chat-panel>header small{display:flex;align-items:center;gap:4px;color:#08785d}.chat-panel>header .icon{width:11px}.messages{padding:14px;overflow:hidden;background:#fff}.bubble{width:82%;padding:10px 12px;border:1px solid #e4e9ef;border-radius:12px;color:#344054;font-size:9px;line-height:1.55}.bubble.user{width:72%;margin-left:auto;border-color:#c7dcfa;background:#e8f2ff;color:#175cd3}.bubble.assistant{margin-top:7px;background:#f8fafc}.search-state{display:flex;align-items:center;gap:5px;margin:12px 0 0;color:#08785d;font-size:8px;font-weight:650}.search-state .icon{width:11px}.sources{display:flex;gap:6px;margin-top:7px}.sources span{padding:4px 7px;border:1px solid #dfe5ec;border-radius:999px;color:#667085;font-size:7px}.composer{display:flex;align-items:center;gap:8px;margin:0 14px 13px;padding:7px 8px 7px 11px;border:1px solid #d0d5dd;border-radius:10px;background:#f9fafb;color:#98a2b3;font-size:8px}.composer span{flex:1}.composer button{display:grid;place-items:center;width:25px;height:25px;border:0;border-radius:7px;background:#142033;color:#fff}.composer .icon{width:12px}.preview-caption{position:relative;display:flex;align-items:center;justify-content:space-between;margin:11px 5px 0;color:#667085;font-size:11px}.preview-caption>span:first-child{display:flex;align-items:center;gap:6px}.preview-caption .icon{width:14px;color:var(--accent)}.typing{display:flex;gap:3px}.typing i{width:4px;height:4px;border-radius:50%;background:#12b886;animation:pulse 1.35s infinite}.typing i:nth-child(2){animation-delay:.18s}.typing i:nth-child(3){animation-delay:.36s}@keyframes pulse{0%,70%,100%{opacity:.25;transform:translateY(0)}35%{opacity:1;transform:translateY(-3px)}}
.features{display:grid;grid-template-columns:repeat(4,1fr);border:1px solid var(--line);border-radius:16px;background:#f8fafc}.features article{display:flex;align-items:flex-start;gap:10px;padding:17px 16px}.features article+article{border-left:1px solid var(--line)}.features .icon{flex:0 0 auto;width:19px;color:#08785d}.features strong,.features span{display:block}.features strong{font-size:13px}.features span{margin-top:4px;color:var(--muted);font-size:11px;line-height:1.45}
.download-details{display:flex;align-items:center;justify-content:space-between;gap:24px;margin-top:14px;padding:17px 18px;border-bottom:1px solid var(--line)}.download-details>div:first-child{display:flex;flex-direction:column;gap:3px}.section-kicker{color:#08785d;font-size:10px;font-weight:750;text-transform:uppercase;letter-spacing:.08em}.download-details strong{font-size:13px}.download-links{display:flex;flex-wrap:wrap;justify-content:flex-end;gap:8px 20px}.download-links a{color:#475467;font-size:12px;font-weight:650;text-decoration:none}.download-links a:hover{text-decoration:underline}
.privacy-note{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:12px;margin-top:14px;padding:14px 16px;border-radius:14px;background:#eff9f7;color:#315d56}.privacy-note>.icon{width:22px;color:#08785d}.privacy-note strong{display:block;color:#174e45;font-size:13px}.privacy-note p{margin:3px 0 0;font-size:12px;line-height:1.5}.privacy-note>a{color:#08785d;font-size:12px;font-weight:700;text-decoration:none;white-space:nowrap}.privacy-note>a:hover{text-decoration:underline}.footer{display:flex;align-items:center;justify-content:space-between;gap:20px;margin-top:17px;color:#7a8494;font-size:11px}.footer nav{display:flex;gap:18px}.footer a{text-decoration:none}.footer a:hover{text-decoration:underline}.footer>span{display:flex;align-items:center;gap:7px}
a:focus-visible,button:focus-visible{outline:3px solid #5ab9b0;outline-offset:3px}
@media(max-width:980px){.page-shell{padding-inline:24px}.hero{grid-template-columns:1fr;gap:30px}.hero-copy{max-width:680px}.product-stage{width:min(680px,100%);margin:auto}.features{grid-template-columns:1fr 1fr}.features article:nth-child(3){border-left:0;border-top:1px solid var(--line)}.features article:nth-child(4){border-top:1px solid var(--line)}}
@media(max-width:680px){body{background:#fff}.page-shell{width:100%;margin:0;padding:0 18px 18px;border:0;border-radius:0;box-shadow:none}.topbar{height:62px}.topnav{gap:15px}.topnav a:nth-child(-n+2){display:none}.hero{padding:32px 0 27px;gap:25px}h1{font-size:36px}.lead{font-size:15px}.hero-actions{align-items:stretch;flex-direction:column}.primary-download{min-width:0}.secondary-download{min-height:46px}.product-window{transform:none}.app-layout{grid-template-columns:105px 1fr;height:270px}.app-sidebar{padding:9px 6px}.conversation{padding:7px 5px}.messages{padding:10px}.bubble{font-size:8px}.download-details{align-items:flex-start;flex-direction:column}.download-links{justify-content:flex-start}.privacy-note{grid-template-columns:auto 1fr}.privacy-note>a{grid-column:2;white-space:normal}.footer{align-items:flex-start;flex-direction:column}.footer nav{flex-wrap:wrap}.download-feedback{display:block;margin:0}}
@media(max-width:430px){.topnav a:nth-child(3){display:none}.hero{padding-top:26px}h1{font-size:32px}.features{grid-template-columns:1fr}.features article+article{border-left:0;border-top:1px solid var(--line)}.app-sidebar{display:none}.app-layout{grid-template-columns:1fr}.product-stage{margin-inline:-4px}.window-controls{font-size:8px}.lead{line-height:1.65}}
@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}.primary-download,.secondary-download{transition:none}.typing i{animation:none;opacity:.7}}
`;
