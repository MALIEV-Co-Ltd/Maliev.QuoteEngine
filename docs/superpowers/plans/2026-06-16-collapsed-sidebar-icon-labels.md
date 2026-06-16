# Collapsed Sidebar Icon Labels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the quote agent side rail show icon-plus-label navigation items when collapsed and move Settings to the bottom of the sidebar rail above the account/sign-in footer.

**Architecture:** Keep the change scoped to the existing `QuoteAgentLaunchShell.razor` markup and `app.css` styling. Reuse existing `WorkspacePage.Settings`, `PageNavClass`, and `OpenSettingsPage` behavior so route state and active styling remain unchanged.

**Tech Stack:** Blazor WebAssembly, MudBlazor icons, CSS.

---

### Task 1: Move Settings into the rail footer

**Files:**
- Modify: `Maliev.QuoteEngine.Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor`

- [ ] **Step 1: Remove Settings from the primary nav**

Replace this block inside `.qe-agent-primary-nav`:

```razor
@if (IsSignedIn)
{
    <button type="button"
            class="@PageNavClass(WorkspacePage.Settings)"
            aria-current="@(IsWorkspacePage(WorkspacePage.Settings) ? "page" : null)"
            @onclick="OpenSettingsPage">
        <MudIcon Icon="@Icons.Material.Outlined.Settings" Size="Size.Small" />
        <span>@Text("Settings", "ตั้งค่า")</span>
    </button>
}
```

with no replacement, so `New project`, `Plugins`, `Projects`, and the search control remain in the primary nav.

- [ ] **Step 2: Add Settings to the rail footer above the account/sign-in footer**

Inside `.qe-agent-rail-foot`, before `.qe-agent-account-action`, add:

```razor
@if (IsSignedIn)
{
    <button type="button"
            class="@PageNavClass(WorkspacePage.Settings) qe-agent-rail-footer-settings"
            aria-current="@(IsWorkspacePage(WorkspacePage.Settings) ? "page" : null)"
            @onclick="OpenSettingsPage">
        <MudIcon Icon="@Icons.Material.Outlined.Settings" Size="Size.Small" />
        <span>@Text("Settings", "ตั้งค่า")</span>
    </button>
}
```

Expected result: Settings remains signed-in only, still uses the existing Settings page action and active class logic, but is visually placed at the bottom of the sidebar rail.

---

### Task 2: Style collapsed rail labels and footer Settings

**Files:**
- Modify: `Maliev.QuoteEngine.Client/wwwroot/css/app.css`

- [ ] **Step 1: Stop hiding primary nav labels in collapsed mode**

Replace the existing collapsed hidden selector:

```css
.qe-agent-shell--rail-collapsed .qe-agent-brand-wordmark,
.qe-agent-shell--rail-collapsed .qe-agent-primary-nav span,
.qe-agent-shell--rail-collapsed .qe-agent-rail-search,
.qe-agent-shell--rail-collapsed .qe-agent-account-action span:not(.qe-agent-account-dot),
.qe-agent-shell--rail-collapsed .qe-agent-signin-card {
    display: none;
}
```

with:

```css
.qe-agent-shell--rail-collapsed .qe-agent-brand-wordmark,
.qe-agent-shell--rail-collapsed .qe-agent-rail-search,
.qe-agent-shell--rail-collapsed .qe-agent-account-action span:not(.qe-agent-account-dot),
.qe-agent-shell--rail-collapsed .qe-agent-signin-card {
    display: none;
}
```

- [ ] **Step 2: Add collapsed icon-label styling for the main nav**

Add this after `.qe-agent-shell--rail-collapsed .qe-agent-primary-nav { padding-inline: 0; }`:

```css
.qe-agent-shell--rail-collapsed .qe-agent-primary-nav a,
.qe-agent-shell--rail-collapsed .qe-agent-primary-nav button {
    grid-template-columns: 1fr;
    grid-template-rows: auto auto;
    justify-items: center;
    gap: 3px;
    min-height: 54px;
    padding-inline: 0;
    font-size: 10px;
    line-height: 1.1;
    text-align: center;
}
```

This replaces the previous collapsed selector that set `grid-template-columns: 1fr; justify-items: center; padding-inline: 0;`.

- [ ] **Step 3: Add footer settings styling and collapsed footer settings styling**

Add after `.qe-agent-rail-foot` rules or near the collapsed rail rules:

```css
.qe-agent-rail-footer-settings {
    width: 100%;
}

.qe-agent-shell--rail-collapsed .qe-agent-rail-footer-settings {
    grid-template-columns: 1fr;
    grid-template-rows: auto auto;
    justify-items: center;
    gap: 3px;
    min-height: 54px;
    padding-inline: 0;
    font-size: 10px;
    line-height: 1.1;
    text-align: center;
}
```

Expected result: In collapsed mode, primary nav buttons and Settings show the icon centered above a compact label. In expanded mode, Settings looks like the other primary nav items but sits at the bottom of the rail.

---

### Task 3: Verify locally

**Commands:**
- `dotnet build Maliev.QuoteEngine.slnx --configuration Release`
- `dotnet run --project Maliev.QuoteEngine.Bff/Maliev.QuoteEngine.Bff.csproj`

- [ ] **Step 1: Build**

Run the Release build and confirm it completes without errors.

- [ ] **Step 2: Browser check**

Open the app, collapse the sidebar, and confirm:
- New project, Plugins, and Projects show icon with label below.
- Settings appears at the bottom of the sidebar rail above the account/sign-in footer.
- Settings remains active when the Settings workspace page is open.
- Expanding the sidebar restores the normal horizontal icon-label layout.

---

### Task 4: Commit the UI change

**Command:**

```bash
git add Maliev.QuoteEngine.Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor Maliev.QuoteEngine.Client/wwwroot/css/app.css
git commit -m "ui: collapse quote agent rail with icon labels"
```

Expected result: A focused commit containing only the sidebar rail layout change.
