#!/usr/bin/env python3
"""Capture and validate QuoteEngine ProjectNew UI parity against Intranet.

The gate is intentionally visual-metric based: it stores screenshots for review
and fails on layout regressions that were repeatedly visible in browser review.

Reference authentication:
  Set MALIEV_INTRANET_BEARER or pass --intranet-bearer-token when the Intranet
  page requires a bearer token. The token is never written to the summary.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any

from playwright.sync_api import BrowserContext, Page, TimeoutError, sync_playwright


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SAMPLE = REPO_ROOT / "Maliev.QuoteEngine.Client" / "wwwroot" / "samples" / "maliev-sample-bracket.step"

SELECTORS: dict[str, str] = {
    "root": ".qe-pn-root, .pn-root",
    "body": ".qe-pn-body, .pn-body",
    "topbar": ".quote-topbar, .top-row, .topbar",
    "leftPanel": ".qe-plp-root, .plp-root",
    "center": ".qe-pn-center, .pn-center",
    "rightPanel": ".qe-pcs-sidebar, .pcs-sidebar",
    "summary": ".qe-qsb-root, .qsb-root",
    "dropzone": ".qe-dropzone--hero, .pn-empty-dropzone, .plp-dropzone",
    "tabs": ".qe-pdc-tabs, .pdc-tabs",
    "viewerToolbar": ".qe-viewer-toolbar, .pv-toolbar",
    "dfmPanel": ".qe-dfm-tab, .dfm-tab",
    "bulkTable": ".qe-pbt-root, .pbt-root",
    "launchHero": ".qe-launch-hero",
    "launchVisual": ".qe-launch-visual img",
    "demoCard": ".qe-demo-sample-card",
    "launchAccountCard": ".qe-launch-account-card",
    "launchSignIn": ".qe-launch-signin",
    "launchAssistant": ".qe-launch-assistant",
    "launchBenefits": ".qe-launch-benefits",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--quote-url", default="http://localhost:5112/projects/new")
    parser.add_argument("--intranet-url", default="http://localhost:5071/sales/projects/new")
    parser.add_argument("--sample-file", type=Path, default=DEFAULT_SAMPLE)
    parser.add_argument("--output", type=Path, default=REPO_ROOT / "artifacts" / "ui-parity" / time.strftime("%Y%m%d-%H%M%S"))
    parser.add_argument("--intranet-bearer-token", default=os.environ.get("MALIEV_INTRANET_BEARER"))
    parser.add_argument("--quote-bearer-token", default=os.environ.get("MALIEV_QUOTEENGINE_BEARER"))
    parser.add_argument("--skip-reference", action="store_true", help="Capture QuoteEngine only.")
    parser.add_argument("--headed", action="store_true", help="Run Chromium headed for local debugging.")
    return parser.parse_args()


def context_for(browser: Any, token: str | None, viewport: tuple[int, int]) -> BrowserContext:
    headers = {"Authorization": f"Bearer {token}"} if token else None
    return browser.new_context(
        viewport={"width": viewport[0], "height": viewport[1]},
        device_scale_factor=1,
        extra_http_headers=headers,
        ignore_https_errors=True,
    )


def apply_theme(page: Page, theme: str) -> None:
    page.evaluate(
        """theme => {
            localStorage.setItem('maliev.quote.theme', theme);
            localStorage.setItem('maliev_theme', theme);
            document.cookie = `maliev_theme=${theme}; path=/; SameSite=Lax`;
            document.documentElement.setAttribute('data-maliev-theme', theme);
        }""",
        theme,
    )
    page.reload(wait_until="domcontentloaded")
    wait_for_ui(page)


def apply_theme_in_place(page: Page, theme: str) -> None:
    toggle = page.locator(".quote-theme-toggle").first
    try:
        if toggle.count() > 0 and toggle.is_visible(timeout=350):
            current = page.evaluate("document.documentElement.getAttribute('data-maliev-theme') || 'light'")
            if current != theme:
                toggle.click(timeout=2_000)
                page.wait_for_timeout(600)
                return
    except Exception:
        pass

    page.evaluate(
        """theme => {
            localStorage.setItem('maliev.quote.theme', theme);
            localStorage.setItem('maliev_theme', theme);
            document.cookie = `maliev_theme=${theme}; path=/; SameSite=Lax`;
            document.documentElement.setAttribute('data-maliev-theme', theme);
        }""",
        theme,
    )
    page.wait_for_timeout(300)


def wait_for_ui(page: Page) -> None:
    page.wait_for_load_state("domcontentloaded", timeout=20_000)
    try:
        page.wait_for_load_state("networkidle", timeout=6_000)
    except TimeoutError:
        pass

    try:
        page.locator(SELECTORS["root"]).first.wait_for(state="attached", timeout=20_000)
    except TimeoutError:
        pass

    page.wait_for_timeout(600)


def safe_box(page: Page, selector: str) -> dict[str, float] | None:
    locator = page.locator(selector).first
    try:
        if locator.count() == 0 or not locator.is_visible(timeout=350):
            return None
        box = locator.bounding_box(timeout=350)
    except Exception:
        return None

    if box is None:
        return None

    return {key: round(float(value), 2) for key, value in box.items()}


def safe_styles(page: Page) -> dict[str, Any]:
    return page.evaluate(
        """() => {
            const read = (selector, properties) => {
                const element = document.querySelector(selector);
                if (!element) {
                    return null;
                }

                const styles = getComputedStyle(element);
                return Object.fromEntries(properties.map(property => [property, styles.getPropertyValue(property)]));
            };

            return {
                logo: read('.quote-brand-logo', ['filter', 'opacity']),
                activeTab: read('.quote-topnav a.active', ['color', 'background-color', 'box-shadow']),
                themeToggle: read('.quote-theme-toggle', [
                    'width',
                    'height',
                    'padding-top',
                    'padding-right',
                    'padding-bottom',
                    'padding-left',
                    'background-color',
                    'box-shadow'
                ]),
                topActions: read('.top-actions', ['gap', 'align-items']),
                topActionItems: Array.from(document.querySelectorAll('.top-actions > *')).map(element => {
                    const box = element.getBoundingClientRect();
                    return {
                        className: element.className,
                        tagName: element.tagName,
                        text: element.textContent.trim(),
                        x: Math.round(box.x),
                        width: Math.round(box.width)
                    };
                })
            };
        }"""
    )


def capture(page: Page, output: Path, app: str, label: str, viewport: tuple[int, int], records: list[dict[str, Any]]) -> None:
    output.mkdir(parents=True, exist_ok=True)
    screenshot = output / f"{app}-{label}-{viewport[0]}x{viewport[1]}.png"
    page.screenshot(path=str(screenshot), full_page=True)

    metrics = {
        "url": page.url,
        "viewport": {"width": viewport[0], "height": viewport[1]},
        "documentWidth": page.evaluate("Math.max(document.documentElement.scrollWidth, document.body.scrollWidth)"),
        "bodyHeight": page.evaluate("Math.max(document.documentElement.scrollHeight, document.body.scrollHeight)"),
        "boxes": {name: safe_box(page, selector) for name, selector in SELECTORS.items()},
        "styles": safe_styles(page),
    }
    metrics["horizontalOverflow"] = metrics["documentWidth"] > viewport[0] + 2
    records.append({"app": app, "label": label, "screenshot": str(screenshot), "metrics": metrics})


def set_upload_file(page: Page, sample_file: Path) -> bool:
    file_inputs = page.locator("input[type=file]")
    if file_inputs.count() == 0:
        return False

    file_inputs.first.set_input_files(str(sample_file))
    try:
        page.locator(".qe-plp-part, .plp-part").first.wait_for(state="visible", timeout=20_000)
    except TimeoutError:
        pass
    page.wait_for_timeout(3_000)
    return True


def click_named_button(page: Page, pattern: str) -> None:
    regex = re.compile(pattern, re.I)
    for role in ("button", "tab"):
        locator = page.get_by_role(role, name=regex).first
        try:
            if locator.count() > 0 and locator.is_visible(timeout=350):
                locator.click(timeout=2_000)
                page.wait_for_timeout(600)
                return
        except Exception:
            continue

    text_locator = page.locator(f"text=/{pattern}/i").first
    try:
        if text_locator.count() > 0:
            text_locator.click(timeout=2_000)
            page.wait_for_timeout(600)
    except Exception:
        pass


def capture_flow(
    browser: Any,
    app: str,
    url: str,
    token: str | None,
    sample_file: Path,
    output: Path,
    records: list[dict[str, Any]],
    failures: list[str],
) -> None:
    for viewport_name, viewport in (("desktop", (1366, 768)), ("mobile", (390, 844))):
        context = context_for(browser, token, viewport)
        page = context.new_page()
        page.goto(url, wait_until="domcontentloaded", timeout=45_000)
        wait_for_ui(page)
        capture(page, output, app, f"empty-light-{viewport_name}", viewport, records)

        apply_theme(page, "dark")
        capture(page, output, app, f"empty-dark-{viewport_name}", viewport, records)

        if viewport_name == "desktop":
            apply_theme(page, "light")
            uploaded = set_upload_file(page, sample_file)
            if not uploaded:
                failures.append(f"{app}: no file input found for upload flow")
            capture(page, output, app, "uploaded-model-light-desktop", viewport, records)

            click_named_button(page, "DFM")
            capture(page, output, app, "dfm-light-desktop", viewport, records)

            click_named_button(page, "All parts|Table")
            capture(page, output, app, "table-light-desktop", viewport, records)

            click_named_button(page, "3D Model")
            apply_theme_in_place(page, "dark")
            capture(page, output, app, "uploaded-dark-desktop", viewport, records)

        context.close()


def record_by(records: list[dict[str, Any]], app: str, label: str) -> dict[str, Any] | None:
    return next((record for record in records if record["app"] == app and record["label"] == label), None)


def height(record: dict[str, Any], key: str) -> float | None:
    box = record["metrics"]["boxes"].get(key)
    return None if box is None else float(box["height"])


def width(record: dict[str, Any], key: str) -> float | None:
    box = record["metrics"]["boxes"].get(key)
    return None if box is None else float(box["width"])


def px(value: Any) -> float | None:
    if value is None:
        return None

    match = re.match(r"^\s*(-?\d+(?:\.\d+)?)", str(value))
    return None if match is None else float(match.group(1))


def rgb_tuple(value: Any) -> tuple[int, int, int] | None:
    if value is None:
        return None

    numbers = re.findall(r"[\d.]+", str(value))
    if len(numbers) < 3:
        return None

    return tuple(max(0, min(255, int(float(number)))) for number in numbers[:3])  # type: ignore[return-value]


def contrast_ratio(foreground: Any, background: Any) -> float | None:
    fg = rgb_tuple(foreground)
    bg = rgb_tuple(background)
    if fg is None or bg is None:
        return None

    def luminance(rgb: tuple[int, int, int]) -> float:
        channels = []
        for channel in rgb:
            value = channel / 255
            channels.append(value / 12.92 if value <= 0.03928 else ((value + 0.055) / 1.055) ** 2.4)

        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2]

    darker, lighter = sorted((luminance(fg), luminance(bg)))
    return (lighter + 0.05) / (darker + 0.05)


def assert_quoteengine(records: list[dict[str, Any]], failures: list[str]) -> None:
    for record in [item for item in records if item["app"] == "quoteengine"]:
        if record["metrics"]["horizontalOverflow"]:
            failures.append(f"{record['label']}: horizontal overflow")

    empty_desktop = record_by(records, "quoteengine", "empty-light-desktop")
    if empty_desktop:
        if empty_desktop["metrics"]["boxes"].get("launchHero") is None:
            failures.append("empty desktop: manufacturing quote hero missing")
        if empty_desktop["metrics"]["boxes"].get("launchVisual") is None:
            failures.append("empty desktop: floating metal component visual missing")
        if empty_desktop["metrics"]["boxes"].get("demoCard") is None:
            failures.append("empty desktop: demo sample card missing for anonymous ProjectNew")
        if empty_desktop["metrics"]["boxes"].get("launchAccountCard") is None:
            failures.append("empty desktop: sign-in/help card missing for anonymous ProjectNew")
        if empty_desktop["metrics"]["boxes"].get("launchSignIn") is None:
            failures.append("empty desktop: launch sign-in CTA missing")
        if empty_desktop["metrics"]["boxes"].get("launchAssistant") is None:
            failures.append("empty desktop: launch assistant CTA missing")
        if empty_desktop["metrics"]["boxes"].get("launchBenefits") is None:
            failures.append("empty desktop: benefit row missing")
        dropzone_height = height(empty_desktop, "dropzone")
        if dropzone_height is not None and not 160 <= dropzone_height <= 240:
            failures.append(f"empty desktop: hero dropzone height is outside launch-card range ({dropzone_height}px)")

    empty_mobile = record_by(records, "quoteengine", "empty-light-mobile")
    if empty_mobile:
        if empty_mobile["metrics"]["boxes"].get("launchHero") is None:
            failures.append("empty mobile: manufacturing quote hero missing")
        if empty_mobile["metrics"]["boxes"].get("demoCard") is None:
            failures.append("empty mobile: demo sample card missing for anonymous ProjectNew")
        if empty_mobile["metrics"]["boxes"].get("launchAccountCard") is None:
            failures.append("empty mobile: sign-in/help card missing for anonymous ProjectNew")
        topbar_height = height(empty_mobile, "topbar")
        if topbar_height is not None and topbar_height > 130:
            failures.append(f"empty mobile: topbar is too tall ({topbar_height}px)")

    empty_dark_desktop = record_by(records, "quoteengine", "empty-dark-desktop")
    if empty_dark_desktop:
        styles = empty_dark_desktop["metrics"].get("styles", {})

        logo_styles = styles.get("logo") or {}
        logo_filter = str(logo_styles.get("filter", "")).strip().lower()
        if logo_filter in {"", "none"}:
            failures.append("empty dark desktop: MALIEV logo has no dark-mode contrast filter")

        active_styles = styles.get("activeTab") or {}
        active_contrast = contrast_ratio(active_styles.get("color"), active_styles.get("background-color"))
        if active_contrast is not None and active_contrast < 4.5:
            failures.append(f"empty dark desktop: active Quote tab contrast is too low ({active_contrast:.2f}:1)")

        theme_styles = styles.get("themeToggle") or {}
        theme_width = px(theme_styles.get("width"))
        theme_height = px(theme_styles.get("height"))
        theme_shadow = str(theme_styles.get("box-shadow", "")).strip().lower()
        if theme_width is not None and theme_width != 40:
            failures.append(f"empty dark desktop: theme toggle width drifted ({theme_width}px)")
        if theme_height is not None and theme_height != 40:
            failures.append(f"empty dark desktop: theme toggle height drifted ({theme_height}px)")
        if theme_shadow not in {"none", ""}:
            failures.append("empty dark desktop: theme toggle shows a default border/ring")

        top_action_styles = styles.get("topActions") or {}
        top_action_gap = px(top_action_styles.get("gap"))
        if top_action_gap is not None and not 6 <= top_action_gap <= 10:
            failures.append(f"empty dark desktop: topbar action gap drifted ({top_action_gap}px)")

        viewport_width = int(empty_dark_desktop["metrics"]["viewport"]["width"])
        for item in styles.get("topActionItems") or []:
            right_edge = int(item.get("x", 0)) + int(item.get("width", 0))
            if right_edge > viewport_width:
                failures.append(f"empty dark desktop: topbar action overflows viewport ({item.get('className', '')})")

            if "quote-currency-select" in str(item.get("className", "")) and int(item.get("width", 0)) > 120:
                failures.append(f"empty dark desktop: currency select is too wide ({item.get('width')}px)")

    uploaded = record_by(records, "quoteengine", "uploaded-model-light-desktop")
    if uploaded:
        toolbar_height = height(uploaded, "viewerToolbar")
        if toolbar_height is not None and toolbar_height > 58:
            failures.append(f"uploaded model: CAD toolbar wraps vertically ({toolbar_height}px)")

        summary_height = height(uploaded, "summary")
        if summary_height is not None and not 86 <= summary_height <= 132:
            failures.append(f"uploaded model: summary bar height drifted ({summary_height}px)")

        left_width = width(uploaded, "leftPanel")
        if left_width is not None and not 240 <= left_width <= 320:
            failures.append(f"uploaded model: parts panel width drifted ({left_width}px)")

        right_width = width(uploaded, "rightPanel")
        if right_width is not None and not 340 <= right_width <= 430:
            failures.append(f"uploaded model: config panel width drifted ({right_width}px)")

    uploaded_dark = record_by(records, "quoteengine", "uploaded-dark-desktop")
    if uploaded_dark:
        if uploaded_dark["metrics"]["boxes"].get("leftPanel") is None:
            failures.append("uploaded dark desktop: uploaded workspace was lost before dark-mode capture")
        if uploaded_dark["metrics"]["boxes"].get("summary") is None:
            failures.append("uploaded dark desktop: summary bar missing")


def assert_reference_comparison(records: list[dict[str, Any]], failures: list[str]) -> None:
    reference = record_by(records, "intranet", "uploaded-model-light-desktop")
    quote = record_by(records, "quoteengine", "uploaded-model-light-desktop")
    if not reference or not quote:
        return

    for metric_name, tolerance in (("leftPanel", 50), ("rightPanel", 80), ("summary", 45)):
        ref_width = width(reference, metric_name)
        quote_width = width(quote, metric_name)
        if ref_width is not None and quote_width is not None and abs(ref_width - quote_width) > tolerance:
            failures.append(
                f"comparison: {metric_name} width differs by {abs(ref_width - quote_width):.0f}px "
                f"(reference {ref_width}px, quote {quote_width}px)"
            )

    ref_summary_height = height(reference, "summary")
    quote_summary_height = height(quote, "summary")
    if (
        ref_summary_height is not None
        and quote_summary_height is not None
        and abs(ref_summary_height - quote_summary_height) > 45
    ):
        failures.append(
            f"comparison: summary height differs by {abs(ref_summary_height - quote_summary_height):.0f}px "
            f"(reference {ref_summary_height}px, quote {quote_summary_height}px)"
        )


def main() -> int:
    args = parse_args()
    if not args.sample_file.exists():
        print(f"Sample file not found: {args.sample_file}", file=sys.stderr)
        return 2

    args.output.mkdir(parents=True, exist_ok=True)
    records: list[dict[str, Any]] = []
    failures: list[str] = []

    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=not args.headed)
        if not args.skip_reference:
            capture_flow(
                browser,
                "intranet",
                args.intranet_url,
                args.intranet_bearer_token,
                args.sample_file,
                args.output,
                records,
                failures,
            )

        capture_flow(
            browser,
            "quoteengine",
            args.quote_url,
            args.quote_bearer_token,
            args.sample_file,
            args.output,
            records,
            failures,
        )
        browser.close()

    assert_quoteengine(records, failures)
    if not args.skip_reference:
        assert_reference_comparison(records, failures)

    summary = {"output": str(args.output), "screenshots": records, "failures": failures}
    summary_path = args.output / "summary.json"
    summary_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")

    print(f"UI parity screenshots: {args.output}")
    print(f"UI parity summary: {summary_path}")
    if failures:
        print("UI parity gate failed:", file=sys.stderr)
        for failure in failures:
            print(f" - {failure}", file=sys.stderr)
        return 1

    print("UI parity gate passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
