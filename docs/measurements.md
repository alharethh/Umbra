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

**This confirms §5.3's own instruction rather than contradicting it.** The spec says outline mode
must stay "a per-icon opt-in, not a global default — it costs recognisability unevenly and users
should be able to apply it to their worst offenders only". The data says the same thing
quantitatively: worth it for two icons here, worthless for two others, and a recognisability cost
everywhere. The global toggle currently in the context menu is therefore the wrong shape; the
per-icon setting at build order step 9 is the right one.

It also revises the premise. §5.3 assumes pin sets dominated by solid-fill logos; modern
application icons largely are not that, so the headline 70–85% is a best case rather than a typical
one. Worth saying plainly in the README alongside the §2.4 caveats.

**Known quality problem.** Interiors look poor on gradient-heavy artwork. Internal edges are found
by thresholding the luminance difference between neighbours, and a smooth gradient crosses that
threshold repeatedly, producing banding and speckle rather than a clean line. Fixable with
non-maximum suppression or a sharper gradient test — but not worth the work until per-icon opt-in
exists and someone wants it on more than two icons. §11.2 Q10 stands: silhouettes read well,
interiors do not.

## Still open

- **§11.2 Q2 — do ManagedShell's 100 ms timers defeat display idle?** Not yet measured. Only the
  taskbar watchdog runs at present, so measuring now would attribute its cost separately from the
  tray timer that arrives at step 7.
- **Crash recovery on a machine with autohide off.** The reference machine has autohide already
  enabled, so restoring is a no-op there and a broken guard would be invisible.
  `ExplorerStateGuardTests` covers the logic against a fake; the live path is unproven.
