using System.Net;
using System.Text.Json;

namespace Maliev.QuoteEngine.Bff.Pages;

internal static class AuthPageRenderer
{
    private const string WorkspaceHandoffKey = "maliev.quote.workspace.handoff";

    public static Task RenderSignInAsync(HttpContext context) => RenderAsync(context, AuthMode.SignIn);

    public static Task RenderSignUpAsync(HttpContext context) => RenderAsync(context, AuthMode.SignUp);

    private static async Task RenderAsync(HttpContext context, AuthMode mode)
    {
        var culture = ResolveCulture(context);
        var copy = AuthCopy.For(culture, mode);
        var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
        var error = context.Request.Query["error"].ToString();

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(Render(copy, culture, returnUrl, error, mode), context.RequestAborted);
    }

    private static string Render(AuthCopy copy, string culture, string returnUrl, string error, AuthMode mode)
    {
        var htmlLang = culture == "th-TH" ? "th" : "en";
        var isSignUp = mode == AuthMode.SignUp;
        var formMode = isSignUp ? "sign-up" : "sign-in";
        var backText = culture == "th-TH" ? "กลับ" : "Back";
        var continueText = culture == "th-TH" ? "ดำเนินการต่อ" : "Continue";
        var validEmailText = culture == "th-TH" ? "อีเมลถูกต้อง" : "Valid email address.";
        var emailEntryHelp = culture == "th-TH" ? "กรอกอีเมลที่ถูกต้อง" : "Enter a valid email address.";
        var form = isSignUp ? RenderSignUpForm(copy, backText, validEmailText) : RenderSignInForm(copy, backText, validEmailText);
        var alternateHref = isSignUp
            ? $"/auth/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}"
            : $"/auth/sign-up?returnUrl={Uri.EscapeDataString(returnUrl)}";
        var googleHref = $"/auth/google?returnUrl={Uri.EscapeDataString(returnUrl)}";
        var hasError = !string.IsNullOrWhiteSpace(error);
        var errorHiddenAttribute = hasError ? string.Empty : " hidden";

        var errorText = hasError ? error : string.Empty;

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
                        --muted: #545454;
                        --line: #e9e9e9;
                        --paper: #ffffff;
                        --panel: #ffffff;
                        --panel-tint: #f7f7f7;
                        --accent: #0a72ef;
                        --danger: #c73228;
                        --shadow-card: rgba(0, 0, 0, .09) 0 0 0 1px, rgba(0, 0, 0, .08) 0 18px 42px -28px;
                        font-family: Geist, "Noto Sans Thai", Arial, sans-serif;
                    }

                    :root[data-maliev-theme="dark"] {
                        color-scheme: dark;
                        --ink: #f5f5f5;
                        --muted: #c9c9c9;
                        --line: #2d2d30;
                        --paper: #0f0f10;
                        --panel: #161616;
                        --panel-tint: #202024;
                        --shadow-card: rgba(255, 255, 255, .1) 0 0 0 1px, rgba(0, 0, 0, .46) 0 22px 48px -24px;
                    }

                    * { box-sizing: border-box; }

                    body {
                        margin: 0;
                        min-height: 100dvh;
                        color: var(--ink);
                        background: var(--paper);
                        font-family: inherit;
                    }

                    a { color: inherit; }

                    .auth-shell {
                        min-height: 100dvh;
                        display: grid;
                        grid-template-rows: auto minmax(0, 1fr);
                    }

                    .auth-topbar {
                        border-bottom: 1px solid var(--line);
                        background: color-mix(in srgb, var(--panel) 94%, transparent);
                        backdrop-filter: blur(14px) saturate(180%);
                        -webkit-backdrop-filter: blur(14px) saturate(180%);
                    }

                    .auth-topbar-inner {
                        width: min(100%, 1240px);
                        min-height: 68px;
                        margin-inline: auto;
                        padding: 0 clamp(20px, 4vw, 40px);
                        display: grid;
                        grid-template-columns: auto minmax(0, 1fr) auto;
                        gap: 20px;
                        align-items: center;
                    }

                    .auth-brand {
                        width: 118px;
                        display: inline-flex;
                        align-items: center;
                        text-decoration: none;
                    }

                    .auth-brand img {
                        width: 100%;
                        height: auto;
                        display: block;
                    }

                    :root[data-maliev-theme="dark"] .auth-brand img,
                    :root[data-maliev-theme="dark"] .auth-title-logo {
                        filter: invert(1) brightness(1.08) contrast(.96);
                        opacity: .94;
                    }

                    .auth-actions {
                        justify-self: end;
                        display: inline-flex;
                        align-items: center;
                        gap: 8px;
                    }

                    .auth-icon-btn,
                    .auth-language-btn,
                    .auth-top-link,
                    .auth-primary,
                    .auth-google {
                        height: 40px;
                        border: 0;
                        border-radius: 6px;
                        font: inherit;
                        font-weight: 700;
                        letter-spacing: 0;
                    }

                    .auth-icon-btn {
                        width: 40px;
                        display: inline-grid;
                        place-items: center;
                        color: var(--muted);
                        background: transparent;
                        cursor: pointer;
                    }

                    .auth-icon-btn:hover {
                        color: var(--ink);
                        background: var(--panel-tint);
                    }

                    .auth-icon-btn svg {
                        width: 18px;
                        height: 18px;
                    }

                    .auth-language {
                        display: inline-flex;
                        padding: 4px;
                        border-radius: 999px;
                        background: color-mix(in srgb, var(--muted) 10%, transparent);
                    }

                    .auth-language-btn {
                        min-width: 36px;
                        height: 32px;
                        border-radius: 999px;
                        color: var(--muted);
                        background: transparent;
                        cursor: pointer;
                        font-size: 11px;
                    }

                    .auth-language-btn[aria-pressed="true"] {
                        color: var(--ink);
                        background: var(--panel);
                        box-shadow: rgba(0, 0, 0, .08) 0 1px 2px;
                    }

                    .auth-top-link {
                        display: inline-flex;
                        align-items: center;
                        justify-content: center;
                        min-width: 104px;
                        padding: 0 18px;
                        color: #ffffff;
                        background: var(--accent);
                        text-decoration: none;
                    }

                    .auth-main {
                        width: min(100%, 1240px);
                        margin-inline: auto;
                        padding: clamp(64px, 13vh, 156px) clamp(20px, 4vw, 40px) 64px;
                        display: grid;
                        place-items: center;
                    }

                    .auth-panel {
                        width: min(100%, 640px);
                        padding: clamp(34px, 5vw, 56px);
                        border-radius: 8px;
                        background: var(--panel);
                        box-shadow: var(--shadow-card);
                    }

                    .auth-title {
                        display: flex;
                        flex-wrap: wrap;
                        align-items: baseline;
                        gap: 14px;
                        margin: 0;
                        font-size: clamp(42px, 4.5vw, 58px);
                        line-height: 1.04;
                        letter-spacing: 0;
                    }

                    .auth-title-logo {
                        width: min(154px, 42vw);
                        height: auto;
                    }

                    .auth-panel > p {
                        max-width: 520px;
                        margin: 26px 0 0;
                        color: var(--muted);
                        font-size: 17px;
                        line-height: 1.66;
                    }

                    .auth-google {
                        width: 100%;
                        height: 50px;
                        margin-top: 30px;
                        display: inline-flex;
                        align-items: center;
                        justify-content: center;
                        gap: 10px;
                        color: var(--ink);
                        background: var(--panel);
                        border: 1px solid color-mix(in srgb, var(--ink) 60%, transparent);
                        text-decoration: none;
                    }

                    .auth-google:hover {
                        background: var(--panel-tint);
                    }

                    .auth-google-icon {
                        width: 18px;
                        height: 18px;
                        display: inline-flex;
                    }

                    .auth-email-panel {
                        margin-top: 20px;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        background: var(--panel-tint);
                    }

                    .auth-email-panel summary {
                        min-height: 56px;
                        padding: 0 20px;
                        display: flex;
                        align-items: center;
                        justify-content: space-between;
                        cursor: pointer;
                        font-weight: 750;
                    }

                    .auth-email-panel summary::after {
                        content: "+";
                        width: 24px;
                        height: 24px;
                        display: grid;
                        place-items: center;
                        border-radius: 999px;
                        background: var(--panel);
                        box-shadow: rgba(0, 0, 0, .1) 0 0 0 1px;
                    }

                    .auth-email-panel[open] summary::after {
                        content: "-";
                    }

                    .auth-form {
                        display: grid;
                        gap: 16px;
                        padding: 0 20px 20px;
                    }

                    .auth-form-grid {
                        display: grid;
                        grid-template-columns: repeat(2, minmax(0, 1fr));
                        gap: 12px;
                    }

                    .auth-form label {
                        display: grid;
                        gap: 7px;
                        color: var(--ink);
                        font-size: 13px;
                        font-weight: 700;
                    }

                    .auth-form input {
                        width: 100%;
                        min-height: 46px;
                        padding: 0 12px;
                        border: 1px solid var(--line);
                        border-radius: 6px;
                        color: var(--ink);
                        background: var(--panel);
                        font: inherit;
                    }

                    .auth-form input:focus-visible,
                    .auth-google:focus-visible,
                    .auth-primary:focus-visible,
                    .auth-top-link:focus-visible,
                    .auth-icon-btn:focus-visible,
                    .auth-language-btn:focus-visible {
                        outline: 2px solid var(--accent);
                        outline-offset: 2px;
                    }

                    .auth-field-help {
                        color: var(--muted);
                        font-size: 12px;
                        font-weight: 500;
                        line-height: 1.45;
                    }

                    .auth-primary {
                        width: 100%;
                        color: #ffffff;
                        background: var(--accent);
                        cursor: pointer;
                    }

                    .auth-email-entry-form,
                    .auth-credential-form {
                        padding: 0;
                    }

                    .auth-divider {
                        display: flex;
                        align-items: center;
                        gap: 12px;
                        color: var(--muted);
                        font-size: 12px;
                        text-transform: uppercase;
                    }

                    .auth-divider::before,
                    .auth-divider::after {
                        content: "";
                        flex: 1;
                        height: 1px;
                        background: var(--line);
                    }

                    .auth-step-actions {
                        display: grid;
                        gap: 10px;
                    }

                    .auth-requirement-list {
                        display: grid;
                        gap: 7px;
                        margin: -2px 0 0;
                        padding: 0;
                        list-style: none;
                    }

                    .auth-requirement-item {
                        display: grid;
                        grid-template-columns: 18px minmax(0, 1fr);
                        gap: 8px;
                        align-items: start;
                        color: var(--muted);
                        font-size: 12px;
                        line-height: 1.35;
                    }

                    .auth-requirement-item::before {
                        content: "";
                        width: 16px;
                        height: 16px;
                        border-radius: 999px;
                        background: var(--panel-tint);
                        box-shadow: rgba(0, 0, 0, .1) 0 0 0 1px;
                    }

                    .auth-requirement-item.is-met {
                        color: var(--ink);
                    }

                    .auth-requirement-item.is-met::before {
                        content: "\2713";
                        display: grid;
                        place-items: center;
                        color: #ffffff;
                        background: #16a34a;
                        font-size: 11px;
                        font-weight: 700;
                    }

                    .auth-secondary {
                        width: 100%;
                        height: 40px;
                        border-radius: 6px;
                        color: var(--ink);
                        background: var(--panel);
                        border: 1px solid var(--line);
                        cursor: pointer;
                        font: inherit;
                        font-weight: 700;
                        letter-spacing: 0;
                    }
                    .auth-primary:disabled {
                        cursor: progress;
                        opacity: .72;
                    }

                    .auth-error {
                        margin-top: 16px;
                        padding: 11px 12px;
                        border-radius: 6px;
                        color: var(--danger);
                        background: color-mix(in srgb, var(--danger) 10%, transparent);
                        font-size: 13px;
                        line-height: 1.45;
                    }

                    .auth-form .auth-error {
                        margin-top: 0;
                    }

                    .auth-error[hidden] {
                        display: none;
                    }

                    .auth-links {
                        margin-top: 26px;
                        display: flex;
                        justify-content: flex-end;
                    }

                    .auth-links a {
                        color: var(--accent);
                        font-size: 14px;
                        font-weight: 700;
                        text-decoration: none;
                    }

                    @media (max-width: 680px) {
                        .auth-topbar-inner {
                            min-height: 64px;
                            padding-inline: 18px;
                            gap: 10px;
                        }

                        .auth-brand {
                            width: 104px;
                        }

                        .auth-actions {
                            gap: 4px;
                        }

                        .auth-icon-btn {
                            width: 34px;
                            height: 34px;
                        }

                        .auth-language {
                            padding: 3px;
                        }

                        .auth-language-btn {
                            min-width: 31px;
                            height: 28px;
                            font-size: 10px;
                        }

                        .auth-top-link {
                            min-width: 82px;
                            height: 36px;
                            padding: 0 12px;
                            font-size: 13px;
                        }

                        .auth-main {
                            padding-top: 44px;
                            align-items: start;
                        }

                        .auth-panel {
                            padding: 26px;
                        }

                        .auth-form-grid {
                            grid-template-columns: 1fr;
                        }
                    }

                    @media (max-width: 460px) {
                        .auth-actions .auth-icon-btn {
                            display: none;
                        }

                        .auth-panel {
                            box-shadow: none;
                            padding-inline: 0;
                        }
                    }
                </style>
            </head>
            <body>
                <div class="auth-shell">
                    <header class="auth-topbar" data-auth-appbar>
                        <div class="auth-topbar-inner">
                            <a class="auth-brand" href="/" aria-label="MALIEV">
                                <img src="/images/logo.svg" width="118" height="27" alt="MALIEV">
                            </a>
                            <span></span>
                            <div class="auth-actions">
                                <button class="auth-icon-btn" type="button" data-theme-toggle aria-label="{{Html(copy.ThemeToggle)}}">
                                    <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
                                        <path d="M20 14.2A7.4 7.4 0 0 1 9.8 4a8.2 8.2 0 1 0 10.2 10.2Z" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"></path>
                                    </svg>
                                </button>
                                <div class="auth-language" role="group" aria-label="Language">
                                    <button class="auth-language-btn" type="button" data-culture-btn="en-US" aria-pressed="{{Bool(culture == "en-US")}}">EN</button>
                                    <button class="auth-language-btn" type="button" data-culture-btn="th-TH" aria-pressed="{{Bool(culture == "th-TH")}}">TH</button>
                                </div>
                                <a class="auth-top-link" href="{{Html(alternateHref)}}">{{Html(copy.AlternateTopAction)}}</a>
                            </div>
                        </div>
                    </header>
                    <main class="auth-main">
                        <section class="auth-panel" aria-labelledby="auth-title">
                            <h1 class="auth-title" id="auth-title">
                                <span>{{Html(copy.Heading)}}</span>
                            </h1>
                            <p>{{Html(copy.Body)}}</p>
                            <div class="auth-error" role="alert"{{errorHiddenAttribute}}>{{Html(errorText)}}</div>
                            <form class="auth-email-entry-form auth-form" data-auth-step="email" novalidate>
                                <label>
                                    {{Html(copy.EmailLabel)}}
                                    <input id="auth-email-entry" name="email" type="email" autocomplete="email" inputmode="email" placeholder="name@company.com" aria-describedby="auth-email-entry-requirements" required>
                                </label>
                                <ul id="auth-email-entry-requirements" class="auth-requirement-list" aria-live="polite">
                                    <li class="auth-requirement-item is-pending">{{Html(emailEntryHelp)}}</li>
                                </ul>
                                <button type="submit" class="auth-primary" data-auth-continue>{{Html(continueText)}}</button>
                            </form>
                            <div class="auth-divider" role="separator">or</div>
                            <a class="auth-google" href="{{Html(googleHref)}}" aria-label="{{Html(copy.Google)}}">
                                <span class="auth-google-icon" aria-hidden="true">
                                    {{GoogleIcon()}}
                                </span>
                                <span>{{Html(copy.Google)}}</span>
                            </a>
                            <form class="auth-credential-form auth-form" data-auth-form="{{formMode}}" data-auth-step="credentials" data-error-text="{{Html(copy.FormError)}}" data-submitting-text="{{Html(copy.Submitting)}}" novalidate hidden>
                                <div class="auth-error" data-auth-error role="alert" hidden></div>
                                {{form}}
                            </form>
                            <div class="auth-links">
                                <a href="{{Html(alternateHref)}}">{{Html(copy.AlternateBodyAction)}}</a>
                            </div>
                        </section>
                    </main>
                </div>
                <script>
                    (() => {
                        const handoffKey = "{{WorkspaceHandoffKey}}";
                        const returnUrl = {{Json(returnUrl)}};
                        const root = document.documentElement;
                        const setStore = (key, value) => {
                            try { localStorage.setItem(key, value); } catch {}
                        };
                        const setCookie = (name, value) => {
                            const secure = location.protocol === "https:" ? "; Secure" : "";
                            document.cookie = `${name}=${encodeURIComponent(value)}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
                        };
                        const isWorkspaceReturn = value => /^\/(quote\/new|quotes\/new|projects\/new|demo)(?:[/?#]|$)/.test(value);
                        const readValue = (data, name) => (data.get(name) || "").toString();
                        const emailStep = document.querySelector("[data-auth-step='email']");
                        const credentialStep = document.querySelector("[data-auth-step='credentials']");
                        const entryEmail = emailStep?.querySelector("input[name='email']");
                        const credentialEmail = credentialStep?.querySelector("input[name='email']");
                        const credentialPassword = credentialStep?.querySelector("input[name='password']");

                        document.querySelector("[data-theme-toggle]")?.addEventListener("click", () => {
                            const nextTheme = root.dataset.malievTheme === "dark" ? "light" : "dark";
                            root.dataset.malievTheme = nextTheme;
                            root.style.colorScheme = nextTheme;
                            setStore("maliev.quote.theme", nextTheme);
                            setStore("maliev.theme", nextTheme);
                        });

                        document.querySelectorAll("[data-culture-btn]").forEach(button => {
                            button.addEventListener("click", () => {
                                const culture = button.dataset.cultureBtn === "th-TH" ? "th-TH" : "en-US";
                                setStore("maliev.quote.culture", culture);
                                setStore("maliev.culture", culture);
                                setCookie("maliev.culture", culture);
                                location.reload();
                            });
                        });

                        emailStep?.addEventListener("submit", event => {
                            event.preventDefault();
                            if (!entryEmail?.checkValidity()) {
                                entryEmail?.reportValidity();
                                return;
                            }

                            if (credentialEmail) {
                                credentialEmail.value = entryEmail.value;
                            }

                            emailStep.hidden = true;
                            credentialStep.hidden = false;
                            credentialPassword?.focus();
                        });

                        document.querySelector("[data-auth-back]")?.addEventListener("click", () => {
                            credentialStep.hidden = true;
                            emailStep.hidden = false;
                            entryEmail?.focus();
                        });

                        document.querySelectorAll("[data-auth-form]").forEach(form => {
                            form.addEventListener("submit", async event => {
                                event.preventDefault();

                                const submit = form.querySelector("[data-auth-submit]");
                                const error = form.querySelector("[data-auth-error]");
                                const data = new FormData(form);
                                const mode = form.dataset.authForm;
                                const originalText = submit?.textContent || "";

                                if (error) {
                                    error.hidden = true;
                                    error.textContent = "";
                                }

                                if (submit) {
                                    submit.disabled = true;
                                    submit.textContent = form.dataset.submittingText || originalText;
                                }

                                const body = {
                                    email: readValue(data, "email"),
                                    password: readValue(data, "password")
                                };

                                try {
                                    const response = await fetch(mode === "sign-up" ? "/quote/v1/auth/sign-up" : "/quote/v1/auth/sign-in", {
                                        method: "POST",
                                        headers: { "Content-Type": "application/json" },
                                        body: JSON.stringify(body)
                                    });

                                    if (!response.ok) {
                                        let message = form.dataset.errorText || "Authentication could not be completed.";
                                        try {
                                            const problem = await response.json();
                                            message = problem.detail || problem.title || message;
                                        } catch {}

                                        throw new Error(message);
                                    }

                                    if (isWorkspaceReturn(returnUrl)) {
                                        try { sessionStorage.setItem(handoffKey, "true"); } catch {}
                                    }

                                    location.assign(returnUrl);
                                } catch (err) {
                                    if (error) {
                                        error.textContent = err instanceof Error ? err.message : (form.dataset.errorText || "Authentication could not be completed.");
                                        error.hidden = false;
                                    }


                                    if (submit) {
                                        submit.disabled = false;
                                        submit.textContent = originalText;
                                    }
                                }
                            });
                        });
                    })();
                </script>
            </body>
            </html>
            """;
    }

    private static string RenderSignInForm(AuthCopy copy, string backText, string validEmailText) =>
        $$"""
                                    <label>
                                        {{Html(copy.EmailLabel)}}
                                        <input name="email" type="email" autocomplete="email" inputmode="email" placeholder="name@company.com" aria-describedby="sign-in-email-requirements" required>
                                        <small id="sign-in-email-requirements" class="auth-field-help">{{Html(copy.EmailHelp)}}</small>
                                    </label>
                                    <label>
                                        {{Html(copy.PasswordLabel)}}
                                        <input name="password" type="password" autocomplete="current-password" minlength="6" aria-describedby="sign-in-password-requirements" required>
                                    </label>
                                    <ul id="sign-in-password-requirements" class="auth-requirement-list" aria-live="polite">
                                        <li class="auth-requirement-item is-pending">{{Html(validEmailText)}}</li>
                                        <li class="auth-requirement-item is-pending">{{Html(copy.PasswordHelp)}}</li>
                                    </ul>
                                    <div class="auth-step-actions">
                                        <button type="submit" class="auth-primary" data-auth-submit>{{Html(copy.Submit)}}</button>
                                        <button type="button" class="auth-secondary" data-auth-back>{{Html(backText)}}</button>
                                    </div>
            """;

    private static string RenderSignUpForm(AuthCopy copy, string backText, string validEmailText) =>
        $$"""

                                    <label>
                                        {{Html(copy.EmailLabel)}}
                                        <input name="email" type="email" autocomplete="email" inputmode="email" placeholder="name@company.com" aria-describedby="sign-up-email-requirements" required>
                                        <small id="sign-up-email-requirements" class="auth-field-help">{{Html(copy.EmailHelp)}}</small>
                                    </label>
                                    <label>
                                        {{Html(copy.PasswordLabel)}}
                                        <input name="password" type="password" autocomplete="new-password" minlength="6" aria-describedby="sign-up-password-requirements" required>
                                    </label>
                                    <ul id="sign-up-password-requirements" class="auth-requirement-list" aria-live="polite">
                                        <li class="auth-requirement-item is-pending">{{Html(validEmailText)}}</li>
                                        <li class="auth-requirement-item is-pending">{{Html(copy.PasswordHelp)}}</li>
                                    </ul>
                                    <div class="auth-step-actions">
                                        <button type="submit" class="auth-primary" data-auth-submit>{{Html(copy.Submit)}}</button>
                                        <button type="button" class="auth-secondary" data-auth-back>{{Html(backText)}}</button>
                                    </div>
            """;

    private static string ResolveCulture(HttpContext context)
    {
        var cookieCulture = context.Request.Cookies.TryGetValue("maliev.culture", out var value) ? value : null;
        return cookieCulture?.StartsWith("th", StringComparison.OrdinalIgnoreCase) == true ? "th-TH" : "en-US";
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        return !string.IsNullOrWhiteSpace(returnUrl)
            && returnUrl.StartsWith("/", StringComparison.Ordinal)
            && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/quote/new";
    }

    private static string GoogleIcon() =>
        """
        <svg viewBox="0 0 18 18" focusable="false">
            <path fill="#4285F4" d="M17.64 9.2c0-.64-.06-1.25-.16-1.84H9v3.48h4.84c-.21 1.13-.84 2.08-1.8 2.72v2.26h2.91c1.7-1.57 2.69-3.87 2.69-6.62z"></path>
            <path fill="#34A853" d="M9 18c2.43 0 4.47-.81 5.96-2.18l-2.91-2.26c-.81.54-1.84.86-3.05.86-2.34 0-4.33-1.58-5.04-3.71H.96v2.33C2.44 15.98 5.48 18 9 18z"></path>
            <path fill="#FBBC05" d="M3.96 10.71A5.41 5.41 0 0 1 3.68 9c0-.59.1-1.17.28-1.71V4.96H.96A8.96 8.96 0 0 0 0 9c0 1.45.35 2.83.96 4.04l3-2.33z"></path>
            <path fill="#EA4335" d="M9 3.58c1.32 0 2.51.45 3.44 1.35l2.58-2.58C13.46.89 11.43 0 9 0 5.48 0 2.44 2.02.96 4.96l3 2.33C4.67 5.16 6.66 3.58 9 3.58z"></path>
        </svg>
        """;

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string Bool(bool value) => value ? "true" : "false";

    private enum AuthMode
    {
        SignIn,
        SignUp
    }

    private sealed record AuthCopy(
        string Title,
        string Description,
        string Heading,
        string Body,
        string Google,
        string EmailSummary,
        string Submit,
        string Submitting,
        string FormError,
        string AlternateTopAction,
        string AlternateBodyAction,
        string ThemeToggle,
        string EmailLabel,
        string EmailHelp,
        string PasswordLabel,
        string PasswordHelp,
        string FirstNameLabel,
        string LastNameLabel,
        string PhoneLabel,
        string CompanyLabel)
    {
        public static AuthCopy For(string culture, AuthMode mode)
        {
            if (culture == "th-TH")
            {
                return mode == AuthMode.SignUp
                    ? new AuthCopy(
                        "สมัครสมาชิก - MALIEV Quote Engine",
                        "หน้าสมัครสมาชิกแบบ server-rendered สำหรับ MALIEV Quote Engine",
                        "สร้างบัญชี",
                        "Google เป็นวิธีที่เร็วที่สุด และสามารถใช้อีเมลกับรหัสผ่านได้เมื่อทีมของคุณต้องการบัญชีแยก",
                        "ดำเนินการต่อด้วย Google",
                        "สร้างด้วยอีเมล",
                        "ยืนยันอีเมล",
                        "กำลังสร้างบัญชี...",
                        "ไม่สามารถสร้างบัญชีได้",
                        "เข้าสู่ระบบ",
                        "มีบัญชีอยู่แล้ว?",
                        "สลับโหมดสี",
                        "อีเมล",
                        "ใช้อีเมลแบบเต็ม เช่น name@company.com",
                        "รหัสผ่าน",
                        "รหัสผ่านมีอย่างน้อย 6 ตัวอักษร",
                        "ชื่อ",
                        "นามสกุล",
                        "โทรศัพท์",
                        "บริษัท")
                    : new AuthCopy(
                        "เข้าสู่ระบบ - MALIEV Quote Engine",
                        "หน้าเข้าสู่ระบบแบบ server-rendered สำหรับ MALIEV Quote Engine",
                        "เข้าสู่ระบบ",
                        "ใช้บัญชีลูกค้าของคุณสำหรับใบเสนอราคา ไฟล์ CAD ที่อัปโหลด การตรวจ DFM คำสั่งซื้อ NDA และเอกสาร",
                        "ดำเนินการต่อด้วย Google",
                        "ใช้อีเมลแทน",
                        "เข้าสู่ระบบ",
                        "กำลังเข้าสู่ระบบ...",
                        "ไม่สามารถเข้าสู่ระบบด้วยอีเมลหรือรหัสผ่านได้",
                        "สร้างบัญชี",
                        "สร้างบัญชี",
                        "สลับโหมดสี",
                        "อีเมล",
                        "ใช้อีเมลแบบเต็ม เช่น name@company.com",
                        "รหัสผ่าน",
                        "รหัสผ่านมีอย่างน้อย 6 ตัวอักษร",
                        "ชื่อ",
                        "นามสกุล",
                        "โทรศัพท์",
                        "บริษัท");
            }

            return mode == AuthMode.SignUp
                ? new AuthCopy(
                    "Sign up - MALIEV Quote Engine",
                    "Server-rendered account creation for MALIEV Quote Engine.",
                    "Create account",
                    "Google is the fastest path. Email and password is available when your team needs a separate login.",
                    "Continue with Google",
                    "Create with email",
                    "Verify email address",
                    "Creating account...",
                    "Account creation could not be completed.",
                    "Sign in",
                    "Already have an account?",
                    "Toggle color mode",
                    "Email",
                    "Use a full email address, for example name@company.com.",
                    "Password",
                    "Password has at least 6 characters.",
                    "First name",
                    "Last name",
                    "Phone",
                    "Company")
                : new AuthCopy(
                    "Sign in - MALIEV Quote Engine",
                    "Server-rendered sign in for MALIEV Quote Engine.",
                    "Sign in to",
                    "Use your customer account for quotes, uploaded CAD files, DFM review, orders, NDAs, and documents.",
                    "Continue with Google",
                    "Use email instead",
                    "Sign in",
                    "Signing in...",
                    "Email or password sign-in could not be completed.",
                    "Create account",
                    "Create account",
                    "Toggle color mode",
                    "Email",
                    "Use a full email address, for example name@company.com.",
                    "Password",
                    "Password has at least 6 characters.",
                    "First name",
                    "Last name",
                    "Phone",
                    "Company");
        }
    }
}
