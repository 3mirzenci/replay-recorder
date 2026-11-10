# Aurora v2: UI overhaul plan for Replay Recorder

Written 2026-11-10. Replaces `aurora-overhaul-plan.md`. Nothing is built yet.
Look reference: `mockup.html` in this folder (open in a browser; Library / Player tabs, accent swatches).
Where the mock-up and this plan disagree, this plan wins. The mock-up is a browser sketch: copy its look, not its HTML.

## The look in one paragraph

Everything sits on plain near-black with no grey boxes. One faint glow in the accent colour lights the top edge (10%), with a soft halo around the video. Greys are neutral. The accent only marks key parts: logo, selected clip, NEW badge, scrub line, play, Export for Discord, trim frame, mic track. The app draws its own title bar. Clips are grouped by day, newest top-left, in a grid that fills the width. Games are filter pills in the top bar.

## 0. Ground rules

- **Branch:** all work on a new branch `aurora`, created from tag `v1.1.0`. Nothing is committed to `main`.
- **No release, no version bump, no tag, no push to `main`.** The in-app updater must never see this work.
- **Don't touch** recording, audio capture, or the export encoding path. The only export-related change is exposing the existing quality calculation (step 8).
- One step = one small commit. The app builds and runs after every step.
- After each step: stop, list what to check, and wait for the owner's OK.

## 1. Keeping the Classic look

Two safety nets, both required:

1. **The old app stays intact.** `main` and `v1.1.0` are untouched, so the exact old layout is always one checkout (or one installer) away.
2. **Classic stays in the Look setting.** The setting gets three options:

| Setting value | Label | What it is |
|---|---|---|
| `aurora` | Aurora | The new look. Default for new installs. |
| `windows` | Classic (grey) | Today's default colours (neutral grey background) |
| `amber` | Classic (blue-grey) | Today's Amber colours (blue-grey background); named by background, since most people set their own accent |

A saved `windows` or `amber` value is respected; nobody is switched automatically.

**What Classic keeps:** its colours, the grey boxes around the top bar and main area, its squarer corners, and no glow, halo or grain.
**What Classic shares with Aurora:** the structure. Own title bar, game pills, fill-width grid, clip labels, player header, sliders on tracks, the new timeline drawing and the export line.

This works by making the differences tokens, not separate layouts:

| Token | Aurora | Classic |
|---|---|---|
| `PanelBg` | transparent | today's `Panel` grey |
| `PanelRadius` | 0 | today's value |
| `ButtonRadius` | 999 (pill) | today's value |
| `ThumbRadius` | 12 | today's value |
| `GlowOpacity`, `HaloOpacity`, `GrainOpacity` | 0.10 / 0.30 / 0.03 | 0 / 0 / 0 |

Maintaining two full layouts is not part of this plan.

## 2. Framework and limits

- Library and player: WPF on .NET 10 (`src/ReplayRecorder.Library`). Recorder tray: WinForms. No framework move, no UI library; styles are written by hand in `App.xaml` as today.
- **Video:** mpv renders into a native child window (`VideoHost`). WPF cannot draw over it. Glow and halo go around it. Fullscreen controls stay a separate window.
- **No live blur anywhere.** Glow and halo are radial gradient brushes.
- **Turkish:** no fixed-width buttons. Every new string goes into `Lang.cs` in both languages.
- Every new style uses `DynamicResource`, because the look and the accent change live.

## 3. Style tokens

New file `Themes/Aurora.xaml`. It keeps every existing key (`Bg`, `Panel`, `Card`, `CardHover`, `Line`, `Text`, `Subtle`, `Accent`, `OnAccent`, `AccentSoft`, `Danger`, `ScrollThumb`, the three fonts). `Windows.xaml` and `Amber.xaml` get the new keys too, with Classic values.

| Group | Aurora values |
|---|---|
| Colours | Bg `#070708`, Raised `#111113`, Glass white 6%, Glass2 white 10%, Line white 8%, Edge `#262628`, Text `#F2F2F3`, Subtle `#A3A3A8`, Faint `#85858B` |
| Tracks | Game `#B9B9C0`, Mic = accent, Discord `#7B86FF` |
| Status | Danger `#FF7A72`, level meter green/amber/red |
| From the accent | AccentGlow, AccentHalo, AccentSoft, OnAccent |
| Fonts | Body: Segoe UI Variable Text. Titles (logo name, big title, day names, player title): Bahnschrift SemiBold. Times and numbers: Bahnschrift with equal-width digits (`Typography.NumeralAlignment="Tabular"`). All ship with Windows; nothing is bundled |
| Type sizes | 11.5 / 12.5 / 13 / 14 / 18 / 20 / 36 |
| Spacing | 4 / 8 / 12 / 16 / 22 / 30 |

**Accent rules (the earlier "some accents look off" problem):**
- The background never takes any colour from the accent. Only the glow, halo and marked parts do.
- `OnAccent` is chosen by contrast ratio (dark `#141210` or white, whichever is higher), not by brightness. Expected: yellow, red, green, cyan, pink, purple, white get dark text; dark blue gets white.
- The accent is used for fills and lines only, never for small text.
- Test set: `#FFC21A`, `#FF4545`, `#3EDC6E`, `#3FC8FF`, `#FF5C8A`, `#3B5BFF`, `#9B6BFF`, `#EDEDED`.

## 4. What changes on each screen

**Library**
- Top row: logo, game pills, search, folder, settings, window buttons. No box around it in Aurora.
- **Game pills** replace the `GameList` dropdown: All, then games by clip count, each with its count, then Favorites. Show as many as fit (at most 5 games); the rest go in a "More" pill that opens a menu. Filtering logic is unchanged; only the control changes.
- **Title row:** the big title (36 px) shows the active filter ("All clips", "Fortnite", "Favorites") with count and size beside it. "All clips" must not appear twice. The four sort options sit at the right end of this row as small pill tabs.
- **Day groups** stay exactly as today: newest first, sticky header, select-day circle.
- **Grid fills the width.** Columns = max(2, floor((W + 18) / (260 + 18))), tile width = (W − 18 × (columns − 1)) / columns. About 4 columns at the current window size. No dead strip on the right.
- **Clip label:** game name, then the time (Bahnschrift, `Subtle`). Size and resolution show only on hover or when selected. Renamed clips show their name in place of the game.
- **NEW badge** (accent) on the newest clip of today, bottom-left of the thumbnail.
- Cards: thin edge, hover lift with an accent-tinted shadow, single accent outline when selected, accent scrub line while scrubbing. The "wall" layout keeps its text-on-picture mode.
- Selection bar, update banner, undo bar, empty state: same pills on dark glass.

**Player**
- **One header row:** "← Library" pill, title, Show in folder, Delete, window buttons.
- **Title** is "Game · today 03:49" style (20 px), subtitle "length · size · resolution". The filename is not shown unless the clip was renamed.
- Video with accent halo around it (outside the video only).
- **Timeline:** three labelled tracks (Game, Mic, Discord) drawn as thin vertical stripes following the loudness; trimmed-off parts fade; the kept range has a rounded accent frame with grips.
- **Volume sliders sit at the right end of their own track**, each with its dB value. The label is "Game", not "Game + chat", whenever a Discord track exists.
- Controls row: trim buttons (pills) left; transport centre (previous frame, play, next frame, **fullscreen**, time); export line and Export for Discord right.
- **Fullscreen stays exactly as today:** the button in the controls row and the F key both open it, with the see-through bar for play/pause, seeking and volumes. The mock-up shows the button next to the frame steps.
- **Export line:** beside the button, the resolution the export will get and the kept length, e.g. "720p60 / 0:42 kept · 19.5 MB". Updates live while trimming.

**Elsewhere:** fullscreen bar, Settings, setup, rename, What's new, menus, tooltips, dropdown lists and the recorder's "Clip saved" pop-up take the same tokens. The six Windows message boxes stay native for now. The tray menu does not change.

## 5. Order of work

0. **Set up.** Create branch `aurora` from `v1.1.0`. Add this plan and the mock-up under `docs/aurora/`.
1. **Tokens.** Add `Aurora.xaml`; add the new keys to `Windows.xaml` and `Amber.xaml`; add the third Look option; apply the `OnAccent` contrast fix. *Check: all three looks switch live; every screen opens; a test line in each font and weight the design uses (Segoe UI Variable Text regular and semibold, Bahnschrift regular and semibold) matches the mock-up, since both are variable fonts and WPF may not render in-between weights as expected. If Bahnschrift SemiBold comes out wrong, report it before going on.*
2. **Colours out of code.** Level meters, `FullscreenBar`, timeline colours, the favourite star, the pop-up, mpv background. *Check: Classic looks identical to v1.1.0.*
3. **Shared control styles.** Pills, tabs, search, sliders, switch, dropdown, menu, tooltip, scrollbar, focus rings. Turn keyboard focus back on for buttons. *Check: the keyboard tests in section 6.*
4. **Library frame.** Boxes via `PanelBg`, glow and grain layers, title row, sort tabs, day header. *Check: the banding test in section 6; Classic still has its boxes.*
5. **Game pills.** Replace `GameList`. *Check: each game, Favorites, "More" overflow with 8+ games, Turkish.*
6. **Cards and grid.** Fill-width columns, new label, hover details, NEW badge. *Check: select, Ctrl/Shift-click, right-click menu, Del, F2, star, hover scrub, drag to Discord; with 1000 clips the library still opens in about 2 s and thumbnails still load only near the screen.*
7. **Player frame.** Single header row, new title, halo, controls row. *Check: video shows; Space, arrows, I/O/P, E, F, Esc all work; the fullscreen button opens fullscreen and Esc or F comes back.*
8. **Export line.** Move the bitrate-to-resolution decision in `DiscordExporter` into one pure function used by both the exporter and the UI, with a unit test. No change to what gets encoded. *Check: the line matches what an actual export produces at 3 trim lengths.*
9. **Timeline.** Redraw `TrimTimeline`; expose lane positions so three WPF sliders line up at the right. *Check: seek, drag start, drag end, drag the whole range; clips with 1, 2 and 3 tracks.*
10. **Title bar.** 10a: `WindowChrome` with own buttons. 10b (separate commit): snap menu on hover. *Check: section 6.*
11. **Settings window.** Show a mock-up first, then build.
12. **Small windows:** setup, rename, What's new, fullscreen bar.
13. **Recorder pop-up colours.**
14. **Accessibility pass** on the finished app: contrast of every text pair, Tab order, names on icon-only buttons.
15. **Test build.** Publish both projects self-contained into `dist\test` (as `build.ps1` does). Stop the running test copy first; start it via WMI, not from the shell.

## 6. Risks and their tests

**Glow banding.** WPF gradient brushes are not dithered, and a 10% glow on near-black has only about 25 colour steps.
- Fix: a 128×128 grey noise image tiled over the glow area at `GrainOpacity`, nearest-neighbour scaling, masked to the glow's shape.
- Test (step 4): full-screen on both monitors, grain on and off. If no rings are visible without grain, set `GrainOpacity` to 0.
- Fallback: lower `GlowOpacity` to 0.06.

**Keyboard in the player.** The window handles Space and Left/Right before any control (`OnPreviewKeyDown`). That stays.
- Space always plays or pauses. Left/Right always step frames.
- Tab moves the focus ring. Enter presses the focused button. Up/Down adjust a focused slider.
- Tests (step 3): click Set start, press Space: video pauses and Set start does not fire again. Tab to a slider: Up/Down change volume, Left/Right step frames. In the library with focus on a top-bar control: Del and F2 still act on the selected clip.

**Title bar and the Windows 11 snap menu.**
- 10a must pass: drag to move, double-click to maximise, drag to screen edges, Win+arrows, Win+Z, resize from every edge, correct size when maximised (no content cut off at the edges).
- 10b: return `HTMAXBUTTON` from `WM_NCHITTEST` over the maximise button, handle its click, and draw its hover state by hand.
- Fallback: if 10b misbehaves, revert that commit only.

**Other**

| Risk | Handling |
|---|---|
| Video can't be drawn over | Halo is drawn around the video area only |
| `TrimTimeline` hit-testing changes | Own commit; every drag mode tested |
| Grid width change breaks lazy thumbnails | 1000-clip check in step 6 |
| Many games overflow the pills | "More" menu; tested with 8+ games |
| Turkish text | One full pass with the language set to Turkish |
| Speed | No blur; scroll the full list and watch for stutter |

## 7. Decisions already made

- Classic stays selectable (section 1).
- The six Windows message boxes stay native for now; the delete confirmation is first in a later round.
- Default glow is 10%.
- Titles use Bahnschrift SemiBold in Aurora, not Segoe UI Variable Display. Classic keeps the fonts it has today. The mock-up has a "Titles" switch for comparison; Bahnschrift is the chosen one.
- Day groups follow calendar days, as today.

## 8. Notes from checking the plan against the code

- **Step 8, export line:** `DiscordExporter.Budget` (bitrate for a length) and `DiscordExporter.PlanFor` (resolution and frame rate for a bitrate) are already pure functions; the UI calls them instead of copying the logic. The line is a prediction: the exporter re-encodes lower if the first file comes out too big, and when the clip is already small enough it copies it without re-encoding, so the line then says "original quality" like the export does. The step-8 check allows for that.
- **Step 6, grid:** cards are a fixed 296 px today inside a `WrapPanel`. Fill-width means setting the card width from the formula whenever the grid's width changes; the lazy thumbnail loading (`InView`) stays as it is.
- **Branch:** `aurora` lives in its own folder (`Replay Recorder - Aurora`, a git worktree), so the main folder stays on `main`.
