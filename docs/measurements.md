# Measurements

Empirical results from running the bar on real hardware. The spec's numbers are targets; these are
observations. Each entry records the machine, because none of this generalises without one.

**Reference machine.** Windows 11 Pro 26200, .NET 10.0.203, primary display 2560 × 1440 at 125%
(2048 × 1152 DIP), multi-monitor with a secondary. Explorer's taskbar configured **autohide on**
(`StuckRects3` byte 8 = `0x03`), `TaskbarAl = 0`.

---

## Step 1 — left edge, AppBar reservation

| Quantity | Expected | Measured |
| :---- | :---- | :---- |
| Reserved rect | `BarMetrics` computed `(0,0) 64×1152` | `(0,0) 64×1152` — exact |
| Work area after reservation | left edge given up, full height retained | `64,0,1984,1152` |
| DPI scale seen by WPF | 1.25 | 1.25 |

The full-height work area is the load-bearing part: it confirms Explorer released its own bottom
reservation, which is what `SetTaskbarState(ABState.AutoHide)` is there to do. Without it both bars
reserve space and every maximised window is short by two taskbar heights.

## Step 2 — bottom edge, autohide contention (§11.2 Q3)

**Result: the predicted flicker artefact does not occur. Q3 is resolved as a release blocker — but read
the scope of the claim below, which is narrower than "no contention".**

shell-integration §2.5 predicted a 10 Hz race — pointer reaches the bottom edge, Explorer reveals
its autohidden taskbar, ManagedShell's 100 ms watchdog re-hides it — with a persistent 1 px line
and flicker as the visible symptoms, as reported by RetroBar users.

Sampled at 1 kHz for 40 s with the bar on the bottom edge and a **real hand sweeping the pointer
into the edge** (synthetic input does not trigger the reveal — see below):

| Observable | Measured |
| :---- | :---- |
| Precondition: cursor Y range | 724 – 1151, edge zone ≥ 1144 |
| Precondition: samples at the edge | 10 674 of 40 000 (26.7%) |
| Explorer `Shell_TrayWnd` visible | **0.0% of the time** |
| Visibility transitions | **0** |
| Intrusion pixels on screen | **0** (min, median and max) |
| Intrusion changes | **0** |

**Why it does not happen — and what the zeroes actually mean.** The prediction assumed Explorer's
taskbar stays a live window that reveals on hover. It does not: ManagedShell hides it with
`SetWindowPos(SWP_HIDEWINDOW)`, and a hidden window is not eligible for the autohide reveal at all.

**So the zeroes above measure a dead trigger, not a clean one.** Hover cannot reveal a hidden
window, so testing hover was guaranteed to observe nothing whatever the design did. Reported
first time round as "no contention", which was wrong: what it shows is that Explorer's taskbar is
fully suppressed while Umbra runs.

Observed by the user, not by this harness: **the Win key still surfaces Explorer's taskbar**, drawn
underneath Umbra, and the Start menu opens correctly over it. That is the contention path that
remains, and it is unmeasured. It is also **working as intended** — Umbra is a replacement taskbar,
so Explorer's being unreachable underneath it is the goal, and Start still works, which is the
functional requirement. No damage persists after Umbra exits: window visibility, `WS_EX_TOPMOST`,
parked position, `ABM_GETSTATE`, the `StuckRects3` autohide bit and the work area all return to
their pre-launch values.

Note this is *not* the same mechanism as ordinary autohide, which is what made the baseline
confusing (below).

### Baseline, for contrast — how Windows autohide actually behaves

Measured with Umbra not running:

| Pointer | Explorer taskbar |
| :---- | :---- |
| At the bottom edge | visible, **48 px** on screen |
| At screen centre | visible, **2 px** on screen |

**Windows autohide never hides the taskbar window.** It slides it off-screen, parking a 2 px sliver
and sliding out to 48 px on reveal. `IsWindowVisible` is `true` throughout. Any instrument that
watches visibility rather than position will therefore read a perfectly stable "always visible" and
learn nothing — that was the first version of this measurement, and it was wrong.

### Methodological note — synthetic input does not trigger the reveal

Neither `SetCursorPos` nor `SendInput` with `MOUSEEVENTF_ABSOLUTE` caused Explorer to reveal an
autohidden taskbar on this machine. With Umbra **not** running — the control — a swept synthetic
pointer left the taskbar at its parked 2 px throughout.

That makes any synthetic run uninterpretable: a clean result cannot be distinguished from a trigger
that never fired. The harness therefore samples the cursor alongside the taskbar and prints an
explicit verdict on whether its own precondition was met, and a run that does not meet it is
reported as proving nothing rather than as a pass. Two earlier runs produced clean-looking zeroes
that meant nothing at all; both are discarded.

### What this does not cover

- **One machine, one Windows build.** RetroBar's reported artefacts may be version- or
  configuration-specific.
- **Secondary monitors.** Only the primary `Shell_TrayWnd` was sampled; `Shell_SecondaryTrayWnd`
  exists on this machine and was not measured.
- **Soak.** 40 s of sweeping, not hours of use. An intermittent artefact would not necessarily show.
- **The tray is not running yet.** Build order step 7 adds ManagedShell's second 100 ms timer and
  its Z-order interception; this measurement predates both.

## Step 3 — tier system and monochrome normalisation

Pointer proximity driving the tier level, verified against the spec's own figures:

| Pointer distance | Expected level | Measured |
| :---- | :---- | :---- |
| 896 DIP | 0 (beyond the 120 px band) | 0.000 |
| 80 DIP | 0.5 — the ramp midpoint, `(120−80)/80` | 0.500 |
| 0 DIP (on the bar) | 2 after the dwell guard | 2.000 |

Release timing matches §3.4: level 2.0 → 0.468 → 0.000 over ~800 ms, i.e. 400 ms per tier.

**Emitted luminance, sampled from screen pixels** rather than from the model — this is what the
panel actually sees:

| Tier | P99 | Max | Target |
| :---- | :---- | :---- | :---- |
| 0 — dormant | 0.048 | 0.072 | P90 → 0.0513 (`#404040`, running icon) |
| 0.5 — mid ramp | 0.060 | 0.089 | between `#404040` and `#4D4D4D` |
| 2 — engaged | 0.422 | 0.494 | P90 → 0.45 cap |

**Decision recorded: the Tier 2 cap is applied to P90, not to the maximum.** §3.2 says "tone-mapped
so their peak luminance lands at the cap" while §5.1 argues for a percentile over the maximum
(immune to a single stray bright pixel). Capping the percentile means the top decile can exceed the
cap — measured max 0.494 against a 0.45 cap, 10% over. The wear cost of that overshoot is
`(0.494/0.45)^1.7 = 1.17`, applied to ~0.1% of an icon's pixels, so it is immaterial to the budget.
Capping the maximum instead would let one stray pixel dim an entire icon, which is the failure §5.1
chose the percentile to avoid.

### A methodological trap worth knowing

The first attempt at this measurement appeared to show icons going full-chroma at 80 px, which
would have been a real bug. It was the test harness: **PowerShell is DPI-unaware, so its
`SetCursorPos` coordinates are virtualised** — asking for `(1280,1270)` puts the cursor at physical
`(1600,1587)`, off the bottom of a 1440 px screen, where it clamps onto the bar. Both the "near" and
"on the bar" cases were measuring Tier 2. Drive the cursor in DIP from a DPI-unaware process, or
physical from an aware one, but do not mix them.

## Step 4 — wear accumulator

Instrumentation only. Nothing feeds back into layout, and nothing should until several weeks of
real data have been looked at.

| Quantity | Value |
| :---- | :---- |
| Accumulator over the reserved rect | 2560 × 70 physical px, **1400 KB** float64 |
| Sample rate | 1 Hz, from the visual tree and its transforms |
| Persist | every 5 min, plus session lock, suspend and exit; written to a temp file and swapped |
| Lit area at Tier 0 | 3.59% of the reserved rectangle |

The scene is read via `TransformToAncestor`, so the drift transform of step 5 will be folded in
automatically rather than needing a second code path.

**The accumulator is verified sensitive to sub-pixel motion**, which is the property §11.2 Q1
depends on: micro-jitter moves content by fractions of a pixel (§6.1), so a compositor that snapped
emitters to integer cells would report that the motion layer achieves nothing, and the answer would
look entirely credible. Two tests hold the integer cell range fixed and vary only the fractional
offset — one asserts the sampled pattern changes, the other that accumulating over a spread of
fractional offsets yields a lower peak cell than the same exposure at one fixed position.

Both were initially written with a half-pixel shift and **passed for the wrong reason**: a shift
that large also moves the emitter onto new pixels, so they were detecting the changed clipping
bounds rather than the sampling. A naive-rasteriser mutant passed against them. They now confine
the shift to a single cell range, and the mutant fails four tests.

## Step 6 — outline mode, measured against a real icon set

> **Outline mode was removed from the product in spec v4.** This measurement is the reason, so it is
> kept in full rather than deleted — the numbers below are what the decision was made on. Nothing
> here is retracted; the feature it describes no longer exists. See design-spec §5.3 and the v3 → v4
> change log.

§5.3 justifies outline mode for "pin sets dominated by large solid-fill logos" and puts the saving
at 70–85% of lit area. **On a real icon set that figure holds for one icon in nine.**

| Icon | Opaque area | Energy | Outline saves |
| :---- | ----: | ----: | ----: |
| File Explorer | 57.8% | 633 | **72.9%** |
| VS Code | 60.6% | 248 | 56.1% |
| Discord | 88.4% | 479 | 35.2% |
| Packaged app | 99.3% | 631 | 27.7% |
| Ascent | 31.8% | 55 | 27.6% |
| NVIDIA Broadcast | 100.0% | 120 | 13.7% |
| Firefox | 71.2% | 373 | 9.9% |
| Jagex Launcher | 97.5% | 271 | 1.0% |
| RuneLite | 42.7% | 123 | 0.0% |

Weighted across the row, outline mode removes **34.6%** of icon lit area — and two thirds of that
comes from File Explorer and VS Code alone. The rest of the set is already mostly stroke and
transparency, with no interior left to reclaim; two icons gain nothing at all.

**This confirmed §5.3's own instruction rather than contradicting it.** The spec said outline mode
must stay "a per-icon opt-in, not a global default — it costs recognisability unevenly and users
should be able to apply it to their worst offenders only". The data said the same thing
quantitatively: worth it for two icons here, worthless for two others, and a recognisability cost
everywhere.

**And that is what ended it.** A feature worth enabling on two icons in nine needs a per-icon
settings surface to be usable at all, which is a real cost in UI and in explanation. Weighed against
34.6% of the lit area of a bar that §2.4 already places 86–191× below light-mode desktop content,
the project owner judged the complexity not worth it and removed the mode in v4. Recorded here
because the measurement is sound and the judgement built on it is a separate thing that a later
reader may want to revisit.

It also revised the premise. §5.3 assumed pin sets dominated by solid-fill logos; modern
application icons largely are not that, so the headline 70–85% was a best case rather than a typical
one. That gap between assumed and measured is the single most useful thing this step produced.

**Interior quality — fixed.** The first implementation flagged a pixel whenever a neighbour differed
by more than a threshold. That works on flat-shaded artwork and falls apart on gradients: a smooth
ramp crosses the threshold at every step, so the whole ramp came back as edge and the icons rendered
as bands and speckle.

Replaced with ridge detection — blur, Sobel, non-maximum suppression along the gradient direction,
then hysteresis thresholding. A real boundary is a ridge and survives; a smooth ramp has no ridge,
so almost nothing does.

Two bugs surfaced while doing it, both of which produced plausible-looking output:

- **A purely relative threshold has no defence against a flat icon.** With the cut taken as a
  fraction of the icon's own strongest gradient, a uniform icon's strongest "gradient" is
  floating-point dust from the blur — around 1e-17 — and a fraction of that admits every pixel in
  the image. A solid square came back 94% intact instead of 24%. Fixed with an absolute floor
  alongside the relative one.
- **Non-maximum suppression with `>=` on both sides keeps plateaus.** On a constant-gradient ramp
  every pixel ties with both neighbours and every pixel survives — reintroducing the exact banding
  the suppression was added to remove. Strict inequality on one side thins a plateau to its leading
  edge.

§11.2 Q10 was answered for one real icon set: the automatic outlines were usable. Not as clean as
hand-authored glyphs would have been, and busy artwork stayed busy, but the banding was gone. The
question is withdrawn in v4 along with the feature — the outlines were good enough, and it was not
enough to save them.

## Step 7 — tray hosting: it works, and the original diagnosis was wrong

**Resolved 2026-09-20.** Windows 11 25H2, build 26200, ManagedShell 0.0.370. Umbra collects
notification icons. It always could.

> **The earlier conclusion recorded here — "Windows 11 25H2 does not route notification icons to
> whichever window claims `Shell_TrayWnd`" — was wrong.** It is kept below, because how a careful,
> well-instrumented investigation reached a confident and incorrect answer is more useful than the
> answer was.

### What was actually happening

Four links in the chain. The first two were verified correct and reported as such; the failure was
in the third, and the fourth is why nobody could see it.

| | |
| :---- | :---- |
| Windows resolves `FindWindow("Shell_TrayWnd")` and sends `WM_COPYDATA` | **Works.** Unchanged on this build |
| ManagedShell's tray window receives it | **Works.** `dwData=1`, `NIM_ADD`, valid owner |
| `NotificationArea.SysTrayCallback` adds the icon | **Threw**, on every single icon |
| The exception is reported | To `ShellLogger` — **which nothing was attached to** |

The throw:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at ManagedShell.WindowsTray.NotifyIcon.SetPinValues()
   at ManagedShell.WindowsTray.NotificationArea.SysTrayCallback(UInt32 message, SafeNotifyIconData nicData)
```

`SetPinValues` is called for every arriving icon and does this:

```csharp
for (int i = 0; i < _notificationArea.PinnedNotifyIcons.Length; i++)
```

`PinnedNotifyIcons` is a `string[]` **the host application must supply**. Umbra never set it, so it
was null, so `.Length` threw — and `SysTrayCallback` wraps its whole body in `catch (Exception ex)`,
logs to `ShellLogger`, and returns. No crash, no return value, no property, no icon. The tray was
empty and completely silent about why.

**One line fixes it:** `PinnedNotifyIcons = []` in `ShellConfig`.

### Why the wrong answer was so convincing

Every observation in the original investigation was correct. `FindWindow` really did return Umbra's
window. `NotificationArea` really was initialised and not failed. A brand-new `Shell_NotifyIcon`
registration really did produce no icon. Double-initialisation, a missing `TaskbarCreated`
broadcast, a stale dependency, a pinned/unpinned filter and elevation really were all eliminated.

**The gap was that every one of those measurements sampled the two ends of the pipe and never the
middle.** `FindWindow` is the outside; `TrayIcons.Count` is the far end. Both are consistent with
"Windows stopped sending" *and* with "we receive it and throw it away", and nothing distinguished
them. That is this project's own rule — *measure the thing, then check the measurement could have
detected it* — failing on the one question where it mattered most.

### What actually distinguished them

Three experiments, each isolating one link, none of them expensive:

1. **A standalone spy window**, 60 lines, no AppBar and no Explorer suppression: claimed
   `Shell_TrayWnd`, won `FindWindow` without even having to demote Explorer's, and logged
   `NIM_ADD` / `NIM_DELETE` arriving. That alone disproves the recorded conclusion.
2. **ManagedShell alone**, no Umbra: held the window, received the message — and still collected
   nothing. That moves the fault inside ManagedShell and clears Umbra's environment entirely.
3. **`ShellLogger.Attach`**, one interface with one method. The exception had been printed on every
   icon since the day the tray was first switched on.

Experiment 3 was the whole investigation. Everything before it was working out where to look.

### The second bug, found on the way out

With icons collecting, the bar still showed none:

```
TrayIcons=9   AllIcons=9   PinnedIcons=0   UnpinnedIcons=9
```

`BarWindow` bound the tray to `PinnedIcons`, on the reasoning that unpinned icons belong behind
§7.1's overflow chevron. But nothing is pinned unless the user pins it, and **there is no UI to pin
with** — so the bound collection was structurally empty. Now bound to `AllIcons`; narrow it again
when the chevron exists.

### What is genuinely dead on Windows 11

`ExplorerTrayService` — ManagedShell's other tray source, which recovers icons that were registered
*before* we started. It walks `Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32` inside
Explorer and reads `TBBUTTON` structures out of its memory with `ReadProcessMemory`. **Windows 11's
notification area is XAML; that window chain does not exist**, so the walk fails at the third step
and returns nothing.

This costs less than it sounds. The second `NotificationArea.Initialize()` broadcasts
`TaskbarCreated`, and well-behaved applications re-register in response — measured, that is where
all eight icons come from. Applications that ignore the broadcast stay invisible until they next
update their icon, which matches the "some icons missing" reports against every other shell
replacement on Windows 11.

There is a native replacement if it ever matters: **`HKCU\Control Panel\NotifyIconSettings`**, which
is Windows 11's own per-icon store. 96 entries on this machine, each carrying `ExecutablePath`,
`InitialTooltip`, `IsPromoted`, and `IconSnapshot` — **a raw PNG of the icon** (verified: the blobs
begin `89 50 4E 47`). It has no `hWnd`, so an icon sourced from it could be drawn but not clicked,
and liveness cannot be derived from it: cross-referencing `ExecutablePath` against running processes
needs process paths that a non-elevated process mostly cannot read (38 of them visible here). A
supplement, not a substitute.

### Costs, unchanged

Tray hosting still runs a permanent 100 ms timer to re-assert the tray window's Z-order, which is
the second of the two timers §11.2 Q2 asks about. **That is now the only reason it is off by
default** — a power question, not a functional one.

## Step 9 — window picker, drag-reorder, settings, monitor selection

Driven against the running bar rather than reasoned about, because none of it is testable from the
core library. Reference machine as above, plus a secondary `\\.\DISPLAY1` at 1920 × 1080, 100%.

**Window picker.** Opened on a File Explorer slot holding four windows:

| Observable | Measured |
| :---- | :---- |
| Thumbnails registered | 4 of 4, source size 1618 × 982 px each |
| Picker geometry | 850 × 164 DIP, clamped to the work area at the screen's left edge |
| Close one window | 4 → 3 windows, picker relaid out 850 → 642 DIP |
| Click a thumbnail | foreground went to that specific window |
| Pointer leaves | dismissed after the 600 ms grace |

The cursor conversion was checked directly rather than assumed: the watcher logged raw `206,1410`
resolving to `165,1128` DIP at 1.25 scale, which is the bar, so the picker correctly stayed open.

**Drag-reorder.** Three drags on a three-pin test set, each verified against `pinned-apps.txt`:
`Firefox, Explorer, NVIDIA` → `Explorer, NVIDIA, Firefox` → `NVIDIA, Firefox, Explorer` →
`Explorer, NVIDIA, Firefox`. Each drop logged exactly one reorder, which is the check that matters:
before the fix, releasing capture before clearing state re-entered `EndDrag` and persisted twice.

A drag suppresses the click that ends it — verified by the absence of a `slot click` line — and a
plain click afterwards still activates normally.

**Settings.** Every field round-trips. The live-applied ones were confirmed against the bar's own
log: dragging the resting-size slider from 24 to 16 produced eight `settings applied live` lines,
one per step, and unticking motion produced `motion False` and stopped the ticks.

**Monitor selection.** Choosing `\\.\DISPLAY1` and restarting moved the bar to
`x=2560 y=1223 w=1920 h=48` at `dpiScale=1.00`, and the wear accumulator resized to 1920 × 60. The
restart handshake worked: `restarting` → `clean shutdown` → `startup`, with the new instance waiting
on the old process id before touching Explorer's state.

### Two instruments that were lying, and are now not

- **`expected rect` was computed against the primary screen.** On a secondary monitor it disagreed
  with `actual rect` on every run — `expected x=0 y=1104 w=2048` against `actual x=2560 y=1223
  w=1920` — while nothing was wrong. A comparison that always fails is as useless as one that can
  never fail. It now takes its origin and extent from the screen actually in use, and the two lines
  agree exactly.
- **`motion disabled by --no-motion`** was logged whenever motion was off, including when it was off
  because of a stored setting. Now says so.

### A harness note, since it cost three false readings

PowerShell 5.1 is DPI-unaware, so `SetCursorPos` and `GetWindowRect` both work in DIP there, while
**UI Automation reports physical pixels regardless** and `PrintWindow` needs physical. Mixing them
put clicks a quarter of the screen away. Separately, three "failures" during this step were the
harness rather than the product: `$script:` variables in PowerShell functions persist across calls,
so a counter and a window-handle lookup both returned stale values, and a window that is behind
another cannot be screenshotted with `CopyFromScreen` — `PrintWindow` with `PW_RENDERFULLCONTENT`
captures it regardless of Z-order. Verify the instrument before believing the result.

## Step 10 — what the notification area is actually carrying, and what language it is in

Reference machine as above. The bar was run on the secondary monitor for this step
(`\\.\DISPLAY2`, 1920 x 1080 at 100%), which is also the first time the reserved rectangle has been
measured there: `x=2560 y=1223 w=1920 h=48`.

### Every notification icon, by GUID

The question was the missing network indicator, and the honest way to ask it is to name every icon
that arrives rather than to look for the one that does not. Eight icons, unchanged across three
runs:

| Title | GUID | Pinned |
| :---- | :---- | :---- |
| `Speakers (USB): 38%` | `7820ae73-23e3-4229-82c1-e41cb67d5b9c` (**volume**) | yes |
| Safely Remove Hardware and Eject Media | `7820ae78-23e3-4229-82c1-e41cb67d5b9c` | no |
| `3 apps are using your microphone` | `7820ae82-23e3-4229-82c1-e41cb67d5b9c` | no |
| Discord | `699e423d-73da-42ba-b94e-881ff8672467` | no |
| Ascent | all zeroes | no |
| OBS Studio | all zeroes | no |
| NVIDIA Broadcast | all zeroes | no |
| Packaged app | all zeroes | no |

**No network icon, and `NETWORK_GUID` is already in the pinned list** — so one would land in the
right place if it arrived. Volume does arrive, carrying `VOLUME_GUID`, which rules out "Windows 11
no longer sends system indicators" as an explanation. The machine is on Ethernet; whether that is
the reason is still open, and the instrument that would answer it now runs all session rather than
for the first six collection changes. Three of the eight carry all zeroes, which is why icons are
keyed for logging by GUID **and** path — keyed by GUID alone they collapse into one entry and only
the first arrival is ever reported.

### The input language, and a list that was the wrong list

`NOB`, read from the foreground window's layout — the same string the Windows taskbar shows. It is
not a notification icon and never will be: it is the Text Services Framework input indicator, which
Explorer draws itself.

**The layout list has two plausible sources and they disagree.** Measured here:

| Source | Count | Contents |
| :---- | :---- | :---- |
| `GetKeyboardLayoutList` | 4 | `04140414` nb-NO, `08090809` en-GB, `04090409` en-US, `04140409` en-US on the Norwegian device |
| `HKCU\Keyboard Layout\Preload` | 2 | `00000414`, `00000809` |

The first reports every layout *loaded in the session*, including ones some other process asked
for. Cycling through it shows `ENG` three times in a row, which reads as a control that has stopped
working — and two of those three would put the user on a US key mapping they never chose, with the
indicator still reading `ENG`. `Preload` is the user's own list, in the user's own order, and is
what Windows' indicator walks. That is now the source, with the enumeration kept as a fallback.

The three-letter name is the **ISO** one, not .NET's `ThreeLetterWindowsLanguageName`: they
disagree on exactly this machine's languages, `NOB` against `NOR`. English agrees under both, so a
check written against English alone would have passed either way.

## Step 11 — where icons actually come from, and why two of them were broken

Two faults reported together: File Explorer's icon changing with the folder, and icons that
sometimes never appear. Measured with a read-only probe over every visible top-level window —
`WM_GETICON` via `SendMessageTimeout` with `ABORTIFHUNG`, `SHGetPropertyStoreForWindow` for the
App User Model properties, `SHGetFileInfo` for the executable, each under its own timeout.

### Most windows have no icon of their own

| Process | Class | `WM_GETICON` | Exe icon | AUMID |
| :---- | :---- | :---- | :---- | :---- |
| LeagueClientUx | `RCLIENT` | yes | ok | `c:
iot games\...\leagueclient.exe` |
| explorer | `CabinetWClass` | yes | ok | — |
| firefox | `MozillaWindowClass` | yes | ok | `308046B0AF4A39CB` |
| packaged app | `Chrome_WidgetWin_1` | **none** | ok | — |
| Riot Client | `Chrome_WidgetWin_1` | **none** | ok | — |
| steamwebhelper | `SDL_app` | **none** | ok | `Valve.Steam.Client` |
| JagexLauncher | `..._SSNSkinWindow` | **none** | ok | — |
| SystemSettings | `Windows.UI.Core.CoreWindow` | **none** | ok | — |
| ApplicationFrameHost | `ApplicationFrameWindow` | **none** | ok | `windows.immersivecontrolpanel_...` |
| TextInputHost | `Windows.UI.Core.CoreWindow` | **none** | ok | — |

**Seven of ten return nothing**, every one of them Electron, CEF, SDL or UWP. So the executable
fallback was not a fallback at all — it was the main path for most of the row, and it was the path
with no timeout, no retry and a global lock around it.

**`System.AppUserModel.RelaunchIconResource` is set by none of them.** It is the documented way for
an application to tell a taskbar what its button icon is, and on this machine nothing uses it. It
is therefore *not* implemented; it would be the correct first source if a real application were
found using one, and it belongs on the worker rather than in the candidate list, because reading a
window property store is itself a cross-process call.

### Explorer advertises the folder, not itself

Two windows open, same process, icons hashed straight out of the returned `HICON`:

| Source | Icon |
| :---- | :---- |
| "Downloads — File Explorer", `WM_GETICON ICON_BIG` | `2A9B2950B140` |
| "Videos — File Explorer", `WM_GETICON ICON_BIG` | `BC9E6FED7231` |
| class icon, `GCL_HICON` | none |
| `explorer.exe` via `SHGetFileInfo` | `F04B987E9EBB` |

Three different icons, and only the last one is stable. **Windows 11 does the same thing
deliberately** — a dynamic taskbar icon added around mid-2025, with no setting to disable it — so
this is a fidelity question rather than a plain bug. What *was* a plain bug is that a slot grouping
several windows took whichever came first in ManagedShell's collection, so the icon also changed
when an unrelated Explorer window opened or closed.

### Verified on hardware, both ways

The bar run with two folders open, captured at Tier 0 with a 4× gain so the monochrome row is
legible, and then run again with the new `applicationIcons` setting off:

| `applicationIcons` | Fourth slot draws |
| :---- | :---- |
| `true` (default) | the File Explorer application icon, unchanged as windows opened and closed |
| `false` | the **Videos folder** icon — the fault, reproduced on demand |

One setting apart, same machine, same windows. The diagnostic line also shows the icon arriving at
`48x48` rather than ManagedShell's 32, which is the new extraction path being the one in use.

**And the freeze did not recur.** `Ascent.exe` — the executable `SHGetFileInfo` never returns for —
was running and in the notification area throughout, and the bar came up normally. It is reached on
a worker now, abandoned after four seconds, and never asked again.

## Step 12 — what a screen reader actually hears

Measured with UI Automation, which is the API Narrator reads, so this is not an approximation of
the accessibility story — it is the accessibility story. The probe walks the control view of
Umbra's own window and prints every element's name, automation id, keyboard focusability and
supported patterns.

**Before.** Worse than silence:

```
List      name=""  id=WindowList
  DataItem  name="Umbra.Shell.Tasks.TaskbarSlot"
  DataItem  name="Umbra.Shell.Tasks.TaskbarSlot"
  DataItem  name="Umbra.Shell.Tasks.TaskbarSlot"     ... once per icon
List      name=""  id=TrayHost
  DataItem  name="ManagedShell.WindowsTray.NotifyIcon"
```

Every application on the bar announced as the same .NET type name. A screen reader would read
that aloud, identically, for every icon — which is less useful than announcing nothing, because
it sounds like information.

**Why.** WPF names a generated `DataItem` peer by calling `ToString()` on the bound object, and
the default returns the type's full name. Nobody had overridden it.

**After.**

```
List      name="Open and pinned applications"  id=WindowList
  DataItem  name="Firefox"
  DataItem  name="Discord"
  DataItem  name="Packaged app"
  DataItem  name="NVIDIA Broadcast"
  DataItem  name="Jagex Launcher"
  DataItem  name="Ascent"
List      name="Notification area"  id=TrayHost
  DataItem  name="Speakers (USB): 22%"
```

Two different fixes, and the difference matters. The slots are named by overriding
`TaskbarSlot.ToString()`. The tray icons could not be — `NotifyIcon` is ManagedShell's type — so
their name is set on the **container** through `ItemContainerStyle`, not on the template's root
`Grid`. **A name set on a plain `Grid` is never read**: a `Grid` is not in the control view, so
automation never surfaces it. That was the first attempt and the tree came back unchanged.

**What is still missing, and the probe says so plainly.** Nothing on the bar is keyboard
focusable and nothing supports `Invoke`. The Start button, the show-desktop sliver, the clock and
the volume/network cluster do not appear in the control view at all, for the same reason the tray
template's `Grid` did not — they are `Grid`s with mouse handlers. Promoting them needs real
controls or custom `AutomationPeer`s, which is §11.2 Q7/Q8 proper and is not done. **Naming is
not operating.**

## Step 13 — §11.2 Q2, and why its premise does not hold

Q2 asks whether ManagedShell's permanent 100 ms timers defeat display idle, and §6.6 rests on it:
WOLED panels run their own compensation cycles while idle or on standby, so a process that keeps
the display awake would suppress a mitigation far more effective than anything Umbra does. It has
been the project's largest open unknown for weeks.

**It was never going to be answered by running the bar for a week, and it does not need to be.**

**What actually resets the display idle timer.** Microsoft's own documentation for
`SetThreadExecutionState` is explicit: "The system automatically detects activities such as local
keyboard or mouse input, server activity, and changing window focus. **Activities that are not
automatically detected include disk or CPU activity and video display.**" A timer is CPU activity.
Twenty wake-ups a second are twenty pieces of exactly the thing Windows does not count.

Display idle is reset by user input, or by an explicit request — `SetThreadExecutionState` with
`ES_DISPLAY_REQUIRED`, or a power request through `PowerSetRequest`.

**Nobody here makes one.** Audited:

| Searched | `SetThreadExecutionState` / `PowerSetRequest` / `ES_DISPLAY` |
| :---- | :---- |
| `src/` — all of Umbra | none |
| ManagedShell 0.0.370, all eight assemblies | none |

So on the mechanism Q2 names, the answer is no: the timers cannot defeat display idle, because
CPU activity is not what the display idle timer watches.

**What is still open, stated narrowly.** Two things, and neither is the one §6.6 feared:

- **Power.** Twenty wake-ups a second keep the CPU out of its deeper idle states. That is a
  battery and thermals question, not a panel-life one, and it is real but much smaller.
- **Empirical confirmation.** `powercfg /requests` from an **elevated** prompt lists every process
  holding a DISPLAY request and would settle it against the running system rather than against
  documentation and a code audit. It needs administrator rights, so it has not been run here.

**And the reason this sat unanswered is worth keeping.** The obvious experiment — leave the bar
running and see whether the display sleeps — could never have worked on this machine:

```
powercfg /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE
    Current AC Power Setting Index: 0x00000000     (never turn the display off)
    Current DC Power Setting Index: 0x00000258     (600 s)
```

On AC, which is how a desktop runs, **this machine never turns its display off at all.** A
before-and-after test would have produced two identical readings and been reported as "the timers
make no difference", which is the right answer arrived at by an instrument that could not have
produced any other. This project's own rule, again: measure the thing, then check the measurement
could have detected it.

## Step 14 — Explorer's auto-hide, restored on the second machine

**2026-10-16, the 2256×1504 laptop at 150%**, which has auto-hide **off** (`StuckRects3` byte 8 =
`0x02`). The development machine has it on, so every earlier exit there restored auto-hide to
auto-hide, and a guard that did nothing would have read exactly the same. This is the first run
where the restore had something to do. Probed from a per-monitor-v2 DPI-aware process (a
short PowerShell script, not checked in, that sets the DPI awareness before creating any window):
`ABM_GETSTATE`, the work area, Explorer's `Shell_TrayWnd` rectangle, and `WindowFromPoint` at two
heights — y=1502, the bottom pixel row, and y=1480, inside where a visible taskbar would be. Each
poll started a fresh `powershell.exe`, so the probe cannot resolve anything faster than about a
second.

**The clean exit is the Debug build's dev exit event** (`DevControl` → `CloseForShutdown`, what
`tools/dev-restart.ps1` sends), not the menu's **Exit**. A release bar has no such event; the two
end in the same shutdown, but the menu item itself is not what was measured.

| | clean exit | `Stop-Process -Force` |
| :---- | :---- | :---- |
| `ABM_GETSTATE` after | 0 | **1 (auto-hide)** |
| Explorer's `Shell_TrayWnd` | (0,1432)–(2256,1504), visible | (0,1502)–(2256,1574): 2 px on screen |
| Work area | 1432 tall, reserved | 1504, nothing reserved |
| `WindowFromPoint` at y=1480 | `Shell_TrayWnd` | `Progman` |
| `StuckRects3` byte 8, `TaskbarAl` | `0x02`, 0 — unchanged | `0x02`, 0 — unchanged |
| State file | deleted | still present |
| Explorer's taskbar back | by the first probe, 1.4 s after (an upper bound) | not until Umbra is relaunched |

**A clean exit restores correctly.** `ExplorerStateGuard.Restore` put Explorer back to auto-hide
off, reserving its 72 px, by the first probe after the exit, 1.4 s later — an upper bound, since
the probe cannot resolve less. The log has the clean shutdown 150 ms after the request.

**A hard kill does not, and the registry does not show it.** The kill leaves Explorer in the
*runtime* auto-hide ManagedShell set to release the work area, while every persisted value still
reads as the user had it. Unchanged at +2 s in the capture; QA reports it still unchanged 15 s
after the kill, and the next launch's recovery line, 45 s later, is consistent with that. The next launch finds the state file, logs
`recovered Explorer taskbar state from a previous unclean exit`, and repairs it — so the damage
lasts until Umbra is started again, or for the rest of the session for someone who does not.

**The bottom-pixel probe alone would have passed it.** The auto-hidden taskbar leaves a 2 px sliver
of itself on screen, so `WindowFromPoint` at y=1502 reports Explorer's `Shell_TrayWnd` either way.
The y=1480 probe and the window rectangle are what tell a visible taskbar from a hidden one. Any
repeat of this measurement needs both. **The QA script's own pass criterion did pass it**: it
judged the taskbar back on visibility plus the bottom pixel, and printed `Explorer taskbar back at:
0.6 s` for the kill — the broken case — against 1.43 s for the clean exit that actually worked.

**Not measured:** whether hovering the edge reveals the auto-hidden taskbar (synthetic input cannot
trigger the reveal), and whether Explorer writes the runtime
auto-hide into `StuckRects3` on sign-out or an Explorer restart. If it does, the damage outlives the
session for a user who never runs Umbra again.

## Still open

- **§11.2 Q2 — do ManagedShell's 100 ms timers defeat display idle?** Not yet measured. Only the
  taskbar watchdog runs at present, so measuring now would attribute its cost separately from the
  tray timer that arrives at step 7.
- **A hard kill leaves Explorer's taskbar auto-hidden until Umbra is next started.** Step 14. No
  code inside the process can fix it, because the process is gone; it needs something outside it.
