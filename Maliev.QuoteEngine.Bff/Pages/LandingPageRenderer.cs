using System.Net;

namespace Maliev.QuoteEngine.Bff.Pages;

internal static class LandingPageRenderer
{
    private const string WorkspaceHandoffKey = "maliev.quote.workspace.handoff";

    public static async Task RenderAsync(HttpContext context)
    {
        var culture = ResolveCulture(context);
        var copy = LandingCopy.For(culture);
        var isSignedIn = context.User.Identity?.IsAuthenticated == true;
        var primaryHref = isSignedIn ? "/quote/new" : "/auth/sign-in?returnUrl=/quote/new";
        var primaryText = isSignedIn ? copy.StartQuote : copy.SignInStart;
        var topActionText = isSignedIn ? copy.Workspace : copy.SignIn;
        var topActionHref = isSignedIn ? "/quote/new" : "/auth/sign-in?returnUrl=/quote/new";

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(Render(copy, culture, primaryHref, primaryText, topActionHref, topActionText), context.RequestAborted);
    }

    private static string Render(
        LandingCopy copy,
        string culture,
        string primaryHref,
        string primaryText,
        string topActionHref,
        string topActionText)
    {
        var htmlLang = culture == "th-TH" ? "th" : "en";
        var themeLabel = culture == "th-TH" ? "สลับโหมดสี" : "Toggle color mode";
        var assistantLabel = culture == "th-TH" ? "ผู้ช่วย MALIEV" : "MALIEV assistant";

        return $$"""
            <!DOCTYPE html>
            <html lang="{{htmlLang}}" data-culture="{{culture}}">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{Html(copy.Title)}}</title>
                <meta name="description" content="{{Html(copy.Description)}}">
                <link rel="icon" href="/icon-192.png">
                <link rel="preload" href="/images/logo.svg" as="image">
                <link rel="preload" href="/images/generated/metal-components-cutout.png" as="image">
                <script>
                    (() => {
                        const root = document.documentElement;
                        const read = key => {
                            try { return localStorage.getItem(key); } catch { return null; }
                        };
                        const theme = read("maliev.quote.theme") || read("maliev.theme") || "light";
                        const storedCulture = read("maliev.quote.culture") || read("maliev.culture");
                        root.dataset.malievTheme = theme === "dark" ? "dark" : "light";
                        root.style.colorScheme = theme === "dark" ? "dark" : "light";
                        if (storedCulture) {
                            const normalized = storedCulture.toLowerCase().startsWith("th") ? "th-TH" : "en-US";
                            root.dataset.culture = normalized;
                            root.lang = normalized === "th-TH" ? "th" : "en";
                        }
                    })();
                </script>
                <style>
                    :root {
                        color-scheme: light;
                        --ink: #171717;
                        --muted: #535353;
                        --muted-2: #6f6f6f;
                        --line: #ebebeb;
                        --paper: #ffffff;
                        --panel: #ffffff;
                        --panel-tint: #fafafa;
                        --primary: #171717;
                        --primary-contrast: #ffffff;
                        --accent: #0a72ef;
                        --focus-blue: #0072f5;
                        --shadow-ring: rgba(0, 0, 0, 0.08) 0 0 0 1px;
                        --shadow-card: rgba(0, 0, 0, 0.08) 0 0 0 1px, rgba(0, 0, 0, 0.08) 0 18px 42px -26px;
                        font-family: Geist, "Noto Sans Thai", Arial, sans-serif;
                    }

                    :root[data-maliev-theme="dark"] {
                        color-scheme: dark;
                        --ink: #f5f5f5;
                        --muted: #c7c7c7;
                        --muted-2: #9d9d9d;
                        --line: #2d2d30;
                        --paper: #0f0f10;
                        --panel: #161616;
                        --panel-tint: #202024;
                        --primary: #f5f5f5;
                        --primary-contrast: #111111;
                        --shadow-ring: rgba(255, 255, 255, 0.1) 0 0 0 1px;
                        --shadow-card: rgba(255, 255, 255, 0.1) 0 0 0 1px, rgba(0, 0, 0, 0.42) 0 22px 48px -24px;
                    }

                    * { box-sizing: border-box; }

                    body {
                        margin: 0;
                        color: var(--ink);
                        background: var(--paper);
                        font-family: inherit;
                        min-height: 100dvh;
                    }

                    a { color: inherit; }

                    .landing-shell {
                        min-height: 100dvh;
                        display: grid;
                        grid-template-rows: auto minmax(0, 1fr);
                        background: var(--paper);
                    }

                    .landing-topbar {
                        position: sticky;
                        top: 0;
                        z-index: 10;
                        background: color-mix(in srgb, var(--panel) 91%, transparent);
                        border-bottom: 1px solid color-mix(in srgb, var(--line) 92%, transparent);
                        backdrop-filter: blur(14px) saturate(180%);
                        -webkit-backdrop-filter: blur(14px) saturate(180%);
                    }

                    .landing-topbar-inner {
                        width: min(100%, 1240px);
                        min-height: 80px;
                        margin-inline: auto;
                        padding: 0 clamp(24px, 4vw, 40px);
                        display: grid;
                        grid-template-columns: auto minmax(0, 1fr) auto;
                        gap: 24px;
                        align-items: center;
                    }

                    .landing-brand {
                        width: 118px;
                        display: inline-flex;
                        align-items: center;
                        text-decoration: none;
                        animation: landing-brand-inset 620ms cubic-bezier(.2, .8, .2, 1) 80ms both;
                    }

                    .landing-brand img {
                        width: 100%;
                        height: auto;
                        display: block;
                    }

                    :root[data-maliev-theme="dark"] .landing-brand img {
                        filter: invert(1) brightness(1.08) contrast(0.96);
                        opacity: .94;
                    }

                    .landing-actions {
                        justify-self: end;
                        display: inline-flex;
                        align-items: center;
                        gap: 8px;
                        animation: landing-actions-inset 620ms cubic-bezier(.2, .8, .2, 1) 120ms both;
                    }

                    .landing-icon-btn,
                    .landing-language-btn,
                    .landing-signin,
                    .landing-primary,
                    .landing-secondary {
                        height: 40px;
                        border: 0;
                        border-radius: 6px;
                        font: inherit;
                        font-weight: 600;
                        letter-spacing: 0;
                        cursor: pointer;
                        transition: background-color 140ms ease, color 140ms ease, box-shadow 140ms ease, transform 140ms ease;
                    }

                    .landing-icon-btn {
                        width: 40px;
                        display: inline-grid;
                        place-items: center;
                        color: var(--muted);
                        background: transparent;
                    }

                    .landing-icon-btn:hover,
                    .landing-icon-btn[aria-expanded="true"] {
                        color: var(--ink);
                        background: var(--panel-tint);
                    }

                    .landing-icon-btn svg {
                        width: 18px;
                        height: 18px;
                    }

                    .landing-language {
                        display: inline-flex;
                        align-items: center;
                        gap: 0;
                        padding: 4px;
                        border-radius: 999px;
                        background: color-mix(in srgb, var(--muted) 10%, transparent);
                    }

                    .landing-language-btn {
                        min-width: 36px;
                        height: 32px;
                        border-radius: 999px;
                        color: var(--muted);
                        background: transparent;
                        font-size: 11px;
                    }

                    .landing-language-btn[aria-pressed="true"] {
                        color: var(--ink);
                        background: var(--panel);
                        box-shadow: rgba(0, 0, 0, .08) 0 1px 2px;
                    }

                    .landing-signin {
                        display: inline-flex;
                        align-items: center;
                        justify-content: center;
                        min-width: 116px;
                        padding: 0 18px;
                        color: #ffffff;
                        background: var(--accent);
                        text-decoration: none;
                    }

                    .landing-signin:hover,
                    .landing-primary:hover {
                        transform: translateY(-1px);
                        filter: brightness(1.08);
                    }

                    .landing-main {
                        width: min(100%, 1240px);
                        margin-inline: auto;
                        padding: clamp(56px, 9vh, 112px) clamp(24px, 4vw, 40px) 56px;
                        display: grid;
                        grid-template-columns: minmax(0, 1fr) minmax(320px, .9fr);
                        gap: clamp(40px, 6vw, 86px);
                        align-items: center;
                    }

                    .landing-copy {
                        max-width: 610px;
                    }

                    .landing-copy h1 {
                        margin: 0;
                        color: var(--ink);
                        font-size: clamp(48px, 6vw, 80px);
                        font-weight: 760;
                        line-height: .98;
                        letter-spacing: 0;
                    }

                    .landing-copy h1 span {
                        display: block;
                    }

                    .landing-h1-accent {
                        color: var(--accent);
                    }

                    .landing-copy p {
                        max-width: 470px;
                        margin: 28px 0 0;
                        color: var(--muted);
                        font-size: clamp(15px, 1.1vw, 17px);
                        line-height: 1.65;
                    }

                    .landing-cta {
                        display: flex;
                        flex-wrap: wrap;
                        align-items: center;
                        gap: 10px;
                        margin-top: 34px;
                    }

                    .landing-primary,
                    .landing-secondary {
                        display: inline-flex;
                        align-items: center;
                        justify-content: center;
                        gap: 9px;
                        height: 48px;
                        padding: 0 22px;
                        text-decoration: none;
                    }

                    .landing-primary {
                        color: var(--primary-contrast);
                        background: var(--primary);
                    }

                    .landing-secondary {
                        color: var(--ink);
                        background: var(--panel);
                        box-shadow: var(--shadow-ring);
                    }

                    .landing-secondary:hover {
                        background: var(--panel-tint);
                    }

                    .landing-benefits {
                        display: flex;
                        flex-wrap: wrap;
                        gap: 0;
                        margin: 40px 0 0;
                        padding: 20px 0 0;
                        list-style: none;
                        border-top: 1px solid var(--line);
                    }

                    .landing-benefits li {
                        padding-right: 16px;
                        margin-right: 16px;
                        border-right: 1px solid color-mix(in srgb, var(--muted) 25%, transparent);
                        color: var(--muted);
                        font-size: 12px;
                        font-weight: 600;
                        white-space: nowrap;
                    }

                    .landing-benefits li:last-child {
                        padding-right: 0;
                        margin-right: 0;
                        border-right: 0;
                        color: var(--ink);
                    }

                    .landing-visual {
                        position: relative;
                        min-height: min(48vh, 460px);
                        display: grid;
                        place-items: center;
                    }

                    .landing-visual::before {
                        content: "";
                        position: absolute;
                        inset: auto 8% 1% 8%;
                        height: 132px;
                        transform: perspective(420px) rotateX(62deg);
                        transform-origin: bottom center;
                        background-image:
                            linear-gradient(color-mix(in srgb, var(--muted) 18%, transparent) 1px, transparent 1px),
                            linear-gradient(90deg, color-mix(in srgb, var(--muted) 18%, transparent) 1px, transparent 1px);
                        background-size: 28px 28px;
                        opacity: .6;
                        mask-image: radial-gradient(ellipse 80% 100% at center bottom, #000 0%, transparent 80%);
                    }

                    .landing-visual img {
                        position: relative;
                        z-index: 1;
                        width: min(100%, 520px);
                        max-height: min(46vh, 420px);
                        object-fit: contain;
                        border-radius: 8px;
                        padding: 18px;
                        background: #f2f2f2;
                        box-shadow: var(--shadow-card), rgba(0, 0, 0, .08) 0 24px 44px -18px;
                    }

                    .landing-assistant-panel {
                        position: fixed;
                        top: 72px;
                        right: max(24px, calc((100vw - 1240px) / 2 + 40px));
                        z-index: 20;
                        width: min(320px, calc(100vw - 48px));
                        padding: 16px;
                        border-radius: 8px;
                        background: var(--panel);
                        box-shadow: var(--shadow-card), rgba(0, 0, 0, .14) 0 18px 48px -20px;
                        opacity: 0;
                        pointer-events: none;
                        transform: translateY(-8px);
                        transition: opacity 160ms ease, transform 160ms ease;
                    }

                    .landing-assistant-panel.is-open {
                        opacity: 1;
                        pointer-events: auto;
                        transform: translateY(0);
                    }

                    .landing-assistant-panel strong {
                        display: block;
                        font-size: 14px;
                    }

                    .landing-assistant-panel p {
                        margin: 8px 0 0;
                        color: var(--muted);
                        font-size: 13px;
                        line-height: 1.5;
                    }

                    .landing-assistant-panel a {
                        display: inline-flex;
                        align-items: center;
                        margin-top: 12px;
                        color: var(--accent);
                        font-size: 13px;
                        font-weight: 700;
                        text-decoration: none;
                    }

                    @keyframes landing-brand-inset {
                        from {
                            opacity: .82;
                            transform: translateX(clamp(-172px, -9vw, -26px));
                        }
                        to {
                            opacity: 1;
                            transform: translateX(0);
                        }
                    }

                    @keyframes landing-actions-inset {
                        from {
                            opacity: .82;
                            transform: translateX(clamp(26px, 9vw, 172px));
                        }
                        to {
                            opacity: 1;
                            transform: translateX(0);
                        }
                    }

                    @media (prefers-reduced-motion: reduce) {
                        .landing-brand,
                        .landing-actions {
                            animation: none;
                        }

                        .landing-signin:hover,
                        .landing-primary:hover {
                            transform: none;
                        }
                    }

                    @media (max-width: 860px) {
                        .landing-topbar-inner {
                            min-height: 68px;
                            gap: 12px;
                            padding-inline: 18px;
                        }

                        .landing-brand {
                            width: 104px;
                        }

                        .landing-actions {
                            gap: 4px;
                        }

                        .landing-icon-btn {
                            width: 34px;
                            height: 34px;
                        }

                        .landing-language {
                            padding: 3px;
                        }

                        .landing-language-btn {
                            min-width: 31px;
                            height: 28px;
                            font-size: 10px;
                        }

                        .landing-signin {
                            min-width: 92px;
                            height: 36px;
                            padding: 0 12px;
                            font-size: 13px;
                        }

                        .landing-main {
                            grid-template-columns: 1fr;
                            padding-top: 34px;
                            gap: 28px;
                        }

                        .landing-visual {
                            order: -1;
                            min-height: 210px;
                        }

                        .landing-visual img {
                            max-height: 220px;
                        }
                    }

                    @media (max-width: 520px) {
                        .landing-icon-btn[data-assistant-toggle] {
                            display: none;
                        }

                        .landing-copy h1 {
                            font-size: 38px;
                        }

                        .landing-copy p {
                            margin-top: 20px;
                        }

                        .landing-cta {
                            align-items: stretch;
                            flex-direction: column;
                        }

                        .landing-primary,
                        .landing-secondary {
                            width: 100%;
                        }
                    }
                </style>
            </head>
            <body>
                <div class="landing-shell">
                    <header class="landing-topbar" data-landing-appbar>
                        <div class="landing-topbar-inner">
                            <a class="landing-brand" href="/" aria-label="MALIEV">
                                <img src="/images/logo.svg" width="118" height="27" alt="MALIEV">
                            </a>
                            <span></span>
                            <div class="landing-actions">
                                <button class="landing-icon-btn" type="button" data-theme-toggle aria-label="{{Html(themeLabel)}}" data-i18n-label-en="Toggle color mode" data-i18n-label-th="สลับโหมดสี">
                                    <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
                                        <path d="M20 14.2A7.4 7.4 0 0 1 9.8 4a8.2 8.2 0 1 0 10.2 10.2Z" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"></path>
                                    </svg>
                                </button>
                                <div class="landing-language" role="group" aria-label="Language">
                                    <button class="landing-language-btn" type="button" data-culture-btn="en-US" aria-pressed="{{Bool(culture == "en-US")}}">EN</button>
                                    <button class="landing-language-btn" type="button" data-culture-btn="th-TH" aria-pressed="{{Bool(culture == "th-TH")}}">TH</button>
                                </div>
                                <button class="landing-icon-btn" type="button" data-assistant-toggle aria-expanded="false" aria-controls="landing-assistant" aria-label="{{Html(assistantLabel)}}" data-i18n-label-en="MALIEV assistant" data-i18n-label-th="ผู้ช่วย MALIEV">
                                    <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
                                        <path d="M5 13a7 7 0 0 1 14 0" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"></path>
                                        <path d="M4 13h3v5H5a1 1 0 0 1-1-1v-4Zm13 0h3v4a1 1 0 0 1-1 1h-2v-5Z" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"></path>
                                        <path d="M17 18c-.8 1.4-2.2 2-4 2h-1" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"></path>
                                    </svg>
                                </button>
                                <a class="landing-signin" href="{{Html(topActionHref)}}" data-wasm-entry data-i18n-en="{{Html(topActionHref == "/quote/new" ? "Workspace" : "Sign in")}}" data-i18n-th="{{Html(topActionHref == "/quote/new" ? "พื้นที่ทำงาน" : "เข้าสู่ระบบ")}}">{{Html(topActionText)}}</a>
                            </div>
                        </div>
                    </header>
                    <aside class="landing-assistant-panel" id="landing-assistant" aria-live="polite">
                        <strong data-i18n-en="MALIEV assistant" data-i18n-th="ผู้ช่วย MALIEV">{{Html(copy.AssistantTitle)}}</strong>
                        <p data-i18n-en="Open the quote workspace to chat with the assistant about files, DFM, materials, and next steps." data-i18n-th="เปิดพื้นที่เสนอราคาเพื่อคุยกับผู้ช่วยเกี่ยวกับไฟล์ DFM วัสดุ และขั้นตอนถัดไป">{{Html(copy.AssistantBody)}}</p>
                        <a href="/demo" data-wasm-entry data-i18n-en="Try the sample file" data-i18n-th="ลองใช้ไฟล์ตัวอย่าง">{{Html(copy.TrySample)}}</a>
                    </aside>
                    <main class="landing-main">
                        <section class="landing-copy" aria-labelledby="landing-title">
                            <h1 id="landing-title">
                                <span data-i18n-en="Get manufacturing quotes," data-i18n-th="ขอใบเสนอราคาการผลิต">{{Html(copy.HeadlineLead)}}</span>
                                <span class="landing-h1-accent" data-i18n-en="with DFM feedback." data-i18n-th="พร้อมข้อเสนอแนะ DFM">{{Html(copy.HeadlineAccent)}}</span>
                            </h1>
                            <p data-i18n-en="Upload CAD files and project details. MALIEV returns expert manufacturability feedback and transparent pricing before you move into production." data-i18n-th="อัปโหลดไฟล์ CAD และรายละเอียดโปรเจกต์ MALIEV จะส่งข้อเสนอแนะด้านการผลิตและราคาที่โปร่งใสก่อนเข้าสู่การผลิต">{{Html(copy.Body)}}</p>
                            <div class="landing-cta">
                                <a class="landing-primary" href="{{Html(primaryHref)}}" data-wasm-entry data-i18n-en="{{Html(primaryHref == "/quote/new" ? "Start a quote" : "Sign in & start quoting")}}" data-i18n-th="{{Html(primaryHref == "/quote/new" ? "เริ่มขอราคา" : "เข้าสู่ระบบแล้วขอราคา")}}">
                                    {{Html(primaryText)}}
                                    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true">
                                        <path d="M2.5 7h9M8 3.5 11.5 7 8 10.5" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"></path>
                                    </svg>
                                </a>
                                <a class="landing-secondary" href="/demo" data-wasm-entry data-i18n-en="Try with sample file" data-i18n-th="ลองใช้ไฟล์ตัวอย่าง">{{Html(copy.TrySample)}}</a>
                            </div>
                            <ul class="landing-benefits" aria-label="{{Html(copy.BenefitsLabel)}}" data-i18n-label-en="Key features" data-i18n-label-th="คุณสมบัติหลัก">
                                <li data-i18n-en="DFM feedback" data-i18n-th="ข้อเสนอแนะ DFM">{{Html(copy.BenefitDfm)}}</li>
                                <li data-i18n-en="Accurate pricing" data-i18n-th="ราคาที่แม่นยำ">{{Html(copy.BenefitPricing)}}</li>
                                <li data-i18n-en="Secure & private" data-i18n-th="ปลอดภัยและเป็นส่วนตัว">{{Html(copy.BenefitSecurity)}}</li>
                            </ul>
                        </section>
                        <figure class="landing-visual" aria-label="{{Html(copy.VisualAlt)}}">
                            <img src="/images/generated/metal-components-cutout.png" width="520" height="390" alt="{{Html(copy.VisualAlt)}}" decoding="async">
                        </figure>
                    </main>
                </div>
                <script>
                    (() => {
                        const handoffKey = "{{WorkspaceHandoffKey}}";
                        const root = document.documentElement;
                        const setStore = (key, value) => {
                            try { localStorage.setItem(key, value); } catch {}
                        };
                        const setCookie = (name, value) => {
                            const secure = location.protocol === "https:" ? "; Secure" : "";
                            document.cookie = `${name}=${encodeURIComponent(value)}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
                        };
                        const applyCulture = culture => {
                            const normalized = culture === "th-TH" ? "th-TH" : "en-US";
                            root.dataset.culture = normalized;
                            root.lang = normalized === "th-TH" ? "th" : "en";
                            document.querySelectorAll("[data-culture-btn]").forEach(button => {
                                button.setAttribute("aria-pressed", button.dataset.cultureBtn === normalized ? "true" : "false");
                            });
                            document.querySelectorAll("[data-i18n-en]").forEach(node => {
                                node.textContent = normalized === "th-TH" ? node.dataset.i18nTh : node.dataset.i18nEn;
                            });
                            document.querySelectorAll("[data-i18n-label-en]").forEach(node => {
                                node.setAttribute("aria-label", normalized === "th-TH" ? node.dataset.i18nLabelTh : node.dataset.i18nLabelEn);
                            });
                            setStore("maliev.quote.culture", normalized);
                            setStore("maliev.culture", normalized);
                            setCookie("maliev.culture", normalized);
                        };
                        const applyTheme = theme => {
                            const normalized = theme === "dark" ? "dark" : "light";
                            root.dataset.malievTheme = normalized;
                            root.style.colorScheme = normalized;
                            setStore("maliev.quote.theme", normalized);
                            setStore("maliev.theme", normalized);
                        };

                        document.querySelectorAll("[data-wasm-entry]").forEach(link => {
                            link.addEventListener("click", () => {
                                try { sessionStorage.setItem("maliev.quote.workspace.handoff", "true"); } catch {}
                            });
                        });

                        document.querySelector("[data-theme-toggle]")?.addEventListener("click", () => {
                            applyTheme(root.dataset.malievTheme === "dark" ? "light" : "dark");
                        });

                        document.querySelectorAll("[data-culture-btn]").forEach(button => {
                            button.addEventListener("click", () => applyCulture(button.dataset.cultureBtn));
                        });

                        const assistantToggle = document.querySelector("[data-assistant-toggle]");
                        const assistantPanel = document.getElementById("landing-assistant");
                        assistantToggle?.addEventListener("click", () => {
                            const isOpen = assistantPanel?.classList.toggle("is-open") ?? false;
                            assistantToggle.setAttribute("aria-expanded", isOpen ? "true" : "false");
                        });

                        applyCulture(root.dataset.culture === "th-TH" ? "th-TH" : "en-US");
                    })();
                </script>
            </body>
            </html>
            """;
    }

    private static string ResolveCulture(HttpContext context)
    {
        var cookieCulture = context.Request.Cookies.TryGetValue("maliev.culture", out var value) ? value : null;
        return cookieCulture?.StartsWith("th", StringComparison.OrdinalIgnoreCase) == true ? "th-TH" : "en-US";
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed record LandingCopy(
        string Title,
        string Description,
        string HeadlineLead,
        string HeadlineAccent,
        string Body,
        string SignInStart,
        string StartQuote,
        string SignIn,
        string Workspace,
        string TrySample,
        string BenefitsLabel,
        string BenefitDfm,
        string BenefitPricing,
        string BenefitSecurity,
        string AssistantTitle,
        string AssistantBody,
        string VisualAlt)
    {
        public static LandingCopy For(string culture)
        {
            return culture == "th-TH"
                ? new LandingCopy(
                    "MALIEV - ขอใบเสนอราคาการผลิต",
                    "หน้าแรกที่โหลดเร็วสำหรับการขอใบเสนอราคา CAD, DFM และงานผลิตแบบกำหนดเองของ MALIEV",
                    "ขอใบเสนอราคาการผลิต",
                    "พร้อมข้อเสนอแนะ DFM",
                    "อัปโหลดไฟล์ CAD และรายละเอียดโปรเจกต์ MALIEV จะส่งข้อเสนอแนะด้านการผลิตและราคาที่โปร่งใสก่อนเข้าสู่การผลิต",
                    "เข้าสู่ระบบแล้วขอราคา",
                    "เริ่มขอราคา",
                    "เข้าสู่ระบบ",
                    "พื้นที่ทำงาน",
                    "ลองใช้ไฟล์ตัวอย่าง",
                    "คุณสมบัติหลัก",
                    "ข้อเสนอแนะ DFM",
                    "ราคาที่แม่นยำ",
                    "ปลอดภัยและเป็นส่วนตัว",
                    "ผู้ช่วย MALIEV",
                    "เปิดพื้นที่เสนอราคาเพื่อคุยกับผู้ช่วยเกี่ยวกับไฟล์ DFM วัสดุ และขั้นตอนถัดไป",
                    "ชิ้นส่วนโลหะตัวอย่างสำหรับใบเสนอราคา MALIEV")
                : new LandingCopy(
                    "MALIEV - Manufacturing quotes with DFM feedback",
                    "Fast landing page for MALIEV custom manufacturing quotes, CAD upload, DFM review, and transparent pricing.",
                    "Get manufacturing quotes,",
                    "with DFM feedback.",
                    "Upload CAD files and project details. MALIEV returns expert manufacturability feedback and transparent pricing before you move into production.",
                    "Sign in & start quoting",
                    "Start a quote",
                    "Sign in",
                    "Workspace",
                    "Try with sample file",
                    "Key features",
                    "DFM feedback",
                    "Accurate pricing",
                    "Secure & private",
                    "MALIEV assistant",
                    "Open the quote workspace to chat with the assistant about files, DFM, materials, and next steps.",
                    "MALIEV machined metal sample part");
        }
    }
}
