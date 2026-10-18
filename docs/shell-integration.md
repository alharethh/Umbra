# Q1 & Q2 — Shell Integration Findings

**Scope:** Resolves §11 Q1 (Start menu anchoring) and Q2 (tray hosting alongside a hidden Explorer taskbar) from the OLED taskbar design spec v2.
**Method:** Source reading of ManagedShell (`ExplorerHelper.cs`, `TrayService.cs`, `NotificationArea.cs`, `ShellHelper.cs`), plus the Windhawk vertical/top taskbar mods as an existence proof for what Start menu repositioning costs.
**Status:** Q2 answered and solved. Q1 answered and **not** solvable within the current architecture — it forces a product decision.

---

## Summary

| | Verdict |
| :---- | :---- |
| **Q2 — tray hosting** | Solved. ManagedShell already does it, by Z-order interception of `FindWindow("Shell_TrayWnd")` plus proxying unhandled messages to Explorer. Cost: two permanent 100 ms polling timers and a dependency on undocumented behaviour. |
| **Q1 — Start menu** | Cannot be repositioned without DLL injection into `StartMenuExperienceHost.exe`. On any non-bottom edge the Start menu will open at the bottom of the screen. This makes a custom launcher **necessary, not optional**, for the project's headline feature. |
| **Unexpected finding** | The bottom edge — designated the reference orientation in §7.1 — is the *worst* placement, because it shares an edge with the autohidden Explorer taskbar. Left/right/top are cleaner. This should change the build order. |

---

## Q2 — Tray hosting and Explorer taskbar suppression

### 2.1 How the Explorer taskbar is suppressed

`ManagedShell.AppBar.ExplorerHelper` does three things, in order:

1. **`SetTaskbarState(ABState.AutoHide)`** via `SHAppBarMessage(ABM_SETSTATE)`. This is the load-bearing call, and it is easy to miss. Setting Explorer's taskbar to autohide is what **releases its reserved work area** — without it, both bars reserve space and every maximised window is short by two taskbar heights.
2. **`SetWindowPos(..., HWND_BOTTOM, SWP_HIDEWINDOW)`** on `Shell_TrayWnd`, on the Start button window (class atom `0xC017`), and on every `Shell_SecondaryTrayWnd` — enumerated in a loop, so 3+ monitor setups are handled.
3. **A `DispatcherTimer` at 100 ms** that polls visibility and re-hides. Explorer re-shows its taskbar routinely; the watchdog is not defensive coding, it is required.

The whole path is skipped when `EnvironmentHelper.IsAppRunningAsShell` — if registered as the actual shell, Explorer's taskbar never exists.

**Original state is saved** (`startupTaskbarState`) and restored on shutdown, which matters for §11 Q4's crash-recovery requirement: a hard kill leaves the user's taskbar autohidden and invisible until they fix it manually. Our watchdog needs to cover this too, not just `ABM_REMOVE`. **Covered** by `Shell/CrashGuard.cs`: a second `Umbra.exe --guard <pid>` waits on the bar and restores the state, and the window visibility, the moment it dies. A kill of every `Umbra.exe` at once still falls back to the next launch.

### 2.2 How the tray is claimed

Worth understanding properly, because the mechanism is not what one would guess.

`ManagedShell.WindowsTray.TrayService`:

- **Registers its own window class literally named `Shell_TrayWnd`** inside our process and creates a hidden `WS_POPUP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW` window of that class, plus a child `TrayNotifyWnd`. Class registration is per-process, so this coexists with Explorer's globally-known window of the same class name.
- **Interception is purely by Z-order.** `FindWindow("Shell_TrayWnd", null)` returns the topmost match. So `MakeTrayTopmost()` raises ours to `HWND_TOPMOST` and `SetWindowsTrayBottommost()` pushes Explorer's to `HWND_BOTTOM`. Applications sending tray data find ours first.
- **A second 100 ms `DispatcherTimer`** re-asserts topmost whenever `FindWindow` stops returning our handle.
- **`SendTaskbarCreated()`** broadcasts the registered `TaskbarCreated` message, prompting every running app to re-add its tray icon. This is how the tray populates on startup.
- **`WndProc` handles `WM_COPYDATA`** with `dwData` 0 (AppBar messages), 1 (`SHELLTRAYDATA` — the icons), 3 (`WINNOTIFYICONIDENTIFIER` — icon position queries for flyout anchoring).
- **Everything unhandled is forwarded to Explorer's real tray** via `ForwardMsg`, which resolves `HwndFwd = WindowHelper.FindWindowsTray(HwndTray)` — that is, the *other* `Shell_TrayWnd`. So this is a **proxy, not a replacement**: Explorer's tray keeps functioning underneath and its own notification icons keep working.
- **Our `Shell_TrayWnd` is never visible.** `WM_WINDOWPOSCHANGED` strips `WS_VISIBLE` if anything tries to show it. It is a pure message sink; the visible tray UI is our own WPF surface, wired up through `NotificationArea`.
- **`SetTrayHostSizeData` / `FillTrayHostSizeData`** report our notification area's rect so that `ABM_GETTASKBARPOS` and tray-icon flyouts anchor to the right place. **This is the hook our four-edge placement needs** — it is how a flyout learns to open toward screen centre from a left-edge bar. Confirm it is called on every layout change, including drift.

### 2.3 What this costs

- **Two permanent 100 ms timers** (tray Z-order + taskbar visibility), 20 wake-ups/second, running whether or not anything is happening. This directly undercuts §6.6's rule about letting the display idle so the panel can run its compensation cycles. Our render loop pausing is not sufficient if ManagedShell keeps the process busy. **Measure whether these alone prevent display idle** before assuming §6.6 works.
- **Dependence on undocumented behaviour.** `FindWindow` returning the topmost Z-ordered match is not contractual. Neither is class-name squatting.
- **Two shell replacements cannot coexist.** Both will fight for topmost at 10 Hz.

### 2.4 Collateral — the open part of §11 Q2

| Concern | Status |
| :---- | :---- |
| `Shell_SecondaryTrayWnd` on multi-monitor | Handled, enumerated in a loop |
| Start button window (`0xC017`) | Handled, hidden separately |
| Explorer's tray icons still working | Yes — messages are proxied through, not swallowed |
| **Autohide edge collision** | **Real constraint. See below.** |
| `Win+B`, `Win+T`, Show Desktop hotkey | **Still untested. Left in Q2.** |

**The autohide collision is the one to design around.** Windows permits only **one autohide AppBar per screen edge**. ManagedShell puts Explorer's taskbar into autohide on the bottom edge as its space-releasing mechanism. Therefore:

> **Our bar must register as a normal space-reserving AppBar and must never use `ABM_SETAUTOHIDEBAR`.** RetroBar's recurring "a toolbar is already hidden on that side of the screen" error is exactly this collision.

This is compatible with our design as written — Tier 0 is a fully-reserved bar painted black, not an autohiding bar — but it permanently forecloses ever adding a real auto-hide option on the same edge as Explorer's taskbar.

### 2.5 The finding that should change the build order

**The bottom edge is the worst placement, not the reference one.**

With Explorer's taskbar autohidden at the bottom, moving the pointer to the bottom screen edge triggers its reveal. ManagedShell's watchdog re-hides it within 100 ms. RetroBar users report the visible symptoms: a persistent 1 px line of Explorer taskbar (issue #994) and flicker.

Our bar sits exactly in that region, and its Tier 2 trigger is *pointer proximity to that same edge*. The two systems will contend precisely where the user interacts most, and the contention is a 10 Hz race with visible artefacts.

Top, left and right edges do not share an edge with the autohidden Explorer taskbar and avoid the problem entirely.

**Recommendation:** validate on the **left edge** first, not the bottom. §7.1 can keep bottom as the *design* reference — it is still where the Windows 11 fidelity requirements come from — but Appendix step 1 should bring up the left edge first, because it isolates our bar's behaviour from Explorer's autohide reveal.

---

## Q1 — Start menu anchoring

### 3.1 The mechanism, in full

`ManagedShell.Common.Helpers.ShellHelper.ShowStartMenu()` is:

```csharp
public static void ShowStartMenu()
{
    ShellKeyCombo(VK.LWIN, VK.LWIN);
}
```

A synthesized `Win` keypress via `SendInput`. That is the entire mechanism, and there is nothing else on offer. No position argument exists because no API accepts one.

Consequences beyond positioning, worth noting since they are not obvious from the call site:

- It is global input injection, so it is subject to UIPI — it will fail silently when a higher-integrity window has focus.
- Other software hooking the `Win` key will intercept it.
- `ShowStartContextMenu()` is `Win+X` and has the same properties, as do `ShowActionCenter()` (`Win+A`) and `ShowNotificationCenter()` (`Win+N`). Everything is keystroke synthesis.

### 3.2 Why it cannot be repositioned

The Start menu is `StartMenuExperienceHost.exe`, a separate process that positions itself from Explorer's taskbar geometry and `TaskbarAl`. We control neither.

Windhawk's Vertical Taskbar mod is the existence proof of what moving it actually requires:

| Target | Hook | Purpose |
| :---- | :---- | :---- |
| `taskbar.dll` | `TrayUI_GetDockedRect`, `TrayUI_MakeStuckRect` | Reserve space on the side |
| `Taskbar.View.dll` | `CTaskListButton::UpdateTaskListButton`, `TaskbarFrame::MeasureOverride` | Rotate XAML elements |
| `user32.dll` | `SetWindowPos`, `GetWindowRect` | Reposition jump lists |
| **`dwmapi.dll`** | **`DwmSetWindowAttribute` on `DWMWA_EXTENDED_FRAME_BOUNDS`** | **Position the Start menu** |

…with injection into `explorer.exe`, `StartMenuExperienceHost.exe`, `ShellExperienceHost.exe` and `ShellHost.exe`. That is DLL injection into protected shell processes, requiring a Windhawk-class engine underneath. Categorically out of scope for a standalone WPF AppBar consuming a NuGet package, and it would drag in the entire maintenance burden of tracking undocumented symbols across Windows builds.

Confirming the symptom from the other direction: Windhawk's *Taskbar on top* mod documents that with the bar at the top, **the Start menu still slides up from the bottom**. Known, unfixed, and that mod is injecting into Explorer already.

### 3.3 What this means for our design

Because we *hide* Explorer's taskbar rather than move it, it remains logically on the bottom edge. So:

| Our bar's edge | Where Start opens | Verdict |
| :---- | :---- | :---- |
| **Bottom** | Bottom, aligned per `TaskbarAl` | Correct. Read `TaskbarAl` and match our Start button's alignment to it |
| **Top** | Bottom of the screen, sliding upward | Wrong — maximum possible distance from the button that invoked it |
| **Left / right** | Bottom-left or bottom-centre | Wrong |

`TaskbarAl` (0 = left, 1 = centre, under `HKCU\...\Explorer\Advanced`) should be read regardless, so that on the bottom edge our Start button sits where the menu will actually appear.

### 3.4 The decision this forces

Spec §10 lists "replacing or reimplementing the Windows 11 Start menu" as a non-goal, and §11 Q1 asked whether a custom launcher moves from optional to necessary.

**Answer: necessary, for every edge except the bottom.**

This is not a corner case — four-edge placement is one of the project's two reasons to exist (§0), and on three of those four edges the Start button opens a menu at the opposite end of the screen. Either §10's non-goal changes, or §0's product definition does.

Three ways forward:

**A. Accept it.** Ship with the Start menu appearing at the bottom on non-bottom edges; document it as a Windows limitation. Zero work. Genuinely jarring on a left or right bar, and it is the first thing anyone will notice.

**B. Full custom launcher.** Own app list, pinned tiles, search. Large — and search is the expensive part, requiring the Windows Search API or a custom index to not feel broken.

**C. Hybrid.** Custom lightweight launcher — pinned apps, all-apps list from the two Start Menu directories (`ShellHelper.UsersStartMenuPath` / `AllUsersStartMenuPath` are already exposed), power actions — with a search field that hands off to the system search UI rather than reimplementing it. Invoke the real Start menu when on the bottom edge, where it lands correctly. Moderate work, avoids the hard part, keeps fidelity where fidelity is achievable.

C looks like the right shape, but this is a product call rather than a technical one.

---

## Empirical tests still to run

Neither question is fully closed without a machine. In rough priority order:

1. **Left-edge bring-up.** Register a space-reserving AppBar on the left with Explorer's taskbar hidden. Confirm maximised windows reflow correctly and no autohide contention occurs.
2. **Bottom-edge contention.** Same on the bottom. Watch for the 1 px Explorer line and for flicker when the pointer enters the bar. Quantify how bad it actually is — §2.5's recommendation assumes it is bad but the severity is unmeasured.
3. **Idle behaviour.** With the bar running and motion paused per §6.6, does the display still enter idle and does the panel run its compensation cycle? ManagedShell's two 100 ms timers are the suspect.
4. **Crash recovery.** Kill the process. Confirm the AppBar reservation is released and Explorer's taskbar returns to its pre-launch state rather than staying autohidden.
5. **Hotkey collateral.** `Win+B`, `Win+T`, `Win+`number, Show Desktop, and `Win+X` with `Shell_TrayWnd` hidden and Z-order inverted.
6. **`ShowStartMenu` under UIPI.** Confirm the synthesized `Win` keypress fails gracefully rather than silently doing nothing when an elevated window has focus.
7. **Tray flyout anchoring.** Confirm `SetTrayHostSizeData` produces correct flyout placement from a left-edge bar, and that it is re-called as drift moves the content.

---

## Suggested spec amendments

- **§11 Q1** → resolved. Replace with the launcher decision (A/B/C above), and move it into the body once decided.
- **§11 Q2** → mostly resolved. Reduce to the untested collateral: hotkeys, and whether ManagedShell's timers defeat display idle.
- **§10 non-goals** → likely needs the Start menu line amended, pending the launcher decision.
- **§6.6** → add the caveat that ManagedShell's polling timers run regardless of our render loop, and that idle behaviour must be measured rather than assumed.
- **§7.1** → note that bottom is the design reference but not the bring-up target.
- **Appendix build order** → step 1 brings up the **left** edge first.
- **New constraint, belongs near §6.3:** our AppBar must never use `ABM_SETAUTOHIDEBAR`, because Explorer's taskbar holds the autohide slot on its edge.
- **§11 Q4** → crash recovery must also restore Explorer's taskbar autohide state, not just release our own AppBar reservation.

---

## Sources

- [ManagedShell — ExplorerHelper.cs](https://github.com/cairoshell/ManagedShell/blob/master/src/ManagedShell.AppBar/ExplorerHelper.cs)
- [ManagedShell — TrayService.cs](https://github.com/cairoshell/ManagedShell/blob/master/src/ManagedShell.WindowsTray/TrayService.cs)
- [ManagedShell — NotificationArea.cs](https://github.com/cairoshell/ManagedShell/blob/master/src/ManagedShell.WindowsTray/NotificationArea.cs)
- [ManagedShell — ShellHelper.cs](https://github.com/cairoshell/ManagedShell/blob/master/src/ManagedShell.Common/Helpers/ShellHelper.cs)
- [RetroBar](https://github.com/dremin/RetroBar) — reference implementation on ManagedShell
- [RetroBar issue #994 — autohide line artefact](https://github.com/dremin/RetroBar/issues/994)
- [RetroBar issue #1190 — "a toolbar is already hidden"](https://github.com/dremin/RetroBar/issues/1190)
- [Windhawk — Vertical Taskbar for Windows 11](https://windhawk.net/mods/taskbar-vertical)
- [Windhawk — Vertical Taskbar implementation notes](https://deepwiki.com/ramensoftware/windhawk-mods/4.5-vertical-taskbar)
- [Windhawk — Taskbar on top for Windows 11](https://ramensoftware.com/windhawk-mod-taskbar-on-top-for-windows-11)
