<div align="center">

<img src="src/Umbra.Shell/Assets/logo.png" width="96" alt="Umbra">

# Umbra

**A Windows 11 taskbar that lives on any edge — and is built not to burn into your OLED panel.**

</div>

---

Windows 11 pins its taskbar to the bottom of the screen and draws it as a bright, permanently
static strip. On an LCD that costs nothing. On an OLED it is the single most burn-in-prone thing on
the display: the same pixels, the same colours, for every hour the machine is on.

Umbra is a replacement built the other way round. It sits on **any** edge, and every design decision
inside it starts from the same question — *how much light does this put on a fixed pixel, and for
how long?*

![The bar waking as the pointer approaches](docs/media/wake.gif)

<sub>Resting, then waking as the pointer arrives: the icons take their colour back, grow to full
size, and the clock gains its seconds and date.</sub>

## Why it exists

**1. It goes where you want it.** Left, right, top or bottom, switchable while it is running.
Windows 11 removed that; this puts it back, as a real space-reserving AppBar that maximised windows
respect.

**2. It is designed against OLED wear, not decorated with a dark theme.** Three mechanisms, all
running at once:

| | |
| :---- | :---- |
| **Three tiers of brightness** | At rest the bar is near-black with monochrome icons. It brightens as your pointer approaches and reaches full colour only when you are on it. Most of the day it emits almost nothing. |
| **Nothing sits still** | Everything drawn drifts slowly inside its own slot, with sub-pixel jitter on top, so no edge lives on one physical pixel. Constant velocity, no easing — an eased turnaround would burn two hot bands where it lingers. |
| **Icons shrink while you are away** | Idle icons draw at 16 px and grow to full size as you approach, so they are at the Windows size for the whole of the time anyone is looking at them. |

**Being honest about the payoff:** measured against real content, Umbra's bar emits **86–191×**
less than light-mode desktop content — and about **5.8× more** than a plain dark-mode application
surface. If you run Windows in light mode, the taskbar was never your panel's problem and this
buys you little. If you run dark mode with a bright taskbar, it is the brightest fixed thing you
own, and this is aimed squarely at it.

## What it does

- **Any edge, live.** Move the bar between edges from the settings window; the desktop reflows.
- **The row is one order.** Pinned and running icons share it, and any icon can be dragged anywhere.
  Pin to make a position stick.
- **Live window previews.** Hover an icon for a thumbnail of every window it has open, with
  per-window close.

  ![Live thumbnails as the pointer moves along the row](docs/media/preview.gif)
- **Real jump lists.** Right-click an icon for the application's own published tasks — "New private
  window", "New Code Session" — read from the same files Explorer reads.
- **Notification area** with an overflow flyout, volume and network as one cluster, and an input
  language indicator. Volume and network open Quick Settings together, as they do on the real
  taskbar; the clock opens the calendar and notifications.

  ![The overflow flyout, then Quick Settings](docs/media/panels.gif)

  ![The clock opening notifications and the calendar](docs/media/clock.gif)
- **Show desktop** in the trailing corner, where it has always been. Click once to clear the
  screen, again to put everything back.
- **A wear heatmap.** Umbra records how much light every pixel of its own rectangle has emitted,
  weighted the way burn-in actually accumulates, and will draw you the map. No other taskbar can
  tell you what it has been doing to your screen.
- **Hover feedback you can colour**, on a holographic pane or a plain chip, your choice — capped so
  the colour is a choice about hue and never about how much light the panel emits. Start takes the
  same treatment: a band of light crosses the button and paints the logo blue behind it.

  ![The Start logo lighting under the pointer](docs/media/start.gif)

## Install

Windows 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/alharethh/Umbra.git
cd Umbra
dotnet publish src\Umbra.Shell -c Release -o artifacts\Umbra
artifacts\Umbra\Umbra.exe
```

Umbra hides Explorer's taskbar while it runs and puts it back when it exits — including after a
crash, which it recovers from on the next launch. Right-click the bar for **Settings**, the wear
heatmap, and **Exit**.

Configuration lives in the settings window and in
`%LOCALAPPDATA%\Umbra\settings.txt`. Everything applies immediately except hosting the notification
area, which is built at startup.

## Status

Runs on hardware daily on Windows 11 25H2. Not yet released as a binary — build it yourself for now.

**Works:** every edge on one monitor · space-reserving AppBar · Explorer suppressed and restored ·
pinning, launching and free drag-reorder · window previews · jump lists · notification area with
overflow · clock, language and network indicators · show desktop · wear accumulator and heatmap ·
the full tier and motion system · live settings.

**Not yet:**

- **One monitor at a time.** You choose which; you cannot have a bar on each. The open question is
  not mechanical — a non-OLED second screen may not want any of this treatment.
- **No keyboard navigation and no screen-reader support.** A custom bar is invisible to a screen
  reader without explicit automation peers, and none are written yet.
- **The notification area costs a permanent 100 ms timer.** Whether that stops the display idling —
  and therefore whether it costs more panel life than it saves — is measured but not yet answered.

## How it is built

C# and WPF on .NET 10. [ManagedShell](https://github.com/cairoshell/ManagedShell) (Apache 2.0)
provides tray hosting, the tasks service and AppBar registration; everything above that is original.

The wear model, colour handling, layout geometry, motion and tier systems live in `Umbra.Core`,
which has no WPF dependency and is covered by 349 tests. The numbers in the design spec are
asserted as regression baselines, and the suite is written to fail on the silent traps specifically
— accumulating light linearly instead of by the burn-in exponent, easing a drift turnaround,
interpolating a tier ramp in the wrong colour space.

| Document | What it is |
| :---- | :---- |
| [`docs/design-spec.md`](docs/design-spec.md) | The specification everything is built against |
| [`docs/measurements.md`](docs/measurements.md) | What the bar actually did on real hardware, per step |
| [`docs/verified-numbers.md`](docs/verified-numbers.md) | Every published figure, independently computed |
| [`docs/shell-integration.md`](docs/shell-integration.md) | How Explorer's taskbar is suppressed, read out of ManagedShell's source |
| [`docs/prototype.html`](docs/prototype.html) | Browser prototype of the tier and motion systems |

## Licence

Not chosen yet. ManagedShell is Apache 2.0, which is compatible with anything you are likely to
pick; a licence needs to be here before this is published.
