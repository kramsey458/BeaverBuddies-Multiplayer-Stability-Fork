---
name: BeaverBuddies Stability Fork
description: Timberborn co-op, one colony built together; the project site drawn as a two-person crosscut saw on blued steel.
colors:
  ground: "#161b20"
  band: "#1c2329"
  plate: "#252e36"
  edge: "#3a454f"
  ink: "#eceeea"
  muted: "#aeb7bd"
  heartwood: "#1b1510"
  pine: "#e3c285"
  pine-deep: "#b98d4e"
  bark: "#4a3322"
  saw-steel: "#c7cdd2"
  red: "#b8322a"
  red-hi: "#cf3d33"
  link: "#8fc6e8"
  host: "#f0a04b"
  guest: "#6fb3ee"
  good: "#8fd08a"
  warn: "#f0c060"
  bad: "#ff7a6b"
typography:
  display:
    fontFamily: "Zilla Slab, Rockwell, Roboto Slab, Georgia, serif"
    fontSize: "clamp(2.5rem, 5.6vw, 4.1rem)"
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: "-0.005em"
  headline:
    fontFamily: "Zilla Slab, Rockwell, Roboto Slab, Georgia, serif"
    fontSize: "clamp(1.85rem, 3.6vw, 2.6rem)"
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: "-0.005em"
  page-title:
    fontFamily: "Zilla Slab, Rockwell, Roboto Slab, Georgia, serif"
    fontSize: "clamp(2.2rem, 5vw, 3.3rem)"
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: "-0.005em"
  title:
    fontFamily: "Zilla Slab, Rockwell, Roboto Slab, Georgia, serif"
    fontSize: "1.3rem"
    fontWeight: 700
    lineHeight: 1.1
  pitch:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif"
    fontSize: "clamp(1.25rem, 2.2vw, 1.5rem)"
    fontWeight: 400
    lineHeight: 1.4
  body:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif"
    fontSize: "17px"
    fontWeight: 400
    lineHeight: 1.65
  label:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif"
    fontSize: "1rem"
    fontWeight: 600
    lineHeight: 1
  chip:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif"
    fontSize: "0.8rem"
    fontWeight: 700
    lineHeight: 1
    letterSpacing: "0.03em"
  button:
    fontFamily: "Zilla Slab, Rockwell, Roboto Slab, Georgia, serif"
    fontSize: "1.12rem"
    fontWeight: 700
    lineHeight: 1
  mono:
    fontFamily: "ui-monospace, Cascadia Code, SF Mono, Consolas, Liberation Mono, monospace"
    fontSize: "0.87em"
rounded:
  focus: "3px"
  sm: "4px"
  md: "6px"
  lg: "8px"
  pill: "999px"
  round: "50%"
spacing:
  gutter: "clamp(16px, 4vw, 40px)"
  section: "clamp(56px, 8vw, 100px)"
  plate: "16px 18px"
  grid: "28px"
  column: "40px"
  tally-column: "48px"
  container: "1160px"
  measure: "70ch"
components:
  button-primary:
    backgroundColor: "{colors.red}"
    textColor: "#ffffff"
    typography: "{typography.button}"
    rounded: "{rounded.md}"
    padding: "0 24px"
    height: "50px"
  button-primary-hover:
    backgroundColor: "{colors.red-hi}"
    textColor: "#ffffff"
  button-secondary:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    typography: "{typography.button}"
    rounded: "{rounded.md}"
    padding: "0 24px"
    height: "50px"
  nav-link:
    backgroundColor: "transparent"
    textColor: "{colors.muted}"
    typography: "{typography.label}"
    rounded: "{rounded.sm}"
    padding: "0 12px"
    height: "44px"
  nav-link-hover:
    backgroundColor: "{colors.plate}"
    textColor: "{colors.ink}"
  nav-link-current:
    textColor: "{colors.ink}"
    rounded: "0"
  chip-confirmed:
    backgroundColor: "{colors.pine}"
    textColor: "{colors.heartwood}"
    typography: "{typography.chip}"
    rounded: "{rounded.pill}"
    padding: "0 9px"
    height: "24px"
  chip-tested:
    backgroundColor: "transparent"
    textColor: "{colors.muted}"
    typography: "{typography.chip}"
    rounded: "{rounded.pill}"
    padding: "0 9px"
    height: "24px"
  plate:
    backgroundColor: "{colors.plate}"
    textColor: "{colors.ink}"
    rounded: "{rounded.lg}"
    padding: "{spacing.plate}"
  question:
    backgroundColor: "{colors.plate}"
    textColor: "{colors.ink}"
    rounded: "{rounded.lg}"
    padding: "14px 52px 14px 18px"
    height: "52px"
  step-number:
    backgroundColor: "{colors.pine}"
    textColor: "{colors.heartwood}"
    rounded: "{rounded.round}"
    size: "44px"
  copy-button:
    backgroundColor: "{colors.plate}"
    textColor: "{colors.ink}"
    rounded: "{rounded.sm}"
    padding: "0 16px"
    height: "44px"
---

# Design System: BeaverBuddies Stability Fork

## Overview

**Creative North Star: "The Crosscut Saw Team"**

Two sawyers on one blade, pulling in rhythm; out of step, the saw binds. The site is that saw laid across a fresh-cut log at dusk: a blued-steel ground, fresh-cut pine for what is solid and played, and one competition red for the single thing to do, Download. The host's orange and the guest's blue live only on the two ends of the saw. It is dark only, chosen for evening co-op sessions, and loads nothing from elsewhere.

Density is that of a working guide, not a brochure. Content reads as ruled sheets: a tally of features split by hairlines, a scorecard with Confirmed in play beside Tested, steps read along a 3px rail like a cut line. Containers are flat plates distinguished by tone and a 1px inset hairline, never by floating shadows. The one moving thing is the saw in the hero, pulled back and forth in a slow loop with sawdust falling; everything else is still.

The world explicitly refuses the SaaS template the old site wore: eyebrow labels, stat strips, icon tiles and grids of identical cards.

**Key Characteristics:**
- Blued-steel tonal stack (ground, band, plate, edge) carries all depth; no light mode.
- Pine marks what is played, current and focused; red appears only on Download.
- Zilla Slab 700 for headings, the brand, buttons and step numbers; everything else in the system face.
- Ruled sheets (hairlines and 3px rules) instead of card grids.
- A small end-grain log round marks each section; the header mark and favicon are a log round with the saw across it.
- One signature motion (the saw pull), switched off under reduced motion.

## Colors

A cold, low-chroma blued-steel ground under warm pine and a single hot red, with the host/guest pair held back for the saw.

### Primary
- **Fresh-Cut Pine** (pine): the colour of the cut face. Confirmed chips, step numbers, the current-page nav underline, the Confirmed column's 3px rule, the 3px rule under guide page heads and over the closing section, the focus ring, text selection, the skip link, the FAQ chevron and hovered question, answer sub-heads, and the hero's "BeaverBuddies" line in the title.
- **Pine Heartwood** (pine-deep): the growth rings of the mark and the border of an open question. A quieter pine for "this is the one you opened".

### Secondary
- **Competition Red** (red, hover red-hi): the Download button and nothing else. White text on it.

### Tertiary
- **Host Orange** (host) and **Guest Blue** (guest): the two saw handles (hero, header mark, favicon). They are the game's own player colours and are defined as tokens, but the CSS never paints UI with them.
- **Saw Steel** (saw-steel): the blade in the mark, favicon and hero drawing. Not a UI surface colour.
- **Bark** (bark): the rim of the log round in the mark and favicon.

### Neutral
- **Blued-Steel Ground** (ground): page background, the header, the ring knocked out around step numbers; also the theme-color meta.
- **Band** (band): alternating section bands (with edge hairlines top and bottom), the footer, and the table of contents plate on narrow screens.
- **Plate** (plate): status plate, callouts, table wraps, FAQ questions, code and pre, Copy button, nav hover.
- **Edge** (edge): every 1px hairline and inset border; the 3px step rail and the Tested column rule; the outlined buttons' ring; scrollbar thumb.
- **Ink** (ink): body text and headings.
- **Muted Steel** (muted): secondary text (ledes, section intros, list descriptions, captions, footer, table headers, nav at rest), the Tested chip.
- **Heartwood Ink** (heartwood): text set on pine (chips, step numbers, selection, skip link).
- **Link Blue** (link): inline links, underlined 1px, 2px on hover.

### Status
- **Good / Warn / Bad** (good, warn, bad): only to name the in-game connection panel's own green, yellow and red where the copy describes them.

### Named Rules
**The Download Red Rule.** Competition red is spent on Download buttons only. A second red element anywhere on a page is a defect.

**The Two Handles Rule.** Host orange and guest blue belong to the two ends of the saw. They never tint text, borders, buttons or backgrounds.

**The Pine Is Solid Rule.** Pine means played, current or focused. What is only tested is muted steel, never pine.

## Typography

**Display Font:** Zilla Slab 600/700, self-hosted woff2 (SIL OFL), with Rockwell, Roboto Slab, Georgia
**Body Font:** system-ui (with -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial)
**Label/Mono Font:** labels use the body face; code uses ui-monospace (Cascadia Code, SF Mono, Consolas)

**Character:** a sturdy workshop slab for anything you would stencil on a tool, over a plain, fast system face for everything you read.

### Hierarchy
- **Display** (700, clamp(2.5rem, 5.6vw, 4.1rem), 1.1): the home title only. The product name "BeaverBuddies" sits inside the h1 as a smaller pine line (clamp(1.05rem, 1.8vw, 1.7rem)) over "Stability Fork"; it is part of the name, not a label.
- **Page title** (700, clamp(2.2rem, 5vw, 3.3rem), 1.1): guide page heads.
- **Headline** (700, clamp(1.85rem, 3.6vw, 2.6rem), 1.1): section h2s; inside guide prose clamp(1.7rem, 3.2vw, 2.2rem). Balanced wrapping.
- **Title** (700, 1.3rem, 1.1): h3s in steps, tally and prose (1.35rem default, 1.5rem for scorecard column heads, 1.2rem in compact steps).
- **Pitch** (400, clamp(1.25rem, 2.2vw, 1.5rem), 1.4, max 32ch): the hero's one-sentence promise.
- **Body** (400, 17px, 1.65; 16.5px under 640px): prose at a 70–74ch measure; ledes max 56ch in muted steel. Bold is 650.
- **Label** (600, 1rem): nav links, table heads (.92rem, muted), TOC heading (.95rem), Copy button (.95rem). Sentence case; nothing is uppercased.
- **Chip** (700, .8rem, .03em tracking): Confirmed / Tested chips.
- **Button** (Zilla Slab 700, 1.12rem): primary and secondary buttons.

### Named Rules
**The Slab Is Stencil Rule.** Zilla Slab is for headings, the brand, buttons and step numbers. Labels, tables and body stay in the system face.

**The Sentence Case Rule.** No uppercase, no tracked-out small labels. The only letter-spacing in the system is the chip's .03em.

## Layout

A single 1160px container with a fluid gutter (clamp(16px, 4vw, 40px)). Sections breathe at clamp(56px, 8vw, 100px) top and bottom and alternate between ground and band; a section head holds the log-round marker, the h2 and a muted intro (max 64ch, 32px below it).

- **Hero:** two columns (1.05fr / .95fr, gap clamp(28px, 5vw, 64px)), copy left, the saw figure right; stacks under 900px.
- **Steps:** three columns along a 3px edge rail with pine number discs sitting on it; under 900px the rail turns vertical at the left. A compact variant in the install guide numbers on the left with hairlines between steps.
- **Tally:** two ruled columns (gap 48px), each item under a 1px top hairline; one column under 900px.
- **Scorecard:** two columns (gap 40px), Confirmed in play and Covered by checks; on wide screens the Confirmed column stays sticky (top 88px) beside the longer Tested list; static and stacked under 900px.
- **Guide pages:** a page head (marker, title, muted intro, 3px pine rule below), then a 230px sticky table of contents beside prose (max 74ch) from 1000px; under that the TOC becomes a band plate with an auto-filling grid of links. Prose sections are 56px apart.
- **Closing:** a final section on the ground with a 3px pine rule above, not a card.
- **Footer:** band, three columns (1.4fr 1fr 1fr) to two to one.
- **Header:** sticky at 64px min on wide screens, static under 640px, where the nav takes the full width. Anchor targets clear it with 84px scroll padding.
- **Mobile tables** unstack into one block per row under 640px; buttons in a row go full width.

## Elevation & Depth

Flat and tonal. Depth comes from the steel stack (ground to band to plate) and from a 1px inset edge hairline on plates. Only two things cast a shadow, both soft and dark rather than coloured or offset.

### Shadow Vocabulary
- **Button lift** (`box-shadow: 0 10px 22px -12px rgba(0, 0, 0, .8)`): the primary Download button only.
- **Screenshot drop** (`box-shadow: 0 0 0 1px var(--edge), 0 24px 40px -20px rgba(0, 0, 0, .8)`): the real connection-panel screenshot.
- **Plate hairline** (`box-shadow: inset 0 0 0 1px var(--edge)`): status plate, callouts, table wraps, questions (pine-deep when open), TOC on narrow screens.
- **Outline ring** (`box-shadow: inset 0 0 0 1.5px var(--edge)`): secondary button, GitHub nav link, Copy button; the ring turns pine on hover.
- **Knock-out ring** (`box-shadow: 0 0 0 5px var(--ground)` or `var(--band)`): lifts step number discs off the rail in the section's own background.

### Named Rules
**The Plate Not Card Rule.** Containers are flat plates with an inset hairline. No drop shadows on plates, no hard offset shadows, no glow.

## Shapes

Gently squared. Radii step 3px (focus ring), 4px (nav, code, Copy, skip), 6px (buttons, pre), 8px (plates, questions, table wraps, screenshot). Round forms are reserved for things that are round in the world: the pill chips, the step discs (50%), the log rounds. Rules come in two weights: 1px edge hairlines divide, 3px rules emphasise (pine under the Confirmed head, under guide page heads and over the closing; edge for the step rail and the Tested head). The current nav link is marked by a 3px pine underline with its radius removed.

## Components

### Buttons
Solid, slab-lettered and chunky enough to press with a thumb.
- **Shape:** gently squared (6px), 50px tall, 24px side padding, icon at 1.15em with a 10px gap.
- **Primary:** competition red with white slab text and the button-lift shadow. Download only, carrying the version.
- **Secondary:** transparent with a 1.5px edge ring and ink text; ring turns pine on hover.
- **Hover / Focus:** both lift 1px (transform .15s cubic-bezier(.2, .7, .2, 1)); primary deepens to red-hi. Focus is the global 3px pine outline, 3px offset.

### Chips
- **Style:** pills 24px tall, 9px padding, 700 .8rem with .03em tracking.
- **State:** Confirmed is solid pine with heartwood text; Tested is a 1.5px muted ring with muted text. Chips can carry a count ("Confirmed · 4").

### Cards / Containers (plates)
- **Corner Style:** 8px.
- **Background:** plate.
- **Shadow Strategy:** inset 1px edge hairline only (see Elevation).
- **Internal Padding:** 16px 18px.
- Used for the hero status plate, callouts, table wraps. Callouts carry no label or kicker; they open with a bold lead sentence.

### Questions (FAQ and Troubleshooting)
A plate that opens. Summary in system 650 1.05rem, 52px min height, a pine chevron drawn from two 2.5px borders that turns from down to up (.2s). Hover turns the summary pine; open swaps the hairline to pine-deep. Answers may use pine sub-heads (650 .95rem) that name the block after them ("Why", "Try this"); a table inside an answer drops its plate for a hairline above.

### Navigation
System 600 1rem links, 44px tall, muted at rest, ink on a plate background on hover. Current page: ink with a 3px pine underline. The GitHub link sits last with a 1.5px edge ring. The brand is the 38px log-and-saw mark beside "BeaverBuddies" in slab 700 with "Stability Fork" below in muted system 600.

### Table of contents
Muted links 40px tall (44px on narrow screens) with a plate hover; sticky beside prose on wide screens, a band plate grid on narrow ones.

### Copy path
A code field (flex, 10px 12px padding) with a Copy button (plate, 1.5px edge ring, pine on hover) that reads "Copied" for 1.6s. The button hides itself when the clipboard API is missing.

### Section marker
A 44 by 38px end-grain log round (procedural webp) above each section's h2 and each guide page title. It is the only thing that sits above a heading.

### The Saw (signature)
The hero figure: a procedural end-grain log round (webp) with a two-person crosscut saw behind it, so only the toothed blade ends and the orange (host) and blue (guest) handles show. The blade group slides 24px each way on a 3.2s cubic-bezier(.45, 0, .55, 1) loop and six sawdust flecks fall and fade on the same beat. Both animations exist only under prefers-reduced-motion: no-preference; reduced motion also turns off every transition and smooth scrolling. The header mark and favicon are the same idea at 64 units: pine round, bark rim, pine-deep rings, steel blade, orange and blue handles.

## Do's and Don'ts

### Do:
- **Do** build depth from the steel stack (ground, band, plate) and a 1px inset edge hairline.
- **Do** read lists as ruled sheets: hairline-separated items, 3px rules for emphasis.
- **Do** mark played and confirmed things in pine and tested-only things in muted steel, side by side.
- **Do** put the end-grain log round above section and page headings as the marker.
- **Do** keep the saw the only moving element, and keep it off under reduced motion.
- **Do** keep touch targets at 44px or more (nav, TOC on mobile, footer links, Copy) and the 3px pine focus ring.
- **Do** self-host every font and image; the site loads nothing from elsewhere.

### Don't:
- **Don't** use competition red for anything but Download.
- **Don't** paint UI with host orange or guest blue; they are the saw's handles.
- **Don't** add eyebrow or kicker labels above headings, or label callouts; the log round is the only thing above a heading.
- **Don't** build stat strips, icon tiles or grids of identical cards.
- **Don't** put drop shadows on plates, or use hard offset shadows or glows.
- **Don't** uppercase or track out labels.
- **Don't** add a light theme.
- **Don't** use Timberborn's official logos or key art, or the original project's art as this fork's brand.
