---
name: AI RAM Guard website
description: Existing AI RAM Guard identity as implemented on the download website.
colors:
  brand: "#ba01c4"
  link: "#ed92f1"
  bg: "#121117"
  surface: "#1c1a23"
  ink: "#f3effa"
  muted: "#b5afbf"
  line: "#393342"
typography:
  headline:
    fontFamily: '"Segoe UI", -apple-system, BlinkMacSystemFont, Arial, sans-serif'
    fontSize: "23px"
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: "-0.015em"
  action:
    fontFamily: '"Segoe UI", -apple-system, BlinkMacSystemFont, Arial, sans-serif'
    fontSize: "16px"
    fontWeight: 600
  instruction:
    fontFamily: '"Segoe UI", -apple-system, BlinkMacSystemFont, Arial, sans-serif'
    fontSize: "15px"
    lineHeight: 1.65
  quiet:
    fontFamily: '"Segoe UI", -apple-system, BlinkMacSystemFont, Arial, sans-serif'
    fontSize: "13px"
    lineHeight: 1.6
rounded:
  download: "8px"
  language: "6px"
spacing:
  compact: "8px"
  inline: "12px"
  text: "16px"
  control: "20px"
  group: "24px"
  section: "32px"
  section-mobile: "40px"
  section-desktop: "64px"
components:
  button-primary:
    backgroundColor: "{colors.brand}"
    textColor: "#fff"
    typography: "{typography.action}"
    rounded: "{rounded.download}"
    padding: "12px 20px"
  button-secondary:
    backgroundColor: "transparent"
    textColor: "#fff"
    typography: "{typography.action}"
    rounded: "{rounded.download}"
    padding: "12px 20px"
  language:
    backgroundColor: "transparent"
    textColor: "{colors.muted}"
    rounded: "{rounded.language}"
    padding: "10px"
  language-selected:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.language}"
    padding: "10px"
  beta:
    backgroundColor: "transparent"
    textColor: "{colors.muted}"
    padding: "0 7px"
---

# Design System: AI RAM Guard website

## Overview

**Creative North Star: "AI RAM Guard's existing app identity"**

This records the shipped website, not the whole native app. It inherits the existing logo, Byens IT attribution, dark neutral canvas and magenta action color. The confirmed direction is an extension of that identity; no replacement brand was created.

The implementation uses clear text hierarchy, open spacing and thin dividers. The download page composition and installation story remain in the surface brief rather than becoming requirements for every future screen.

**Key Characteristics:**
- Dark neutral canvas with one magenta action accent.
- Readable utility typography and restrained rounded controls.
- Flat, divider-led groups with visible focus treatment.

Evidence: `site/style.css`, `site/index.html`, `site/app.js`; checked against `.impeccable/review/desktop-full.png` and `mobile-full.png`. Product identity comes from `PRODUCT.md`; surface composition comes from `.impeccable/surface-briefs/download.md`.

## Colors

The primary magenta sits against slightly violet dark neutrals; a lighter magenta makes text links distinct.

### Primary
- **Brand magenta** (`brand`): filled primary actions and the inherited identity.
- **Light magenta** (`link`): links and keyboard focus outlines.

### Neutral
- **Canvas** (`bg`): the page background.
- **Raised neutral** (`surface`): selected language control and secondary-action hover.
- **Main ink** (`ink`): headings and primary content.
- **Muted ink** (`muted`): requirements, supporting text and inactive language controls.
- **Divider** (`line`): section rules, status badge and screenshot border.

**The Action Accent Rule.** Use the brand fill for the primary action; supporting links use the lighter link color.

## Typography

The implemented interface stack is Segoe UI with Apple system, BlinkMacSystemFont, Arial and sans-serif fallbacks. The recurring ramp is functional rather than a fixed ratio: section headlines, action labels, instructions and quiet supporting text are recorded above.

Section headlines repeat across platform, preview and help sections. Instructions use the recorded instruction role; expanded help text uses the same size with a slightly looser line-height (1.7). Requirements and secondary navigation use smaller text (14px). Native disclosure summaries use medium-weight text (16px desktop; 15px mobile).

The current page heading is observed at `clamp(36px, 5.3vw, 60px)`, weight 650, line-height 1.08 and tracking -0.03em. The lead is 20px with line-height 1.55, reduced to 18px on mobile. These single-page treatments are recorded as observations, not reusable type tokens.

Not canonized: the inherited system-font display heading remains a local exception in this scoped extension; it does not establish a display-face rule for future surfaces.

## Layout

Header, main and footer share a centered container with a maximum width (1000px) and desktop side gutters (32px). Recurring spacing uses the frontmatter steps; larger content sections are separated by the desktop section step.

At viewport widths up to and including 640px, gutters become 20px and major section separation becomes the mobile section step. The download area changes from two equal columns to one; its vertical separator becomes a horizontal rule. Download actions fill their column width. The screenshot scales proportionally, and its heading/caption row becomes stacked.

These are observed website behaviors, not a requirement that other product surfaces use two columns. Supporting text stays close to its action; help answers have a maximum readable measure (75ch).

## Elevation & Depth

The website uses no box shadows. Depth comes from the canvas/surface contrast, thin borders and open spacing. Hover states change color without a lift or animation.

**The Flat Surface Rule.** Preserve flat resting surfaces and use the existing neutral border and tone vocabulary to separate groups.

Smooth anchor scrolling is the only authored motion behavior. Reduced-motion preference restores automatic scrolling; there are no authored transition durations.

## Shapes

Download controls have gently rounded corners (8px), language controls tighter corners (6px), and the small status badge tighter still (5px). The screenshot has a soft frame (12px). These component-specific values are not a universal radius scale.

Borders are thin (1px). Download glyphs are inline stroked SVG, not text glyph icons. The native disclosure marker remains a browser control affordance.

## Components

### Buttons

Download links are substantial rectangular controls with the action typography, recorded padding and minimum height (52px). The primary is brand-filled with white text; its hover fill and border darken to the implemented state color (`#9c00a6`). The secondary has a transparent fill and subdued border (`#65586e`); its hover uses the surface fill and link-colored border.

Platform detection promotes the Mac action on Mac and renders the Windows action as outlined; the default promotes Windows. This platform selection is page behavior, not a universal primary-button rule.

Keyboard focus on links, buttons and disclosure summaries uses a visible outline (3px) in the link color, offset from the control (5px). States change immediately.

### Chips

The Beta badge is a quiet, noninteractive status label: muted text, transparent fill, thin divider-colored border, compact padding and the component corner shape above. It is not a filter or action.

### Navigation

The language selector uses native buttons with an `aria-pressed` state. Selected language gets a neutral surface fill and main ink; inactive language is muted. Hover text turns white. At the mobile breakpoint, padding tightens to 9px 7px and type to 13px. The brand stays at the opposite edge of the same header row.

### Disclosure rows

Help uses native `details` and `summary` with horizontal dividers, generous summary padding (20px vertically) and no card fill. Expanded answers use muted text and normal links. The Mac approval link opens and targets the matching disclosure.

### Image frame

The supplied Windows screenshot keeps its aspect ratio, thin neutral border and soft component corners. Its product UI is evidence imagery, not a source for additional website tokens.

## Do's and Don'ts

### Do:
- **Do** preserve the supplied logo, AI RAM Guard identity and Byens IT attribution.
- **Do** use the Action Accent Rule and visible keyboard focus.
- **Do** retain the website's stacked mobile behavior and proportionally scaled imagery.
- **Do** keep supporting text legible against the dark canvas.

### Don't:
- **Don't** turn this page's column count or content order into a system-wide layout rule.
- **Don't** infer website tokens from colors embedded inside the app screenshot.
- **Don't** promote the inherited display-font exception into a reusable display rule.

