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

## Still open

- **§11.2 Q2 — do ManagedShell's 100 ms timers defeat display idle?** Not yet measured. Only the
  taskbar watchdog runs at present, so measuring now would attribute its cost separately from the
  tray timer that arrives at step 7.
- **Crash recovery on a machine with autohide off.** The reference machine has autohide already
  enabled, so restoring is a no-op there and a broken guard would be invisible.
  `ExplorerStateGuardTests` covers the logic against a fake; the live path is unproven.
