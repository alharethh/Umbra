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

**Result: the predicted contention does not occur on this machine. Q3 is resolved, not deferred.**

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

**Why it does not happen.** The prediction assumed Explorer's taskbar stays a live window that
reveals on hover. It does not: ManagedShell hides it with `SetWindowPos(SWP_HIDEWINDOW)`, and a
hidden window is not eligible for the autohide reveal at all. The watchdog never fires because
nothing re-shows the window — 0 transitions in 40 s confirms it. The race has no second party.

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

## Still open

- **§11.2 Q2 — do ManagedShell's 100 ms timers defeat display idle?** Not yet measured. Only the
  taskbar watchdog runs at present, so measuring now would attribute its cost separately from the
  tray timer that arrives at step 7.
- **Crash recovery on a machine with autohide off.** The reference machine has autohide already
  enabled, so restoring is a no-op there and a broken guard would be invisible.
  `ExplorerStateGuardTests` covers the logic against a fake; the live path is unproven.
