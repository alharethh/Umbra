# Verified numbers — test baselines

Every figure below was computed independently during design and cross-checked against the spec. Assert these in `Umbra.Core.Tests`. **If an implementation disagrees with one of these, the implementation is wrong.**

## Decided

| | |
| :---- | :---- |
| Project name | **Umbra** — namespace `Umbra`, executable `Umbra.exe` |
| Target | **.NET 10** |
| First drop | **`Umbra.Core` class library + xUnit tests. No WPF yet.** |
| Default config | Bottom edge, left-aligned icons, `TaskbarAl = 0` |
| Start menu | Cannot be repositioned. Accepted, ship all four edges, no launcher |

## Build environment constraint

The assistant's sandbox has **no .NET SDK and cannot install one** (network restricted). C# cannot be compiled or tested there. Hence the core-library-first approach: all spec maths lives in a plain library with no WPF dependency, so its algorithms can be validated against the figures already verified in Python/JS, and you can run the real tests on Windows.

Expect compile errors on first build. That is anticipated, not a failure.

## Planned first drop

```
Umbra.sln
Directory.Build.props
src/Umbra.Core/
  Color/      Rgb, Srgb (transfer functions, relative luminance, scale, cap)
  Wear/       WearModel (L^n), WearAccumulator (float64), DutyBudget
  Layout/     SlotMetrics, BarMetrics, BarEdge, Alignment
  Motion/     MotionModel (constant-velocity triangle + 2 Hz micro-jitter)
  Tiers/      Tier, TierPalette, TierController (ramp, attack/release, dwell guard)
  Icons/      IconNormalizer (P90 + integrated-luminance diagnostic)
tests/Umbra.Core.Tests/
```

## Numbers the tests must assert

All verified in this session; treat as regression baselines.

| Quantity | Value |
| :---- | :---- |
| `L(#333333)` | 0.0331048 |
| `L(#404040)` | 0.0512695 |
| `L(#4D4D4D)` | 0.0742136 |
| `L(#B3B3B3)` | 0.450786 |
| `wear(#333333)`, n=1.7 | 0.00304659 |
| Tier 2 cap wear (L=0.4508) | 0.258077 |
| v3 pinned icon, duty-weighted | 0.003574 |
| v3 running icon, duty-weighted | 0.007115 |
| v2/v1 pinned (regression) | 0.005392 |
| Ratio vs light desktop (L=0.80) | 191× |
| Ratio vs dark surface (`#1E1E1E`) | bar is 5.76× **higher** |
| `A_max` @100% (pitch 44, icon 24) | 7.80 px |
| Drift amplitude (0.70·A_max) | 5.46 px |
| Micro amplitude (0.25·A_max) | 1.95 px |
| Peak offset / clearance | 7.41 px / 2.59 px |
| Drift velocity (±5.46, 20 min) | 0.0182 px/s |
| Micro step @2 Hz (A=1.95, T=60 s) | 0.102 px |
| float32 accumulator error @5M adds | ~4.2% — must use float64 |

## Open, in priority order

1. Does the motion layer earn its keep? Needs the wear accumulator running on real hardware.
2. Do ManagedShell's two 100 ms timers defeat display idle? If so, §6.6's argument collapses.
3. Bottom-edge autohide contention severity — gates the shipping default.

Full list in spec §11.2.
