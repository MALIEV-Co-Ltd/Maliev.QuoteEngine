# MALIEV Editorial Design System

This repository follows MALIEV's editorial social direction: white space, black ink, disciplined typography, and product-visible interfaces. The system is intentionally not Vercel, Apple, or any external brand reference.

## Color

- Paper: `#ffffff` for primary app surfaces and content panels.
- Soft paper: `#f7f7f4` only for subtle page separation when pure white needs depth.
- Ink: `#333333` for headings, body copy, primary controls, icons, and dark fills.
- Muted ink: `#555555` for secondary copy.
- Hairline: `rgba(51, 51, 51, .18)` for borders and separators.
- Focus: a visible `#333333` outline or a translucent `rgba(51, 51, 51, .22)` ring.
- Status colors are reserved for real system states only. Do not tint whole panels blue, purple, beige, or gradient.

## Typography

- English: `Inter`, `Arial`, or the existing MALIEV system sans stack.
- Thai: `Noto Sans Thai` wherever Thai text is rendered.
- Use weight 600 for page and dialog headings, 400-500 for body/UI copy, and 650 only where the existing component system requires it.
- Letter spacing is `0`. Do not tighten headings or utility text.
- Body line height should read editorially: 1.45-1.6 for prose and 1.2-1.35 for compact controls.

## Components

- Dialogs and account surfaces use white panels, `#333333` text, `#333333` borders, and 8px radius.
- Primary buttons use `#333333` fill with white text.
- Secondary buttons use white fill, `#333333` text, and a `#333333` or hairline border.
- Inputs use white fill, `#333333` text, clear labels, and high-contrast placeholders.
- Cards are for repeated items and framed tools only. Do not nest cards inside cards.
- Avoid decorative gradients, tinted modal fills, large blue panels, glow effects, and low-contrast gray-on-blue treatments.

## Layout

- Preserve real workspace density. Settings, Plugins, and quote tools should start close enough to the top of the usable content area that the page feels active, not like a landing page.
- Use white space as quiet editorial margin, not empty dead area.
- First screen in QuoteEngine is always the quote workflow, not a marketing hero.
- Keep controls stable across desktop and mobile; text must not overflow buttons, rails, dialogs, or cards.

## Imagery And Brand

- The MALIEV wordmark/logo must use official assets.
- Customer-visible media must show the real product, part, file, drawing, quote, or workflow state where possible.
- Editorial social creative uses the same black/white direction with generous quiet space and Thai text rendered through a shaping-aware path.

## Application Rule

QuoteEngine UI should look like a working manufacturing studio: practical, quiet, high contrast, and typographically precise. If a component starts to look like a blue SaaS modal, a gradient landing page, or a borrowed design-system demo, bring it back to white, `#333333`, and clear product workflow.
