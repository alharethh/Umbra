# Umbra

An OLED-optimised taskbar replacement for Windows 11. Two reasons to exist, independent of each other:

1. It can sit on **any screen edge**.
2. It is designed from the ground up to **minimise OLED differential wear**.

**Stack:** C# / WPF, .NET 10, with [ManagedShell](https://github.com/cairoshell/ManagedShell) (Apache 2.0) as a NuGet dependency for tray hosting, the tasks service, and AppBar registration. Everything above that layer is original.

## Current state

Design is complete and verified. **Build order steps 1–9 are done and run on hardware**, bar one item: a bar on every monitor at once. The three queued bugs are fixed and the row now reorders freely — see “What comes next” for what is left. Step 6 (outline mode) was built, measured, and then removed — see §5.3. 296 tests green.

```
dotnet test Umbra.slnx
dotnet publish src\Umbra.Shell -c Release -o artifacts\Umbra    # runnable copy
```

Run it: `Umbra.exe` — configuration lives in the settings window now (right-click the bar → **Settings…**), stored in `%LOCALAPPDATA%\Umbra\settings.txt`. **Everything applies immediately except tray hosting**, which is built into the shell host at startup. The old flags still work and **override the file**, because `--no-motion` exists so the §11.2 Q1 experiment needs no rebuild and an experiment that lost silently to a stored setting would be worse than no flag: `[--edge left|top|right|bottom] [--no-motion] [--icon-size N] [--tray]`.

Right-click the bar for settings, the wear heatmap, the motion speed slider, and to exit; **right-click an icon for its jump list** — the application’s own tasks, then the application itself, then pin and close every window it has open (the item names the count, because closing six windows should not look like closing one). Per-window close lives in the preview. **Hovering an icon raises a live preview of its windows** after 450 ms; **drag any icon along the bar to reorder it** — pinned or not, the row is one order and the icons slide out of the way as you go.

**Idle icons ship shrunk, and grow past §7.1 on approach.** `DormantIconSize` defaults to 16 and
`BarWindow.ActiveIconExtent` is 26, not §7.1's 24 — a deliberate deviation taken by the project owner, so the bar ships with the shrink on rather than leaving it to be found. The icon is at the Windows 11 size for the whole of the time anyone is looking at it.

| Project | State |
| :---- | :---- |
| `Umbra.Core` | Complete for the spec as written. Every figure in `docs/verified-numbers.md` asserted |
| `Umbra.Shell` | WPF, `net10.0-windows`, ManagedShell 0.0.370 |

**What works:** any edge on any single monitor, space-reserving AppBar, Explorer suppressed and restored (including after a hard kill), one slot per application with pinning and free drag-reorder of every icon, click to activate or minimise, a window picker with live thumbnails for applications with several windows, launch from a pin, tier system on pointer proximity, monochrome normalisation, icon shrinks while dormant and grows on hover, wear accumulator and heatmap, drift and micro-jitter, Start button with press animation and Windows-blue while the menu is open, clock with seconds from Tier 1, a settings window, hover feedback on icons and on Start, dark context menus that sit above the Start menu, and one instance at a time.

**What does not:**

- **No system tray, and it is blocked by Windows rather than by us.** Umbra does claim the topmost `Shell_TrayWnd` and `FindWindow` does return it, but Windows 11 25H2 (build 26200) no longer delivers notification icons that way — proven with a controlled registration test. Off behind `--tray`. Do not re-debug without first checking whether the build has changed. `docs/measurements.md`.
- **No bar on more than one monitor at a time.** You can choose *which* monitor it occupies (Settings → Monitor, stored by device name so unplugging a screen cannot silently move it), but not run one per monitor. That needs a `BarWindow` and an AppBar registration per screen, a wear accumulator per screen, and a decision about which screen owns Start and the tray. It is also not purely mechanical: §11.2 Q12 asks whether the OLED treatment should apply per monitor at all, and on a non-OLED secondary the answer may be that it should not — so the design question wants settling before the code.
- **No keyboard path and no screen-reader support.** §11.2 Q7 and Q8, both untouched. A custom WPF bar is invisible to a screen reader without explicit `AutomationPeer`s.

Measured on a 2560×1440 panel at 125%. Every figure in `docs/measurements.md` came from this machine.

| Document | What it is |
| :---- | :---- |
| `docs/design-spec.md` | **The spec. Read this first.** v4, current |
| `docs/shell-integration.md` | How ManagedShell actually suppresses Explorer's taskbar and claims the tray, read out of its source. Constraints that fall out of it |
| `docs/verified-numbers.md` | Regression baselines for the test suite. Every figure independently computed |
| `docs/measurements.md` | What the bar actually did on real hardware, per build step. Observations, not targets |
| `docs/prototype.html` | Interactive browser prototype of the tier and motion systems. Open it, press F |
| `docs/archive/` | Superseded spec versions, kept for the changelog trail |

## Settled decisions — do not relitigate without cause

- **Default configuration:** bottom edge, left-aligned icons, `TaskbarAl = 0`.
- **The Start menu cannot be repositioned.** It is a separate process (`StartMenuExperienceHost.exe`) that anchors to Explorer's taskbar, and moving it requires DLL injection. This is **accepted**; all four edges ship; no custom launcher is built. Spec §7.0.
- **P5 — slot containment.** Drawn content may move only inside its own hit slot, never into a neighbour's. This is a product constraint and outranks wear arguments. It is why drift is ±5.46 px rather than v1's ±64 px.
- **Consequence of P5:** slot-bounded drift does nothing for icon interiors, and **nothing else does either.** §5.3's outline mode was the designated interior defence; it was built, measured, and removed in spec v4 — 34.6% of lit area across a real nine-icon row against the 70–85% the spec assumed, concentrated in two icons, and a recognisability cost on all of them. Interiors are now covered by the Tier 0 luminance floor alone. Accepted; do not re-add it without new evidence. Spec §5.3 and the v3 → v4 change log carry the reasoning, `docs/measurements.md` step 6 the numbers.

## Constraints that are easy to get wrong

These have all already caused a bug in an earlier draft or in a comparable project. Each one fails *silently*.

- **`float64` for the wear accumulator, never `float32`.** float32 drifts ~4.2% within 5M additions and then discards per-second increments entirely once the total reaches the millions. The heatmap still looks plausible.
- **Accumulate `L^n · Δt`, not `L · Δt`.** Linear accumulation measures emitted light, not wear, and under-ranks bright elements — exactly backwards.
- **`TextOptions.TextFormattingMode="Ideal"`.** The `Display` default snaps glyphs to the pixel grid, which silently defeats the fractional micro-jitter the whole motion layer depends on. Also `UseLayoutRounding="False"` and `SnapsToDevicePixels="False"` on the drifting content group.
- **`TextRenderingMode="Grayscale"`**, and verify no `ClearTypeHint` survives anywhere in the tree. Subpixel fringing is fixed-position differential wear.
- **Never register an autohide AppBar.** Windows allows one per screen edge and Explorer's taskbar holds that slot as a side effect of how it is suppressed. Use a normal space-reserving AppBar. The symptom otherwise is "a toolbar is already hidden on that side of the screen".
- **Constant-velocity drift, no easing.** Eased turnarounds concentrate residence time at the extremes, creating two hot bands — the opposite of the goal.
- **Decorrelate the clock by frequency, not phase.** A phase offset on the same frequency produces an identical accumulated wear distribution.
- **Crash recovery must restore Explorer's autohide state and `TaskbarAl`**, not just call `ABM_REMOVE`. A hard kill otherwise leaves the user's taskbar invisible.
- **Only one Umbra may run.** Two of them both register a space-reserving AppBar and both suppress Explorer's taskbar — and the second captures the taskbar state **already suppressed**, so on its exit it restores the user's taskbar to hidden. The damage outlives both processes. `Shell/SingleInstance.cs` holds a session-local named mutex, and the line that matters is *where* it is claimed: before `ShellHost` is constructed. A duplicate that gets as far as a host has already done it. The restart path passes `--wait-for <pid>` and claims with a 5 s grace, because it overlaps its predecessor on purpose — a zero timeout there would leave the user clicking “Restart Umbra” and getting no bar at all. An **abandoned** mutex is a successful claim, not a refusal; treating it as taken would make Umbra unstartable until logout after any hard kill, with the taskbar still suppressed.
- **Interpolate tier ramps in linear light**, not in sRGB values.
- **The clock uses half the short-axis amplitude** (`MotionDriver.ClockShortAxisScale`). §6.2's formula gives ±6.75 px across, which is ~17 physical pixels of vertical travel on a 48 px bar — unremarkable among icons, conspicuous on a line of text beside the screen edge, which gives the eye a fixed reference. It costs nothing that was working: a 24 px icon body is never uncovered on that axis anyway (§6.0), so the whole cost falls on features thinner than the travel, where peak duty doubles — measured 0.162 → 0.324 for a 1.5 px stroke — and only on horizontal ones, since vertical stems are held by the untouched long-axis drift.
- **The engaged band is zero, not §3's 40 px.** The bar reaches Tier 2 when the pointer is *on* it, not near it — a 40 px band lights the bar up as the pointer passes on its way elsewhere. `TierController` takes the band as a parameter so §3's figure stays the library default and the deviation is visible at the call site. The Tier 0 → 1 ambient ramp is deliberately untouched: it is the near-invisible grey step §2.3's duty budget is built on, and collapsing it would leave the bar with two states instead of three.

## The core library

`Umbra.Core` — a plain class library with **no WPF dependency**, plus xUnit tests. Everything the spec specifies lives here so it is testable in isolation:

```
Umbra.slnx                       (the .NET 10 SDK emits slnx, not sln)
Directory.Build.props
src/Umbra.Core/
  Color/      Rgb, Rgba, Srgb — transfer functions, relative luminance, scale, cap, linear-light lerp
  Wear/       WearModel (L^n), WearAccumulator (float64), FoldingAccumulator, DutyBudget, ScreenContext
  Layout/     SlotMetrics, BarMetrics, Rect, BarEdge, Alignment
  Motion/     MotionModel — constant-velocity triangle + 2 Hz micro-jitter; MotionClock (§6.6 pausing)
  Tiers/      Tier, TierPalette, TierController, TierEasing, NotificationBadge
  Icons/      IconBitmap, IconNormalizer — P90 plus the integrated-luminance diagnostic
tests/Umbra.Core.Tests/
```

The figures in `docs/verified-numbers.md` are asserted as tests. They were computed independently and are correct; if an implementation disagrees with them, the implementation is wrong. Tolerances are set to the precision each figure is published at — several of the budget totals are truncated rather than rounded, so the allowance is one unit in the last printed place.

The suite is written to fail on the silent traps, not just to cover the API. Before trusting a change here, mutate the thing under test and confirm something goes red: linear accumulation instead of `L^n`, an eased turnaround, an sRGB-space tier ramp. All three are caught. Note the ramp test has to be measured on the running indicator (black → `#404040`); adjacent greys like `#333333` → `#404040` put both interpolation methods inside the same 8-bit step, so a test written against that pair passes either way.

## Step 9 — what the window picker and the settings cost to get right

**DWM thumbnails fail by drawing nothing, never by erroring.** All three constraints are in
`Picker/DwmThumbnail.cs`, and each one produces an empty rectangle that looks like a layout bug:

- The destination window **must not be layered**, so the picker has no `AllowsTransparency`. Setting
  it silently blanks every thumbnail.
- Destination rectangles are **physical pixels relative to the client area**, while WPF lays out in
  DIP. Unscaled, thumbnails land at four fifths size in the corner at 125% and look perfect at 100%.
- DWM composites **over** our own content, so the title and close button sit in a header strip above
  each thumbnail rather than on top of it. The Windows taskbar does the same thing for the same
  reason.

Register thumbnails at `DispatcherPriority.Loaded`, not in the `Loaded` handler: `ActualWidth` and
`TransformToAncestor` are both read when placing one, and at `Loaded` the arrange pass has not
necessarily produced final values.

**The picker never activates** (`WS_EX_NOACTIVATE`, like the bar) because taking focus blanks the
running indicator and loses track of the window the user was looking at. That means it never gets a
deactivated event either, so it watches the cursor at 10 Hz while open and closes 600 ms after the
pointer leaves both it and the bar. The timer exists only while the picker is up.

**`EndDrag` clears its state before releasing mouse capture.** `ReleaseMouseCapture` raises
`LostMouseCapture` synchronously, which calls straight back in — releasing first ran the whole
method twice per drop, persisting and logging twice. It resolved correctly by accident.

**Settings that need a restart say so.** The edge, alignment, monitor and tray are baked into the
AppBar registration, and §6.3 forbids re-registering the reserved rectangle in steady state because
it reflows every maximised window. The restart passes `--wait-for <pid>` and the new instance waits
for the old one to exit before touching Explorer's state — without it, two instances briefly overlap
and the new one can capture the *suppressed* taskbar state as if it were the user's, then faithfully
restore the taskbar to hidden on its own exit. That is exactly the silent failure
`ExplorerStateGuardTests` exists for.

**WPF's stock `ComboBox` ignores `Background`.** Setting `Background` and `Foreground` on a dark
window leaves light-grey text on near-white chrome. The settings window carries a full
`ControlTemplate` for that reason; it was caught by screenshotting the window, not by reading it.

## Feedback and menus — two WPF traps worth not rediscovering

**`ItemContainerStyle` on a `ContextMenu` is applied to `Separator` too.** A style declared
`TargetType="MenuItem"` throws *"cannot be applied to type 'Separator'"* the moment the menu is
built, and because it surfaces on the dispatcher rather than at parse time it takes the whole
application down rather than failing to compile. `Rendering/MenuTheme.xaml` therefore uses implicit
type-matched styles, which cannot misapply.

**A `ContextMenu` cannot find the window's resources.** It lives in its own popup tree, and its
resource lookup does not climb into the window that owns it — so `menu.TryFindResource("DarkMenuItem")`
returns null for everything in `MenuTheme.xaml`. Silently: a null `Style` is legal and simply leaves
WPF's default template in place. **The result looked almost right**, which is what made it hard to
see — the default template also has an icon column and also highlights on hover, so the menu appeared
themed while ignoring every metric in the theme. The give-aways were rows measuring 22 DIP against
the style's 36, and separators running edge to edge instead of inset. Styles for items built in code
are resolved from the **window** (`BarWindow.MenuStyle`), which logs any key it cannot find, because
nothing else about this failure is visible. A `Style="{StaticResource ...}"` written in XAML is
unaffected — that is resolved at parse time, which is why the menu's own dark chrome was correct
throughout.

**`PrintWindow` renders a popup with an `Effect` incompletely.** It captured the context menu with
its last item missing, which looked exactly like a clipped menu; UI Automation reported all four
items present and none offscreen. Screenshot popups with `CopyFromScreen` from a DPI-aware process,
or ask UI Automation instead of counting pixels.

**A menu on a `WS_EX_NOACTIVATE` window will not close when you click elsewhere.** WPF dismisses a
context menu by capturing the mouse for its popup and watching for a click outside; the bar never
takes focus, so that capture is not honoured across processes and the menu sits open until you
click back inside Umbra. `Rendering/ContextMenuDismisser.cs` watches the pointer itself, exactly as
the picker does and for exactly the same reason. Both the cursor and the menu rectangle are read in
physical pixels, so no DPI conversion is involved anywhere in the comparison.

**Minimised windows still preview.** DWM usually holds the last composed frame, so
`DwmRegisterThumbnail` on a minimised window shows its contents rather than nothing. Skipping them
on `IsMinimized` — which the first cut did — made every minimised window preview as a grey app icon.
The icon fallback now only appears when DWM genuinely reports no content.

**Hover chips borrow the safety margin, and that is the whole budget.** The chip behind an icon is
concentric with it and drifts with it, so its edge sits at `e/2 + margin + peakOffset`. §6.2 holds
back 5% of the pitch — 2.2 px — inside the gutter, and `BarWindow.HoverChipMargin` of 2 px is the
largest value that fits at every icon size. `HoverChipTests` pins it, including a check that a wider
chip would fail.

## The motion speed slider is a development tool — **remove it before shipping**

Right-click the bar → **Motion speed…**. It winds motion time forward so the drift becomes visible;
at 1× there is nothing to see, which is P6 working as designed and is exactly why the instrument is
needed — "invisible" and "not running" look identical otherwise. `docs/prototype.html` has the same
control for the same reason.

**Project owner's decision (2026-08-18): keep it until the app is ready to ship, then strip it from
the production build.** It is deliberately self-contained so that removal is a clean deletion:

- `src/Umbra.Shell/Motion/MotionSpeedWindow.xaml` and `.cs` — delete both
- `MotionDriver.TimeScale` and `MaxTimeScale` — delete; restore the fixed 2 Hz timer interval
- `WearSampler.Suspended` — delete the public setter; `_suspended` stays for session lock
- The `Motion speed…` item in `BarWindow.xaml` and `OnMotionSpeedClick` in the code-behind

Above 1× it ticks at 60 Hz rather than §6.1's 2 Hz, and **wear recording is paused for as long as
the multiplier is above 1×** — simulated motion never happened, and writing it into the dataset that
has to answer §11.2 Q1 would be inventing evidence. It resets to 1× when the window closes.

## Icon size: shrink while dormant, full size on hover

`--icon-size N` sets the extent drawn while the bar is monochrome; it ramps back to the §7.1 default
of 24 px on `TierRender.ChromaBlend`, so the icon reaches full size exactly as it reaches full
colour. **The hit slot stays 44 px at every size, so clicking is never affected.** Default with no
flag is byte-identical to before.

The point is that §6.2 derives drift amplitude from the gutter, so a smaller icon travels further
and each screen pixel under it rests more. Under P3 the wear accrues at Tiers 0 and 1 (~100% duty
together), so shrinking only while dormant keeps the benefit and returns the visual cost.

**The payoff is far worse than the geometry suggests, and this is the thing to not re-derive.**
Measured against the real motion model over 24 h:

| Dormant icon | Permanently-lit core | Burn-in improvement |
| :---- | :---- | :---- |
| 24 px | 9.8 × 10.5 px | — |
| 20 px | 2.0 × 3.0 px | **1.00×** |
| 19.29 px | **gone** | **1.00×** |
| 18 px | gone | 1.04× |
| 16 px | gone | 1.25× |
| 12 px | gone | 2.32× |

The core vanishing is a *geometric* milestone that does not track wear — at 19 px the hottest pixel
is still lit 99.8% of the time. Do not quote it as a wear result. `IconSizeRampTests` pins this.

**The trap it hides:** a grown icon has a narrower gutter and so a smaller permitted amplitude.
Leaving the offset at the dormant value while the icon grows pushes it out of its slot — silently,
only at the drift turnarounds, and **only once the dormant size is 18 px or below**, so a check at
20 px reports a clean pass on broken code. `MotionDriver.Reemit()` is the guard; the escape is
asserted as a test so the guard cannot be quietly dropped.

## What comes next

**Updated 2026-09-19.** The three queued bugs are closed and icon reordering is done; what those
cost to get right is recorded below, because every one of them failed in a way that looked like
something else. What is left is the tray, then the list under "Then".

### Icon order — settled, and the three bugs that came out of it

**Project owner's decision, 2026-09-19: the row is not segregated.** Windows does not separate
pinned icons from running ones, and neither do we. There is one order covering both, every icon in
it can be dragged anywhere, and `Tasks/BarOrder.cs` owns it — deliberately free of WPF and
ManagedShell so the thing that decides where every icon sits can be tested without a window.

**The rule is one sentence: an icon holds its place for as long as it is pinned or running, and
joins the end when it first appears.** That settles the question the old layout dodged. An unpinned
application that closes leaves the row, so on reopening it is new and goes to the end — its old
position is not held, because nothing is drawn there to hold and a row reserving invisible gaps for
absent applications would be a worse surprise than the jump it replaces. Pinning is how a position
is made to stick. `BarOrderTests` pins all of it.

The pin file still records only pins, and now *follows* the row rather than dictating it:
`BarOrder.PinnedIn` reads the pinned subsequence out and `PinSettings.Reorder` writes it. So a pin
dragged past a running icon keeps its new relative order across a restart, which the segregated
layout had no way to express.

Three consequences:

- **Bug 1 — pinning an icon moved it to the far left. Gone by construction.** Pinning changes only
  which list a key is in, and layout does not consult that list. There is no code path left that
  could reintroduce it without deleting `BarOrder`. Covered by
  `BarOrderTests.Pinning_Does_Not_Move_The_Icon`, which reinstating the pins-first sort turns red
  along with three others. **Not yet watched happening on hardware** — the row itself has been.
- **Bug 3 — clicking a pinned application that is not running did nothing. Withdrawn.** The project
  owner misread the symptom; there was never a bug. The instrumentation added to `BarWindow.Launch`
  is kept, because a silent launch failure is worth catching if it ever is real.
- **Feature 1 — icon reordering. Done.** The drag no longer refuses unpinned icons, the dragged
  icon is carried under the pointer, and the rest of the row slides around it.

**Two traps this left behind, both worth not rediscovering:**

`PinSettings.Reorder` must keep a pin the row did not name. The obvious implementation builds a
fresh list from the keys it was handed, and any pin missing from those keys — one the row has not
caught up with — silently vanishes from the user's layout on the next write. Nothing looks wrong
until the next launch. Asserted by `A_Pin_The_Row_Did_Not_Name_Keeps_Its_Place`; deleting the rescue
line turns it red.

`Reconcile` and `Reorder` both report whether anything changed, and the model writes the pin file
only when they say yes. They run on every window event anywhere on the desktop, so a false positive
puts a disk write behind every window that opens.

### The row animates — `Rendering/SlidingStackPanel.cs`

Reordering used to be instantaneous, which reads as a glitch rather than as a move: nothing connects
where an icon was to where it went. The panel arranges exactly as a `StackPanel` would, then puts
the difference back as a `TranslateTransform` and animates it to zero over 180 ms.

- **Layout is never a frame out of date; only the paint lags.** That is what makes it safe to leave
  hit-testing alone — WPF hit-tests through a render transform, so a click during the slide lands on
  the icon the user can see.
- **The easing is not a breach of §6.1.** That rule is about drift, where eased turnarounds
  concentrate residence time at the extremes and burn two hot bands. This is a one-off transition
  between two positions the user chose: no turnarounds, no duty cycle. Same treatment the hover chip
  and the Start press already get.
- **Slides compose from the child's current visual position, not from where it was going to be.** A
  reorder arriving mid-slide is not exotic — it is every drag.
- **The dragged icon is held out of the animation** (`Hold`/`Carry`/`Release`) so the pointer owns
  it. When a reorder shifts its layout slot underneath it, the panel absorbs the jump, or it would
  flash a slot's width sideways in the frame before the next mouse move.
- **It works because slots are updated in place.** `TaskbarModel` moves slots rather than replacing
  them and WPF's generator moves the matching container — a container that was destroyed and
  recreated has nowhere to slide from.

### Bug 2 — context menus drew underneath the Windows Start menu. Fixed.

**The obvious fix does not work, and it reports success while not working.**
`SetWindowPos(HWND_TOPMOST)` on the popup's HWND returns `true`, the window really is topmost, and
the menu is still clipped by the Start menu's bottom edge. Measured, with the call logged to prove
it ran. `HWND_TOPMOST` is the top of the *ordinary* band; Windows 11 keeps the Start menu in a
higher Z-order band, and entering that one needs `SetWindowBand` plus a UIAccess manifest — a signed
binary installed to a trusted location. Not available to us, and not worth it.

**What actually fixes it is doing what the click should have done: closing Start.** Windows dismisses
Start whenever you click outside it. It does not do that for us, because the bar carries
`WS_EX_NOACTIVATE` and clicking it never moves the foreground, so Start is never told it lost
anything. `StartMenuWatcher.Dismiss` closes it, guarded by a fresh `Poll` — `ShellHelper.ShowStartMenu`
is a *toggle*, so calling it blind would open the very menu we are trying to be rid of.

It is wired to `ContextMenuOpeningEvent` at the window, not to `Opened`: dismissing after the popup
is up shows it clipped for as long as Start takes to animate away. `WindowOrder.Raise` still runs and
still sets the popup topmost — that is what keeps it above ordinary topmost windows, which is a real
case even with Start gone.

**Verify any change here against the Start menu specifically, and screenshot the precondition.** An
ordinary application window sits below the bar already and will report a pass whether the fix works
or not.

### The logo, and the title bar it sits in

`Assets/logo.svg` is the canonical artwork; `Assets/umbra.ico` is **rendered from the same geometry**
at nine sizes rather than downscaled from a raster, because resampling a 412 px PNG to 16 px turns
crisp corners into mush. The numbers were measured off the supplied artwork, not guessed — squares
at 7..194 / 216..402 horizontally, and corner samples showing **one 45° gradient across the whole
2×2 block**, not one per square. `ApplicationIcon` in the csproj puts it on the exe, and WPF windows
inherit it from there.

**The title bar is not part of the window's content.** It is drawn by the desktop compositor, which
knows nothing about `Background`, so a fully themed dark window still comes up with a white caption
and no amount of XAML reaches it. `Rendering/DarkTitleBar.cs` asks DWM instead: the immersive
dark-mode flag, plus Windows 11's caption, text and border colours taken from the window's own
brushes so the two cannot drift. Applied at `SourceInitialized`, not `Loaded` — the handle exists by
then and the window has not been presented, so the caption is never painted light first.

These windows are unconditionally dark rather than following the system theme: the bar they
configure is black by design, and a light settings window beside it would be the brightest thing on
the panel.

### Auxiliary windows come to the front — `Shell/WindowOrder.cs`

Settings, the heatmap and the motion speed window used to open *behind* whatever the user was
looking at. Same root cause as Bug 2: Umbra is never the foreground process, and Windows refuses a
foreground change to a process that does not already own the foreground. We look exactly like the
background application that restriction exists to stop.

The bar cannot simply take focus instead — that is what makes click-to-minimise work at all. So
`ShowInFront` attaches our input queue to the foreground thread's with `AttachThreadInput`, which
makes `SetForegroundWindow` legal for the duration, and detaches immediately. If the attach fails —
an elevated foreground window, or a desktop in transition — it falls back to toggling `Topmost`,
which reorders the window without asking anyone's permission. That is the difference between a
settings window the user has to hunt for and one they merely have to click. The fallback logs when
it is used; on this machine it has not been.

### An updated application changes identity, and the bar draws it twice

Reported as "discord on the right should merge with the pinned discord icon — the pin is just for
launching discord, and i cant close discord from it". One cause, both symptoms.

**A path-keyed application carries its version directory in its identity.** Discord's key went from
`Discordpp-1.0.9253\Discord.exe` to `Discordpp-1.0.9258\Discord.exe`, so the running
application stopped matching the pin. Two slots: a pin that could only launch it, and a running icon
that could not be pinned. The pinned one believed nothing was running, so it offered no Close.

**Healing only the pin's path cannot fix this, and the reason is worth keeping.** `UpdatePath` runs
when a pinned application is seen running — which is exactly what has stopped happening. It is
circular, and it fails by doing nothing at all. The **key** has to move, which is
`PinSettings.Rekey`, driven by `TaskbarModel.AdoptMovedApplications` before the row is reconciled.

`AppIdentity.IsSameApplicationMoved` is the rule, and it is deliberately narrow because it decides
that two different executables are one application: same file name, sibling directories, shared
parent. That is the shape a versioned install has and very little else. It is only ever asked about
pins whose own executable has already gone, so the alternative to adopting is a permanently broken
icon rather than a working one.

`BarOrder.Rename` keeps the position. Letting the reconcile drop the old key and append the new one
would also work, and would send a deliberately-placed pinned icon to the end of the row — once,
mysteriously, on whichever launch followed an application update.

Verified on hardware: one Discord icon, with a running indicator, whose menu offers Close.

### A pin’s recorded path goes stale, and everything about that failure is silent

Reported as "a weird logo to the right of firefox". It was Discord: pinned, not running, drawing an
empty placeholder — and doing nothing when clicked, which is why it had been clicked four times.

**A pin records the executable path of the window it was created from, and that path is not
stable.** Applications that update in place — Discord, Slack, Teams, anything on Squirrel — install
each release into its own versioned directory and delete the old one. Discord’s pin held
`app-1.0.9253\Discord.exe`; the disk had moved on to `app-1.0.9258`. A pinned application that is
not running has **only** that path to work from, for its icon and for its launch, so both failed
together.

**Windows does not have this problem because it does not pin the versioned path.** Discord’s Start
Menu shortcut is what survives a release, so that is what to ask. Two fixes, and both are needed:

- `ShortcutResolver` finds the application again when the recorded path has gone, by reading Start
  Menu `.lnk` files. **Matched on the shortcut’s own name, not on what it points at** — the obvious
  choice is the one that does not work, because the all-users Discord shortcut targets `Update.exe`,
  which is nothing like the `Discord.exe` the pin recorded. Consulted only when the path is actually
  missing: while it is good it is the better answer, being the executable the user’s own window was
  running from rather than whatever shortcut shares its name.
- `PinSettings.UpdatePath` heals the pin whenever the application is seen running, because a running
  window always knows where it really is. That is what stops it going stale again. It reports
  whether anything changed and writes only then — it runs inside the model’s rebuild, which happens
  on every window event anywhere on the desktop.

Verified on hardware: the icon came back, and a click resolved `app-1.0.9253` → `app-1.0.9258` and
started it.

The shell-link COM now lives in `Shell/ShellLinkInterop.cs`, shared between this and the jump list
reader — jump list tasks load from bytes through `IPersistStream`, Start Menu shortcuts from a file
through `IPersistFile`, and everything after the load is identical. A second copy of an interface
whose method *order* is the contract is a good way to acquire a silent memory bug.

**Caution when reading `bring-up.log`:** the test suite shares it. `StalePinTests` writes lines like
`pin path healed: Discord -> C:\Local\Discord\...` using invented paths, and they are
indistinguishable from real ones except by the path not existing on this machine. Check the
timestamp against when the bar was actually running before trusting a line.

### Jump lists — an application’s own tasks in the icon menu

Right-clicking an icon shows what the Windows taskbar shows: the application’s published tasks
("Open new tab", "New private window", "New Code Session"), then the application itself, then pin
and close. `JumpList/`.

**The documented API is a dead end, and it fails by succeeding.**
`IApplicationDocumentLists::GetList` returns `S_OK` and zero items for every application — measured
against Firefox, Discord and Explorer. It reports the *calling* process’s own lists; Explorer uses
something else for everyone else’s. A check that only tested for an error would have concluded the
feature was impossible.

**The data is on disk, and Windows will parse it for us.** Applications publish tasks through
`ICustomDestinationList`, which lands in
`%APPDATA%\Microsoft\Windows\Recent\CustomDestinations\<hash>.customDestinations-ms`. The
container format is undocumented — but the shell links *inside* it are not, and they start with a
20-byte signature (`HeaderSize` 0x4C then the link CLSID). So `ShellLinkReader` finds those offsets
and hands each one to `IPersistStream::Load` on a real `ShellLink`. **MS-SHLLINK is never
reimplemented here and therefore cannot drift from it.** The loader reads only as far as the link it
is given, so one link’s trailing bytes being the next link costs nothing.

Three things that are easy to get wrong, all found by measurement:

- **Titles are not one type.** The label is `System.Title` in the link’s property store, and
  applications write it as `VT_LPWSTR` or `VT_BSTR` as they please. Reading only the first gives
  Firefox its task names and Edge a column of blanks. `PropVariantToStringAlloc` exists for this.
- **The file name is a hash of the AppUserModelID, which is undocumented — and we often have no
  AUMID anyway.** So files are identified by content: the target of the first link is the
  application. Match on the full path *and* the bare file name, because they fail in opposite
  directions — packaged applications move into a new versioned directory on every update (one app’s
  path changed mid-project), and a UWP application’s "path" inside a link is its AUMID behind an
  invented `Desktop\` prefix that `GetFileName` strips off.
- **An application can have several files**, left by previous installs — two each for Firefox,
  Chrome and PowerShell on the development machine. Newest write wins.

Cost: reading the first link of all 44 files takes ~95 ms, which is too much for every right-click,
so the index is built once and then only re-reads files whose timestamps moved. Jump lists change
whenever you open a document, so a full rebuild per open would put the 95 ms back.

**Known limitation: every entry appears under one "Tasks" heading.** Scanning for links skips the
container, and the container is what carries the category names — so Firefox’s frequently-visited
sites, which it publishes in the same file, are listed under "Tasks" rather than "Frequent". File
order is preserved, so the grouping still reads correctly; only the heading is wrong. Fixing it
means parsing the container, which is the part that is genuinely undocumented.

Verified end to end on hardware: one packaged app’s four tasks and Firefox’s ten render as the screenshots
show, and clicking "Open new tab" opened one.

### The running indicator — two bugs the 48 px reserved rect uncovered

Reported as "the small white line under open apps glitches — sometimes drawn wrong, sometimes not
drawn at all, on any edge". Two separate causes, both latent for a while.

**1. Short-axis drift carried it off the bar.** Every other drifting element is centred in the
content band, so the band's own clearance absorbs the travel — a 16 px icon in a 48 px band has
16 px of room against a peak short-axis excursion of about 8.75 px. The pill is the exception: it is
pinned to the *outer* edge of the bar with 1 px of margin, because that is where Windows 11 puts it.
8.75 px of travel took it past the edge of the window, where it was clipped — partly for most of the
drift cycle and entirely at the extremes. No pattern was visible because the drift period is
minutes long.

**This became reachable when `BaseHorizontalReserved` was cut from 56 to 48.** Those 8 px were the
headroom that used to absorb exactly this. The reasoning recorded for the change weighed the icon,
which is centred and had clearance to spare, and not the one element that had none. `PillOffset` is
now its own transform source, clamped on the short axis by `BarWindow.ClampPillOffset` — P5 applied
per element rather than per slot. **Clamped, not zeroed**: the long axis is untouched, and it is the
half that does the work, since the pill is 16 px long and 3 px thick.

**2. On left and right edges it was drawn sideways.** `IndicatorWidthConverter` had a fixed
`AlongBar` flag, and the "along" instance was wired permanently to `Rectangle.Width`. That is right
on a horizontal bar and exactly wrong on a vertical one, where "along" is the Y axis — the pill came
out as a 16×3 dash lying across the icon instead of a 3×16 line beside it. The converter now takes
the bar's `IsVertical` as a third binding and works out its own axis: `along = Horizontal !=
vertical`. The `PillWidth` / `PillHeight` properties had encoded the correct swap all along and
nothing was bound to them.

Verified on hardware on both axes. `RunningIndicatorTests` pins the clamp, sweeping the whole
excursion in both directions rather than testing the extremes, because a sign error shows on one
side only; removing the clamp turns four of them red.

### Settings apply live — including the edge and the monitor

Only tray hosting still needs a restart. Everything else — icon size, motion, row alignment, screen
edge, monitor — takes effect on the spot, through `BarWindow.ApplyPlacement`.

**§6.3 is not violated by this, and the distinction is the whole point.** What it forbids is
re-registering the reserved rectangle *in steady state*, because that reflows every maximised window
on the desktop. A user deliberately moving the bar is not steady state, and the reflow is exactly
what they asked for. The prohibition is against doing it behind their back.

Three tiers of cost, and they are not the same:

- **Alignment** touches nothing outside the bar. Both alignments reserve the same strip and only
  move the icons inside it, so no AppBar call happens at all. It was on the restart list for no
  better reason than sitting next to the edge in the settings window.
- **Edge and monitor** go through `Reregister`: `UnregisterAppBar`, set `Screen` / `AppBarEdge` /
  `DesiredHeight` / `DesiredWidth`, then `RegisterAppBar` and `UpdatePosition`. All of those are
  protected members of `AppBarWindow`, which is our own base class. **Unregister first** —
  registering a second rectangle before releasing the first leaves Explorer reserving space for an
  edge nothing occupies, and it survives until Explorer restarts.
- **Tray** is not live and is not worth making live: it collects nothing on 25H2.

Two things that are easy to get wrong here:

`RaisePlacementProperties` raises **all** the bound layout properties, not the subset that depends
on the edge. There are about thirty; they are cheap property reads; they happen only when the user
changes a setting. A hand-maintained list of which property reads what would drift out of sync and
leave one control sitting where the old edge used to be.

**The wear accumulator is sized to the reserved rectangle in physical pixels, so a new edge is a new
grid.** `StartWear` persists the outgoing dataset before dropping it — `WearStore.LoadCompatible`
will refuse to load it back, correctly, since per-pixel wear from a horizontal bar means nothing on
a vertical one, but the record on disk should still be whole up to the moment the user moved the
bar. It runs at `DispatcherPriority.Loaded`, so the window has actually resized before it measures.

**Verified on hardware both ways:** bottom → left (axis flip, reserved extent 48 → 56, accumulator
2560×60 → 70×1440) and back, with the desktop reflowing correctly each time and no restart.

### The restart button is always enabled

It used to grey out unless a pending change needed a restart. That made the one control that
reliably fixes a misbehaving bar unavailable exactly when it was wanted — and there is no other way
to restart Umbra, because the bar has no window to close. Now that placement applies live, nothing
routine needs it at all, which is more reason for it to work when asked rather than less.

### Feature 2 — the tray, properly

The deep dive. Everything known so far is in `docs/measurements.md` step 7 and is worth reading
before starting, because most of the obvious explanations are already eliminated by controlled
experiment: the Z-order interception genuinely works (`FindWindow("Shell_TrayWnd")` returns Umbra's
window, `WS_EX_TOPMOST` set, `NotificationArea` initialised and not failed), and a brand-new
`Shell_NotifyIcon` registration made while Umbra held that window still delivered nothing. Double
initialisation, a missing `TaskbarCreated` broadcast, a stale ManagedShell, a pinned/unpinned filter
and elevation are all ruled out.

The conclusion recorded there is that **Windows 11 25H2 (build 26200) no longer routes notification
icons to whichever window claims the class name**, which is exactly the risk
`docs/shell-integration.md` §2.3 flagged as never contractual. A deep dive therefore starts from
"what does 25H2 actually do instead", not from "what is wrong with our wiring". The rendering side
is already complete and untested against real data, so if a delivery mechanism is found the display
work is largely done. It is behind `--tray` / the `tray` setting because running it costs a
permanent 100 ms timer for nothing.

### Then

1. **A bar per monitor.** Monitor *selection* works; running several bars at once does not. See "What does not" above — §11.2 Q12 should be settled first, because the answer may be that a non-OLED secondary wants no OLED treatment at all.
2. **Keyboard and accessibility** — §11.2 Q7 and Q8. `Win+`number, `Win+T`, and `AutomationPeer`s. The settings window is ordinary WPF and is already reachable; the bar is not.
3. **Strip the motion speed slider** when shipping — see the section above for the four places.

The §11.2 Q1 experiment — a week with drift on against a week with it off, compared on the heatmaps — is **deferred by the project owner, not cancelled**. `--no-motion` exists so it needs no rebuild when the time comes.

Five traps worth not rediscovering:

- **A WPF `Binding` with no `Path` never updates.** It resolves the object once and freezes, because there is no property to watch. This silently killed the running indicator: correct widths, correct fill, nothing drawn.
- **Do not share one `Transform` instance across many elements.** Assigning the same `ContentOffset` to every slot's indicator stopped all of them rendering. Bind `X`/`Y` into a per-element `TranslateTransform` instead — that is why the icon, the Start glyph and the indicator each have their own.

- **PowerShell is DPI-unaware, so its `SetCursorPos` coordinates are virtualised.** Asking for y=1270 at 125% puts the cursor at physical 1587. Any harness that drives the pointer must work in DIP from a DPI-unaware process, or physical from an aware one, and must not mix them. This produced a convincing false bug report against the tier system.

- Do not set `InvariantGlobalization` anywhere in this solution. See the comment in `Directory.Build.props` — it is an application-level runtime switch and it kills WPF popups.
- **Synthetic mouse input does not trigger Explorer's autohide reveal.** Neither `SetCursorPos` nor `SendInput` works. Any measurement of edge behaviour needs a real hand on the mouse, and needs to verify its own precondition — see the methodological note in `docs/measurements.md`.
- **The bar carries `WS_EX_NOACTIVATE`**, so it never takes focus — that is what makes click-to-minimise work at all. A consequence: its context menus do not take keyboard focus either, so `SendKeys` cannot drive them. Menu items have to be clicked.

Four things are waiting on a decision or a measurement rather than on code:

- **Explorer's autohide restore is unproven on this machine.** The development machine has autohide already enabled (`StuckRects3` byte 8 = `0x03`), so restoring is a no-op and a broken guard would look identical to a working one. `ExplorerStateGuardTests` covers the logic against a fake; the live path still wants one run on a machine configured the other way.
- **§11.2 Q2 — do ManagedShell's 100 ms timers defeat display idle?** Only the taskbar watchdog runs so far; the tray Z-order timer arrives at step 7. Worth measuring now, with one timer, so the second one's cost is attributable.
- **Short-axis drift amplitude.** §6.2's formula gives ±4.8 px and micro-jitter adds ±1.95 px on the same axis, for 6.75 px of travel. §6.2 annotates the amplitude "≈ ±4 px" and §8 credits the reserved band's 8 px (4 per side) to short-axis drift. Neither matches 6.75. P5 is not at risk — the travel stays inside the slot — but the reserved band does not cover what §8 says it covers. `BarMetrics.ShortAxisFitsReservedHeadroom` returns false and a test records it.
- **Short-axis waveform.** §6.2 puts the short axis under the same "triangular, constant velocity" heading as the long axis; `docs/prototype.html` drives it with a sine, whose residence density piles up at both turnarounds — the exact failure easing was removed for. `MotionModel` uses a triangle. Its period (0.61 × the long-axis period) is the prototype's ratio and is not fixed by the spec.

## Working notes

- The spec's numbers are targets to build against and tune, not measured results. Where it says "measure this", it means it.
- Of spec §11.2's three highest-priority unknowns, Q3 (bottom-edge contention) is now answered — it does not occur. The two that remain are whether the motion layer earns its complexity at all, and whether ManagedShell's 100 ms timers defeat display idle. The first needs the wear accumulator running on real hardware, which is why instrumentation comes before the motion layer in the build order.
- **Measure the thing, then check the measurement could have detected it.** Q3 produced two clean-looking zeroes from runs whose trigger never fired. An instrument that cannot fail is not reporting a pass.
- Do not overclaim in the README. The bar is 86–191× below light-mode desktop content but ~5.8× *above* a dark-mode application surface. The wear thesis is real for dark-mode users and close to irrelevant for light-mode ones. Say so.
