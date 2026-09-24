# CLAUDE.md

BeaverBuddies Stability Fork: a Timberborn co-op mod, an independent fork of thomaswp's BeaverBuddies (GPL-3.0). The
mod is in `BeaverBuddies/` (+ `TimberNet/`), tests in `StabilityTests/` (CI) and `RuntimeChecks/` (local, needs the
game's assemblies), the website in `docs/`. Every change lands on `main` through a PR → merge.

## Standing rules

- Never launch or drive Timberborn, and never touch installed mods or saves. The maintainer (Kyler) playtests himself.
- Commit on a branch and open a PR. Kyler has said to merge PRs automatically: merge, then check the page live.
- The fork is **feature-complete**: fixes and new Timberborn versions only. New features go into BeaverBuddies
  Timber Together (https://github.com/timbermods/TimberTogether); point there, never sell against it.
- Assume fresh games: no old-save compatibility notes. Keep thomaswp's credit and the GPL everywhere.
- Mod tests, as `.github/workflows/tests.yml` runs them (.NET 8; 276/276 pass):
  `dotnet restore StabilityTests/StabilityTests.csproj --source https://api.nuget.org/v3/index.json`, then
  `dotnet run --project StabilityTests --no-restore`. Python checks: `python -m unittest discover -s RuntimeChecks -p
  "test_water_snapshots.py"` and `python RuntimeChecks/compare_walker_traces.py --self-test`. RuntimeChecks needs a
  local game install; see `StabilityTests/README.md`.
- Release (no script; how 1.1.13 to 1.1.15 were cut): bump `<Version>` in `BeaverBuddies/BeaverBuddies.csproj` and
  `BeaverBuddies/manifest.json`; add `## X` on top of `STABILITY-CHANGELOG.md`; update the README status note, install
  lines and check counts; update the site (below). PR → CI green → merge → annotated tag `vX` on the **merge commit**
  ("BeaverBuddies Stability Fork X") → `gh release create vX --repo timbermods/BeaverBuddies-Stability-Fork
  --verify-tag --latest --title "BeaverBuddies Stability Fork — X"` with `BeaverBuddies-Stability-Fork-X.zip` (the
  **Release Steam** build, folder `BeaverBuddies-Stability-Fork/version-1.1/`) and `X-SHA256SUMS.txt`. A full Latest
  release, never a pre-release.

## Writing README and website text

Kyler, 2026-09-24: "simplicity and elegance is effective and desirable." Every change to the README, the website
text and the player docs follows these rules.

- **Write for a Timberborn player** who wants to download, install and use the mod. Developer detail goes in
  `DEVELOPING.md` (building, checks, how the networking works, playtests) or `STABILITY-CHANGELOG.md`; link to it
  rather than repeating it.
- **Short.** One idea per sentence, most under about 20 words. A paragraph or FAQ answer is one to three sentences,
  a troubleshooting answer a few numbered steps.
- **Lead with the action.** Menu paths as arrow chains; on-screen labels in bold, exactly as in game.
- **Say each thing once**, where a player would look for it; link to it elsewhere.
- **Plain words.** No internals (class names, ids, formats) unless the player needs them to act.
- **Cut** filler, repeated caveats, edge cases a player won't meet, and history ("since …", "no longer", older
  builds). Describe the mod as it is now.
- **Check every fact against the code** before writing it; changelogs lag.
- **Keep, briefly:** credits, the unofficial line, the status, and safety facts.
- **Reread as a new player before publishing.** Every step works as written, and nothing is said twice.

The player docs are `README.md`, `STEAM-INVITES.md`, `CONNECTION-PANEL.md`, `PLAYER-ACTIVITY.md` and `docs/`. Facts
checked in the code on 2026-09-24 that older text got wrong: settings open from **Mods** → the settings button on
**BeaverBuddies - Stability Fork** (the Mod Settings mod adds one to each mod's row); **Player cursors** is in the
Esc menu, in a co-op game only; joining closes when the host unpauses or first changes the game, not at **Start
Game**; **Save and Rehost** is only in the desync dialog (otherwise the host saves and hosts that save again); public
builds show no bug-report button.

## Website

- **Where:** `docs/`: `index.html` (features), `install.html`, `troubleshooting.html`, `faq.html`; assets in
  `docs/assets/`. No 404 page. Live at https://timbermods.github.io/BeaverBuddies-Stability-Fork/.
- **Published:** GitHub Pages ("legacy" build) serves `main:/docs`, so merging to main publishes in about a minute.
  `docs/.nojekyll` must stay. No build step: plain HTML, CSS, a little vanilla JS.
- **Latest releases update themselves:** when a release becomes GitHub's Latest, `.github/workflows/latest-release.yml`
  (the shared timbermods workflow) appends the standard footer to its notes, sets the site's
  `data-release="version|tag|asset-name"` fallback text and the README lines ending in `<!-- latest -->` to the new
  version, runs the site checks and commits to main. Pre-releases change nothing. Descriptions, status lists and FAQs
  stay manual (the checklist below). Dry run: Actions → Latest release → Run workflow.
- **Look:** "The Crosscut Saw Team". A two-person crosscut saw laid across a fresh-cut log round at dusk: two sawyers
  on one blade, out of step the saw binds. Dark only, blued steel. The look is fixed: updates extend it, never restyle.
- **Design records (read these before any site change):**
  - `PRODUCT.md`: the facts, voice, honest status and every site contract.
  - `DESIGN.md`: the visual system and its named rules, the source of truth for the look.
  - `.impeccable/surfaces/docs-index-html.md`: the direction contract.
  - `.impeccable/design.json`: tokens and component snippets.
  - `.impeccable/critique/`: the pre-redesign critique.

### Design rules (from DESIGN.md; keep them)

- **Download Red**: red #b8322a (hover #cf3d33, white text) is spent on Download buttons only. A second red is a defect.
- **Two Players**: host orange #f0a04b and guest blue #6fb3ee exist only for the two players: their cursors, name tags
  and dots in the hero's shared-colony map, and the saw handles in the header mark and favicon. Never on text,
  borders, buttons or backgrounds.
- **Pine Is Solid**: pine #e3c285 means played, current or focused (Confirmed chips, step discs, current-nav underline,
  3px emphasis rules, focus ring, selection, FAQ chevron). Tested-only things are muted steel #aeb7bd, never pine.
- **Slab Is Stencil**: Zilla Slab only for headings, the brand, buttons and step numbers; everything else system-ui.
- **Sentence Case**: no uppercase, no tracked-out labels; the only letter-spacing is the chip's .03em.
- **Plate Not Card**: containers are flat plates (#252e36, 8px radius, `inset 0 0 0 1px var(--edge)`). No drop shadows
  on plates, no offset shadows, no glow. Only the Download button and the panel screenshot cast a (soft, dark) shadow.
- Tokens, all in `:root` of `docs/assets/style.css` (there is no light theme and no theme toggle; `color-scheme: dark`):
  ground #161b20, band #1c2329, plate #252e36, edge #3a454f, ink #eceeea, muted #aeb7bd, heartwood text on pine
  #1b1510, pine-deep #b98d4e (open question border), bark #4a3322, saw steel #c7cdd2, link #8fc6e8. good #8fd08a /
  warn #f0c060 / bad #ff7a6b only to name the connection panel's own green, yellow and red in copy; warn also draws
  the warning callout's 3px top rule.
- Fonts: Zilla Slab 600 and 700, self-hosted in `docs/assets/fonts/` (OFL.txt alongside); body is system-ui, code
  ui-monospace. No other webfonts, and nothing from a CDN at runtime: the site loads nothing from elsewhere.
- Hero: an inline SVG in `index.html` (`svg.coop-map`): one shared colony from above, the host's orange cursor on a
  selected house, a friend's blue cursor laying a path, and a small connection panel with both players *In sync*.
  Edit the SVG directly; it uses only the tokens above.
- Textures: `log-round-mark.webp` (section marker) and `log-round.webp` (no longer shown), made by `docs/assets/make_log.py`
  (numpy + Pillow, seed 1114; `cd docs/assets && python make_log.py`, byte-reproducible). Change the script and re-run
  it rather than editing images, then `embed-prompt` each changed raster (see below). `connection-panel.png` is a
  real in-game screenshot, embedded by the README: never rename or move it.
- Structure: ruled sheets, not card grids. Hairline tally (`ul.tally`), steps on a 3px rail (`ol.steps.horizontal`,
  compact variant `ol.steps.compact` in the install guide), the scorecard, guide pages with `.page-hero` + sticky
  `nav.toc` + `.prose`, FAQ/Troubleshooting as `details.q` > `summary` + `.answer` (pine `.sub` heads), `.table-wrap`
  tables that unstack under 640px, `.callout` plates that open with a bold lead sentence (`.callout.warn` adds a 3px
  warn top rule, for must-not-miss install warnings), `.path` + Copy button.
- The log round (`<span class="round" aria-hidden="true">`) is the only thing above a heading: no eyebrows or kickers.
- Phones: no horizontal scroll at 390px, tap targets ≥ 44px, the header turns static under 640px.
- Motion: in the hero map, the friend's blue cursor moves down the path it is laying (40px, 4.8s loop); the only moving
  thing, and only under
  `prefers-reduced-motion: no-preference` (reduce also kills transitions and smooth scroll).
- Don't: add a light theme, stat strips, icon tiles, grids of identical cards, side-stripe accents, gradient text, new
  accent colours, stock/generated imagery, Timberborn's official logos or key art, or thomaswp's `Media/` art as this
  fork's brand (credit it to the original if ever shown).
- New components: build them from these tokens and components, match the neighbouring sections, add them to DESIGN.md.

### Content rules

- Write all text by *Writing README and website text* above; the rules below add the site's specifics.
- Describe the mod as it is now. No "New in", "added in <version>" or version history on player pages; that lives in
  `STABILITY-CHANGELOG.md` and the release notes. The one upgrade fact kept: delete an old
  `BeaverBuddies-StabilityPreview` folder.
- Played/not-played status matches the README's status note exactly (e.g. "1.1.15 has not been played"; 1.1.13, the
  last feature release, was played and stays linked). Never invent numbers, reviews, player counts or screenshots.
  PRODUCT.md "Evidence on Hand" lists what does not exist.
- Credits on every page footer: an independent fork of BeaverBuddies by thomaswp and contributors; maintained by
  Timbermods; unofficial, not affiliated with or endorsed by Mechanistry; report problems to this repo.
- Terminology: in-game names exactly (Host co-op game, Invite Friends, Start Game, Join co-op game, Save and Rehost,
  Reconnect (wait for Rehost), connection panel, Ease off below, speed boost, Player cursors, You, in the chat,
  Viewing / Editing). The mod is "BeaverBuddies Stability Fork"; in the mod list "BeaverBuddies - Stability Fork".
- `docs/assets/release.js` is shared across timbermods sites and byte-identical (to MixedStorage's copy): replace it
  with a newer shared copy, never edit it. Every page loads it with the same `data-repo` and `data-asset`.

### Update the website for a new release

When asked to "update the website for the latest release, consistent with the design":
1. Read the release and the docs: `gh release view vX -R timbermods/BeaverBuddies-Stability-Fork`, README (status
   note at the top, Highlights, Testing), `STABILITY-CHANGELOG.md`, `CONNECTION-PANEL.md`, `STEAM-INVITES.md`,
   `PLAYER-ACTIVITY.md`, `BeaverBuddies/Localizations/enUS_BeaverBuddie.csv`. List every player-facing change.
2. Update every place the site states a changed fact (`grep -rn "1\.1\.15" docs/` finds the version ones):
   - Static fallbacks that `release.js` overwrites: `data-release="version"` (index hero + closing Download buttons,
     install Download button, SHA256SUMS line, mod-list and `BeaverBuddies vX is loaded!` lines, troubleshooting
     `#not-loading`), `data-release="asset-name"` (install: zip name twice). Download links keep
     `href=".../releases/latest"` with `data-release-href="download"`; never link a fixed tag.
   - Home status plate `div.status[data-release-pinned="X"]`: bump the attribute and rewrite both paragraphs (what the
     release is, check count, played or not, the feature-complete/Timber Together line).
   - **The scorecard** (`index.html#stability`, `div.scorecard`): `div.confirmed` (Confirmed in play) and
     `div.tested` (Covered by checks), each item `<li><b>Title</b><p>One or two sentences.</p></li>`, no version
     numbers. Add new fixes to `tested`; move an item to `confirmed` only when Kyler reports it played. Keep the chips
     `Confirmed · N` / `Tested · N` equal to the item counts. Mirror changes in PRODUCT.md's Confirmed/Tested lists
     and the FAQ "What are the known limits?" answer.
   - Requirements and game version (`1.1.2.4`, Harmony, Mod Settings): install `#requirements`, FAQ "Which version of
     Timberborn…", troubleshooting `#not-loading`.
   - Features: home `ul.tally` (one `<li><h3>…</h3><p>…</p></li>` per feature), the `#steam`, `#panel` (status table,
     bullet list) and `#teammates` sections, FAQ "How is it different…"; limits in `#limits` and the FAQ.
   - Settings and keys: install `#settings` table (Setting / Default / What it does) and the Bindings line under it.
   - Meta: each page's `<title>`, `meta description`, index `og:description`.
   - PRODUCT.md "Operating Context" and "Honest status"; the README status note if it repeats site facts.
3. Put new content into the existing components above (a new FAQ is another `details.q` in the right section, a new
   problem another `details.q` in troubleshooting, plus a `nav.toc` link for a new section). Don't restyle anything.
   Write it by *Writing README and website text* above.
4. Test (no CI checks the site; these held on 2026-09-23):
   - `grep -Fc 'src="assets/release.js" defer data-repo="timbermods/BeaverBuddies-Stability-Fork" data-asset="^BeaverBuddies-Stability-Fork-[\d.]+\.zip$"' docs/*.html` → 1 on each of the 4 pages.
   - `grep -rn "releases/tag/\|releases/download/" docs/` → nothing.
   - `for c in confirmed tested; do sed -n "/<div class=\"$c\">/,/<\/div>/p" docs/index.html | grep -o '<li>' | wc -l;
     done` matches `grep -o 'Confirmed · [0-9]*\|Tested · [0-9]*' docs/index.html` (4 and 12 now).
   - `curl -s https://timbermods.github.io/MixedStorage/assets/release.js | cmp - docs/assets/release.js` → no output.
   - `"$(ls -d ~/.claude/plugins/cache/impeccable/impeccable/*/skills/impeccable | tail -1)/scripts/impeccable" embed-prompt --scan docs` → 0 missing.
5. Preview: `python -m http.server 8782 -d docs` (in the background), open http://localhost:8782/. Check the dark site
   at desktop and a 390px phone (no horizontal scroll) and the changed sections. With the personal
   `impeccable-site-flow` skill: `python <skill>/scripts/capsite.py http://localhost:8782/ <out> "" install.html
   troubleshooting.html faq.html`; otherwise the Browser pane at desktop and mobile sizes. Stop the server after.
6. Optional but recommended: run the detector,
   `"$(ls -d ~/.claude/plugins/cache/impeccable/impeccable/*/skills/impeccable | tail -1)/scripts/impeccable" detect --json docs`
   (parse from the first `[`). Known false positives (no `.impeccable/config.json` records them): `side-tab` /
   `border-accent-on-rounded` on the deliberate 3px rules (step rail, its mobile `border-left`, `.page-hero`,
   `.closing`, scorecard heads); `cramped-padding` on `.tint`, `.table-wrap`, `.steps`, `.closing`, `.page-hero`;
   `flat-type-hierarchy` (it counts the small footer/TOC h2s); advisories for .92/.95/.96/1.2/1.35/1.7rem, `#fff` on
   Download, `shape-assembled-illustration` (the saw SVG), and `side-tab` on `.callout.warn` (its documented 3px warn
   top rule).
7. If the look changed (a new component or layout), update DESIGN.md and `.impeccable/design.json`.
8. Update the README if it repeats the facts.
9. Ship: branch → commit → push → `gh pr create`. After Kyler says merge: `gh pr merge <n> --merge` (that publishes),
   then verify:
   - `gh api repos/timbermods/BeaverBuddies-Stability-Fork/pages/builds/latest -q .status` is `built`;
   - `curl -s https://timbermods.github.io/BeaverBuddies-Stability-Fork/ | grep -c "<a changed string>"` finds it.

### Full redesign

A new look goes through the whole Impeccable flow (init → critique → audit → direction → build → finish review →
DESIGN.md). With the personal skill: "use the impeccable-site-flow skill to redesign this site".
