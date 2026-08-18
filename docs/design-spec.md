# OLED-Optimised Taskbar — Design Specification

**Version:** 4 · supersedes v3, v2 and v1
**Target:** Windows 11 · WOLED (WRGB) desktop panels · four-edge placement
**Default configuration:** bottom edge, left-aligned icons
**Status:** Pre-implementation design. Numbers here are targets to build against and tune, not measured results. An interactive prototype of §3 and §6 exists (`oled-taskbar-prototype.html`) and should be kept in sync with this document.

> **What changed in v4.** §5.3 outline mode is **removed from the design**, not merely deprioritised. v2 promoted it to the primary defence for icon interiors; measuring it against a real icon set (`docs/measurements.md`, step 6) found it removes 34.6% of lit area across the row rather than the 70–85% §5.3 assumed, with two thirds of that coming from two icons and two icons gaining nothing at all. Against a recognisability cost on every icon and a per-icon settings surface to manage it, the project owner judged it not worth its complexity. **The consequence is stated plainly in §6.0: nothing now defends icon interiors except the Tier 0 luminance floor, and that is accepted.** §11.2 Q10 is withdrawn with it. Full list in §12.

> **What changed in v3.** Shell integration research closed v2's §11 Q1 and Q2. The Start menu **cannot** be repositioned without DLL injection, and the decision is to accept it and ship all four edges — so §10's non-goal stands and no launcher is built. The default configuration is now **bottom edge with left-aligned icons**, which as it happens makes the Start menu land correctly by default (§7.1). Start moves to the *bottom* of vertical bars so the button sits near where the menu will appear. Left-alignment introduced a new geometry constraint — the row must be inset so drift cannot push content off-screen at the corner (§6.3). Tray hosting is solved and its mechanism is documented (§11.1), along with two constraints it imposes: never register an autohide AppBar, and ManagedShell's polling defeats the assumption behind §6.6. Full list in §12.

> **What changed in v2.** Slot containment became a governing principle (P5), capping drift amplitude at the icon gutter and eliminating the drift-versus-click conflict in v1 §6. Consequently macro drift no longer protects icon interiors, and §5.3 outline mode was promoted to the primary interior defence. The wear budget became duty-weighted, revealing that v1's Tier 1 was a co-equal rest state rather than a cheap one. Tier 2 was luminance-capped. Errors in v1 §2 (the "order of magnitude" claim), §5.1 (what P90 equalises), §6.2 (eased turnarounds), §6.4 (clock phase) and §9 (accumulation exponent, float32 precision) were corrected.

---

## 0. Product definition

A taskbar replacement that looks and behaves like the Windows 11 taskbar, but:

1. can be placed on **any screen edge** (Windows 11 cannot), and
2. is designed from the ground up to **minimise OLED differential wear**.

Those two reasons to exist are independent. Either alone justifies the project to a user; together they define it.

**Stack:** C# / WPF, with [ManagedShell](https://github.com/cairoshell/ManagedShell) (Apache 2.0) consumed as a NuGet dependency for tray hosting, the tasks service, and AppBar registration. Everything above that layer is original.

---

## 1. Governing principles

These resolve disputes when specific rules conflict.

**P1 — Black is free, light is not.** A black pixel draws no current. Every non-black pixel is a cost that must justify itself.

**P2 — Wear is superlinear in luminance.** Degradation follows roughly `lifetime ∝ 1/J^n` with *n* ≈ 1.5–2.0, so accumulated wear per unit time goes as `L^n`. Assume **n = 1.7** for planning. Halving luminance more than triples life.

**P3 — Duty cycle dominates, but "rest" means every state the user is not actively in.** Tier 0 and Tier 1 together run ~100% duty and together constitute the entire wear budget. v1 treated Tier 1 as transient; §2.2 shows it was not. Optimise Tier 0 *and* Tier 1 ruthlessly; spend on Tier 2 only.

**P4 — Neutral is optimal, not a compromise.** On WRGB, a neutral grey is carried predominantly by the unfiltered white emitter — the most efficient path to a given luminance. Saturated colour requires filtering white light and discarding most of it, driving the emitter harder. Desaturation is using the panel correctly, not sacrificing looks for longevity. (This argument is WOLED-specific; see §11.2 Q6.)

**P5 — Slot containment. Nothing ever leaves its slot.** Hit geometry is fixed and computed from the undrifted layout. Drawn geometry may move *only within the bounds of its own hit slot*, never into a neighbour's.

> A taskbar exists so that applications are where the user expects them to be. An icon rendered outside its usual place defeats the product, regardless of what it buys in wear. This constraint is not negotiable against wear arguments — it is the reason the product exists.

This is a strengthening of v1's P5, which fixed hit geometry but let drawn geometry "drift freely." At v1's ±64 px that put icons on top of their neighbours' click targets. P5 as now stated makes the visual and the clickable regions permanently agree, at the cost documented in §6.0.

**P6 — Motion the user can perceive has failed.** Drift exists to redistribute wear, not to be seen. Any motion noticeable in normal use is a defect, even if its wear benefit is real. In practice P5 binds long before perceptibility does (§6.3).

**P7 — Fidelity is the default, deviation needs a reason.** Match Windows 11 unless a principle above forces otherwise. Every deviation is listed in §8.

---

## 2. Luminance model

### 2.1 The table

All values sRGB. "L" is relative luminance as a fraction of panel peak. Nits column assumes a 200-nit SDR desktop — scale to your actual setting. Wear is `L^1.7` at 100% duty, normalised to white.

| Hex | L | @200 nits | Relative wear | vs. always-on white |
| :---- | :---- | :---- | :---- | :---- |
| `#000000` | 0.00% | 0.0 | 0 | ∞ |
| `#0A0A0A` | 0.30% | 0.6 | 0.000052 | 19066× less |
| `#1A1A1A` | 1.03% | 2.1 | 0.000421 | 2377× less |
| `#333333` | 3.31% | 6.6 | 0.003047 | 328× less |
| `#404040` | 5.13% | 10.3 | 0.006409 | 156× less |
| `#4D4D4D` | 7.42% | 14.8 | 0.012018 | 83× less |
| `#666666` | 13.29% | 26.6 | 0.032346 | 31× less |
| `#808080` | 21.59% | 43.2 | 0.073806 | 13.5× less |
| `#B3B3B3` | 45.08% | 90.2 | 0.258077 | 3.9× less |
| `#FFFFFF` | 100.00% | 200.0 | 1.000000 | 1× |

Note the gamma: `#808080` is not half-bright, it is ~22% of peak. Reason in luminance, not in hex.

### 2.2 The duty-weighted budget

**This replaces v1's single-state comparison, which was the source of two errors.**

v1 compared rest states as if each ran at 100% duty, then compared those against a 0.1%-duty white hover. That framing produced the claim that *"a full-white hover state costs an order of magnitude less than even a very dark rest state."* It does not: `#333333` at 100% duty is 328× below always-on white, and white at 0.1% duty is 1000× below. **That is 3×, not an order of magnitude.** The claim was flagged in v1 as "the single most important number in the document," so it is corrected here rather than quietly dropped.

The right calculation weights each tier by its duty and sums. For v1's tier values and duty cycles (Tier 0 85%, Tier 1 15%, Tier 2 0.1% at full white):

| Element | Total wear/s | Tier 0 share | Tier 1 share | Tier 2 share |
| :---- | :---- | :---- | :---- | :---- |
| Pinned icon (v1 values) | 0.005392 | 48.0% | **33.4%** | 18.5% |
| Running app icon (v1 values) | 0.011299 | 48.2% | **42.9%** | 8.9% |

Tier 1 was a third to a half of the entire budget. v1's instruction to "optimise rest ruthlessly; spend freely on hover" was therefore aimed at the wrong state — Tier 1 *is* rest, it just has a pointer nearby.

**Second problem: the v1 design was fragile against an unmeasured parameter.** Tier 2 duty of 0.1% is 1.4 min/day. But Tier 2 triggers on *proximity within 40 px*, not on click, so 1% (14 min/day) is entirely plausible. At 1% duty with full-white Tier 2:

| Element | Total wear/s | Tier 2 share |
| :---- | :---- | :---- |
| Pinned icon (v1 values, T2 duty 1%) | 0.014392 | **69.5%** |
| Running app icon (v1 values, T2 duty 1%) | 0.020299 | **49.3%** |

The entire thesis inverts on a parameter nobody has measured. That is not an acceptable place for a design to sit.

### 2.3 The v2 budget

Three changes, specified in §3: Tier 1 darkened one step, the proximity band narrowed (Tier 0/1 duty → 92%/8%), and Tier 2 luminance-capped at L ≤ 0.45 while retaining full chroma.

| Element | Total wear/s | Tier 0 | Tier 1 | Tier 2 | vs. v1 |
| :---- | :---- | :---- | :---- | :---- | :---- |
| Pinned icon | 0.003574 | 78.4% | 14.3% | 7.2% | 1.51× better |
| Running app icon | 0.007115 | 82.9% | 13.5% | 3.6% | 1.59× better |

And the robustness that was missing — the same design if Tier 2 duty turns out to be 10× higher than assumed:

| Element | Total wear/s | Tier 2 share |
| :---- | :---- | :---- |
| Pinned icon (T2 duty 1%) | 0.005896 | 43.8% |
| Running app icon (T2 duty 1%) | 0.009438 | 27.3% |

Still not dominant. Against v1 under the *same* pessimistic assumption (0.014392) this is 2.4× better; it lands roughly where v1 sat in its optimistic case (0.005392), which is the point — v2's worst case is v1's best case, so the design no longer swings on a number nobody has measured. The capped Tier 2 also rescues v1's original claim: a capped-luminance hover at 0.1% duty is 3876× below always-on white against `#333333`'s 328×, which genuinely is **an order of magnitude cheaper than the rest state**. The sentence v1 wanted to write becomes true once Tier 2 is capped.

### 2.4 Absolute context — how much does any of this matter?

The above is all internal to the bar. The question that determines whether the project's wear thesis is worth anything is how the bar compares to *the rest of the screen*. Using the v2 pinned-icon total of 0.003574:

| Screen content | Wear/s | Ratio to pinned icon |
| :---- | :---- | :---- |
| Light-mode window body (L = 0.80) | 0.684310 | **191× the bar** |
| Mid-tone content (L = 0.50) | 0.307786 | **86× the bar** |
| Dark-mode text and UI (L = 0.05) | 0.006141 | 1.7× the bar |
| Dark-mode app surface (`#1E1E1E`) | 0.000620 | **0.17× — the bar is 5.8× worse** |

**Read the last row carefully, because it decides the motion layer's fate.**

For a light-mode user, the bar is ~200× below the wear-limiting content on their screen and this entire document is an elaborate optimisation of a rounding error. Fine — the four-edge placement still justifies the project.

For a dark-mode user, the bar's monochrome icons are **roughly six times brighter than the application backgrounds surrounding them, and unlike application content they never move, never scroll, and are present in every session.** On a dark-mode desktop the taskbar icon row is plausibly the single most static-and-lit region on the panel. That is the user for whom this project's wear thesis is real, and it is the user the motion layer exists for.

State this in the README. It is a more honest and more useful claim than "reduces OLED wear," and it tells a prospective user whether they are in the audience.

---

## 3. State tiers

Four tiers. Every element declares its appearance in each.

| Tier | Trigger | Duty cycle | Purpose |
| :---- | :---- | :---- | :---- |
| **0 — Dormant** | Pointer > 120px away | ~92% | Bar nearly invisible |
| **1 — Ambient** | Pointer within 120px | ~8% | Legible, scannable |
| **2 — Engaged** | Pointer within 40px / over bar / keyboard focus | ~0.1% | Full Win11 fidelity, full chroma |
| **3 — Alert** | Unread notification / attention flash | rare | Must be visible from Tier 0 |

The proximity band is **120 px at 100% scaling and scales with per-monitor DPI** — v1 specified 200 px in raw pixels, which both inflated Tier 1 duty and meant the band covered a different fraction of the screen on every monitor.

Tier 2 now also triggers on keyboard focus (`Win+T`, arrow navigation), since no pointer proximity exists in that path. See §11.2 Q7.

### 3.1 Tier values

| Element | Tier 0 | Tier 1 | Tier 2 | Tier 3 |
| :---- | :---- | :---- | :---- | :---- |
| Background | `#000000` | `#000000` | `#000000` | `#000000` |
| Pinned icon (idle) | `#333333` | `#404040` | full chroma, L ≤ 0.45 | — |
| Running app icon | `#404040` | `#4D4D4D` | full chroma, L ≤ 0.45 | — |
| Running indicator (pill) | hidden | `#404040` | `#666666` | — |
| Clock | `#333333` | `#404040` | `#666666` | — |
| Start glyph | `#333333` | `#404040` | `#666666` | — |
| Tray icons | `#333333` | `#404040` | full chroma, L ≤ 0.45 | — |
| Notification badge | `#B3B3B3`, pulsed | `#B3B3B3`, pulsed | accent, steady | accent, steady |

Tier 1 is one step darker than v1 throughout, per §2.2. Tier 2 greys drop from `#808080` to `#666666` for the same reason — against a pure black background, 26.6 nits is emphatically legible.

### 3.2 The Tier 2 luminance cap

**Tier 2 is full chroma, luminance-capped to L ≤ 0.45** (≈90 nits at a 200-nit reference). Icons are tone-mapped so their peak luminance lands at the cap; hue and saturation are untouched.

This is the highest-value change in v2 and it costs almost nothing. Against a pure black bar, a 90-nit icon reads as vividly, unambiguously "full colour" — the perceptual cue for colour is chroma, not luminance, and there is no adjacent bright content to compare against. The cap buys a 3.9× reduction in Tier 2 cost and, more importantly, removes the design's dependence on an unmeasured duty figure (§2.3).

"Full colour" was always the design intent. "Full luminance" never was; v1 conflated them.

### 3.3 Tier 3 — the notification badge

The badge is, after the Tier 2 cap, **the brightest static element in the entire design** — `#B3B3B3` at L = 45%, in a tiny fixed position, potentially persisting for hours on an unacknowledged count. It is textbook differential wear and v1 granted it an exemption with no mitigation at all. Three rules:

- **Pulse, do not hold.** 1 s on / 3 s off. This quarters duty *and* reads as more attention-grabbing, not less. Fade the transitions (200 ms) so it does not read as a fault indicator.
- **Independent drift phase.** The badge drifts on its own frequency within the icon's slot, decorrelated from the icon row (§6.4).
- **Decay when ignored.** After 15 minutes unacknowledged, step down to `#808080` and lengthen the pulse to 1 s on / 6 s off. A badge the user has demonstrably chosen not to act on does not need 90 nits.

Tier 3 remains the only element permitted saturated colour at rest, and only when an actual notification exists. It is small, low-duty in aggregate, and the entire point of the design would be lost if alerts were invisible.

### 3.4 Transitions

- **Attack** (dimmer → brighter): 120 ms, ease-out.
- **Release** (brighter → dimmer): 400 ms, ease-in. Slower so the bar does not flicker during transient pointer passes.
- **Proximity ramp:** Tier 0 → Tier 1 interpolates continuously over the 120 px → 40 px band rather than snapping. Interpolate in **linear light**, not in sRGB values, or the ramp will look wrong and spend more time bright than intended.
- **Dwell guard:** do not promote to Tier 2 until the pointer has been inside 40 px for 60 ms. Prevents full illumination from mouse travel that merely crosses the bar.

---

## 4. Background and chrome

- Background: `#000000` exactly. Not `#010101`. Not near-black.
- **No** acrylic, mica, blur, transparency, gradient, or vignette. All are per-pixel luminance at 100% duty with no informational content — the worst possible trade under P1 and P3.
- **No** top border, hairline, or separator. If a visual edge proves necessary at Tier 1+, cap it at `#1A1A1A` and drift it with everything else. Prefer solving it with spacing.
- Rounded corners: Windows 11's floating-bar corner radius is compatible with black, since the rounding is achieved by *not lighting* pixels. Free. Keep it.

**Known accepted limitation.** A permanently-unlit bar beneath permanently-lit desktop content creates a wear discontinuity at the bar's edge, which over years may become visible as a line on full-screen uniform content. This is inherent to any reserved-space bar and is *not* improved by lightening the bar — that only adds wear on the dark side of the step without removing the step. Reserving no space at all would eliminate it, but is incompatible with the Windows 11 fidelity requirement. Accepted, documented, not mitigated.

Note that §2.4 sharpens this: for a light-mode user the step is large and this limitation is the project's most visible long-term artefact. For a dark-mode user the step is small, because the content above the bar is not much brighter than the bar itself.

---

## 5. Icon treatment

### 5.1 Monochrome normalisation

Naive desaturation fails badly across a real pin set: a dark logo vanishes, a white logo blasts. Normalise instead.

1. Convert to linear light.
2. Compute perceptual luminance per pixel (Rec. 709: `0.2126R + 0.7152G + 0.0722B`).
3. Compute the icon's **90th-percentile luminance** over non-transparent pixels. Use P90 rather than max (immune to single stray bright pixels) and rather than mean (immune to how much padding the icon has).
4. Scale the whole icon so P90 lands on the tier's target luminance.
5. Clamp, re-encode to sRGB. Preserve the alpha channel untouched — the silhouette is what carries recognition at this size.

**Correction to v1.** v1 claimed this "makes the wear budget predictable per slot." It does not. P90 normalisation equalises *peak* brightness, not emitted energy. A solid-fill logo and a thin-stroke logo land at identical P90 but differ by 5–10× in total lit area, and therefore in total wear. The claim is withdrawn.

What P90 delivers is uniform perceived brightness across the row, which is worth having on its own merits. If per-slot wear parity is wanted, it needs a second pass:

6. *(Optional, off by default)* Compute integrated linear luminance over the slot. If it exceeds the row median by more than 2×, scale the icon down until it does. Since v4 removed outline mode there is no second remedy to offer, so this stays what §5.1 already said it should be — a **diagnostic**, surfaced to the user, acted on by the user.

Ship step 6 as a diagnostic first: surface "this icon costs 4× the row median" in settings and let the user decide. Automatic dimming of specific logos will look like a rendering bug.

### 5.2 Antialiasing and glyph positioning

**Greyscale antialiasing only. ClearType and all subpixel rendering disabled**, for icons and text alike.

Subpixel rendering places fixed coloured fringes on fixed subpixels along every glyph edge. For static content at a static coordinate, that is differential subpixel wear at exactly the scale this project exists to prevent.

In WPF, three settings are required and v1 named only the first. They are listed here rather than deferred to implementation because the second one **silently defeats §6.1**:

- `TextOptions.TextRenderingMode="Grayscale"` on the root, and verify no `ClearTypeHint` survives anywhere in the tree.
- `TextOptions.TextFormattingMode="Ideal"`. The default in many WPF configurations is `Display`, which snaps glyphs to the pixel grid — that quantises the fractional micro-jitter §6.1 depends on into integer steps, producing exactly the discrete accumulating states §6.1 exists to avoid. This failure is invisible in testing; the bar will appear to jitter correctly while doing nothing.
- `UseLayoutRounding="False"` and `SnapsToDevicePixels="False"` on the drifting content group, for the same reason.

### 5.3 Outline mode — removed in v4

**Outline mode is not part of the design.** v2 promoted it to the primary defence for icon interiors; v4 removes it. This section is kept rather than deleted because §6.0 depends on knowing what used to fill that role and no longer does.

What it was: edge-detect the alpha silhouette plus internal luminance edges, render as a 1.5 px stroke, discard the fill. v2 put the saving at 70–85% of lit area for "pin sets dominated by large solid-fill logos".

**Why it is gone.** It was built (build order step 6), it worked, and it was measured against a real nine-icon set — `docs/measurements.md` records the run. Weighted across the row it removed **34.6%** of icon lit area, not 70–85%. Two thirds of that came from two icons; two icons gained nothing whatever. The premise was the problem: §5.3 assumed pin sets dominated by solid fills, and modern application icons are largely small shapes on transparent backgrounds with no interior left to reclaim.

Set against a recognisability cost on every icon it is applied to, and a per-icon settings surface needed to manage that unevenness, the project owner judged the return did not justify the complexity. **The decision is a product judgement, not a measurement dispute** — the measurement is not in doubt and is recorded above.

**What this costs, stated plainly.** Icon interiors now have no defence at all. §6.0's table is updated to say so. The mitigation that remains is the Tier 0 luminance floor: at rest an interior sits at L = 3.3%, which §2.4 puts 86–191× below light-mode desktop content and ~5.8× *above* a dark-mode application surface. For a light-mode user this changes nothing that mattered. For a dark-mode user — the user §2.4 identifies as the one this project is actually for — it gives up the only mechanism the design had for the interiors. That is the accepted cost.

§11.2 Q10, which asked whether auto-generated outlines looked acceptable, is withdrawn. It had been answered affirmatively for one icon set before the removal; the answer is now moot.

---

## 6. Motion model

### 6.0 What motion can and cannot do under P5

**The amplitude rule (unchanged, and now binding against us).** Shift amplitude must exceed the feature it is protecting. Displacing a 24 px icon by 2 px redistributes wear only along its edges; the interior still runs 100% duty.

P5 caps total drawn offset at the icon gutter — ±10 px at 100% scaling, before safety margin. Against a 24 px icon:

| Amplitude | Peak-to-peak travel | Always-lit core | Peak duty | Reduction |
| :---- | :---- | :---- | :---- | :---- |
| ±2 px | 4 px | 20 px | 1.000 | 1.00× |
| ±5.5 px *(v2 drift)* | 11 px | 13 px | 1.000 | 1.00× |
| ±12 px *(full gutter)* | 24 px | 0 px | 1.000 | 1.00× |
| ±24 px | 48 px | 0 px | 0.500 | 2.00× |
| ±32 px | 64 px | 0 px | 0.375 | 2.67× |
| ±64 px *(v1)* | 128 px | 0 px | 0.188 | 5.33× |

**Stated plainly: slot-bounded drift provides no peak-wear reduction for icon interiors whatsoever.** Meaningful reduction requires amplitude greater than the icon's own width, which is nearly three times the entire gutter. v1 achieved this and paid for it by putting icons over their neighbours' click targets. v2 declines to pay that price, per P5.

So the motion layer's honest job description is narrower than v1's:

| Problem | Covered by |
| :---- | :---- |
| Edge gradients, glyph strokes, thin features | Micro-jitter (§6.1) — fractional offsets vary the AA gradient continuously |
| The outer ~8 px ring of each icon | Slot-bounded drift (§6.2) |
| **Icon interiors** | **Nothing.** Not motion, and since v4 not outline mode either (§5.3). The Tier 0 luminance floor alone. Accepted — see below |
| Clock digit positions and the colon | Drift + independent frequency (§6.4) |

This is a real reduction in scope from v1 and should not be papered over. The compensating argument is §2.4: at Tier 0 the interiors sit at L = 3.3%, which is 86–191× below light-mode desktop content. Interiors are a genuine concern only for dark-mode users.

**And since v4 there is no answer for them.** v2 assigned that job to outline mode; §5.3 records why it was removed. The row above now reads "nothing" honestly rather than naming a mechanism that no longer exists. §11.2 Q11 — the intra-icon luminance ramp — is the only remaining idea that would address interiors without removing them, and it is speculative.

### 6.1 Micro-jitter — protects edges and strokes

| Parameter | Value |
| :---- | :---- |
| Amplitude | ±0.25 × A_max (≈ ±2 px at 100% scaling), both axes |
| Period | Two incommensurable sinusoids, 60 s and 83 s |
| Update rate | **2 Hz** |
| Rendering | **Fractional offsets**, resolved by the AA — do not snap to integers |
| Scope | Whole content group, coherent |

Fractional positioning matters more than amplitude here. Integer-snapped jitter quantises into a handful of discrete states that each still accumulate; sub-pixel offsets mean the edge gradient itself lands differently every frame. See §5.2 for the WPF setting that silently breaks this.

**Update rate is a v2 correction.** v1 implied continuous animation. At ±2 px over a 60 s period, a 60 Hz repaint moves the content **0.0035 px per frame** — 5.2 million repaints per day to accomplish nothing perceptible or measurable between frames. At 2 Hz the step is 0.105 px, which is more than fine for varying the AA gradient, at 172,800 repaints per day. Thirty times less GPU wake-up for identical benefit.

### 6.2 Slot-bounded drift — protects the icon periphery

Renamed from v1's "macro drift," because it no longer does the macro job.

**Amplitude is derived, not chosen.** For a slot of pitch *p* containing content of extent *e*:

```
A_max = (p − e) / 2 − 0.05·p        [the gutter, less a 5% safety margin]
A_drift = 0.70 × A_max
A_micro = 0.25 × A_max
```

The two are additive and must never exceed the gutter. Worked values:

| Scaling | Pitch | Icon | Gutter | A_max | Drift | Micro | Peak total | Clearance |
| :---- | :---- | :---- | :---- | :---- | :---- | :---- | :---- | :---- |
| 100% | 44 | 24 | ±10.0 | ±7.8 | ±5.5 | ±2.0 | ±7.4 | 2.6 px |
| 125% | 55 | 30 | ±12.5 | ±9.8 | ±6.8 | ±2.4 | ±9.3 | 3.2 px |
| 150% | 66 | 36 | ±15.0 | ±11.7 | ±8.2 | ±2.9 | ±11.1 | 3.9 px |
| 175% | 77 | 42 | ±17.5 | ±13.7 | ±9.6 | ±3.4 | ±13.0 | 4.5 px |
| 200% | 88 | 48 | ±20.0 | ±15.6 | ±10.9 | ±3.9 | ±14.8 | 5.2 px |

This answers "how far should it slide" the right way: **it slides as far as the slot allows and not one pixel further**, and the figure follows DPI automatically because it is derived from layout metrics rather than asserted in pixels.

| Parameter | Value |
| :---- | :---- |
| Amplitude, long axis | 0.70 × A_max (see table) |
| Amplitude, short axis | 0.50 × A_max_short, ≈ ±4 px at 100% |
| Period | **20 min**, triangular, **constant velocity** |
| Scope | Whole content group, coherent — icons never reorder or move relative to each other |

**Two v1 corrections here.**

*Eased turnarounds are removed.* v1 specified "triangular with eased turnarounds." Easing means dwelling longer at the extremes, which concentrates residence time at exactly the two positions the drift is trying to avoid, producing two hot bands. Uniform wear distribution requires **constant velocity**. The easing was presumably added to avoid a visible direction change — but at ±5.5 px over 20 minutes the velocity is **0.018 px/s (66 px/hour)**, so there is nothing to smooth. The turnaround is physically imperceptible.

*Period shortened from 45 min to 20 min.* The residence distribution of a triangle wave is period-independent, so this costs nothing in wear terms. It matters because a session shorter than the period samples a biased portion of the cycle: a 30-minute session against a 45-minute period leaves the bar parked off-centre for most of it. A 20-minute period means even short sessions traverse the full range. Velocity remains 0.018 px/s, far below any plausible detection threshold.

**Reordering remains prohibited.** It achieves wear spreading but destroys the pinned row's most valuable property, and under P5 it is doubly excluded.

### 6.3 Composition and headroom

Total offset = drift + micro, applied as a single render transform on the content group, clamped to the slot per P5.

Because drift is now slot-bounded, **v1's long-axis reservation of ≥64 px per side is deleted.** The reserved rectangle no longer needs drift headroom along the bar's length at all — a significant simplification.

| Metric | Content | Reserved | Reason |
| :---- | :---- | :---- | :---- |
| Horizontal bar height | 48 px | 56 px | Short-axis drift + corner rounding |
| Vertical bar width | 56 px | 64 px | Short-axis drift + corner rounding |
| Long-axis headroom | — | none required | Drift is contained within slots |

**Row inset at the aligned end (v3).** With the default left-aligned layout the first slot sits at the start of the bar, so there is nowhere for negative drift to go — content would clip at the screen edge. The row must therefore be inset from the aligned end by at least the peak total offset (7.4 px at 100% scaling).

This is **not a deviation.** Windows 11's own left-aligned taskbar already pads before the first icon, so matching that inset satisfies both fidelity and drift headroom at once. Measure the actual Win11 inset on the target machine and adopt it; if it turns out to be smaller than the peak offset, take the larger of the two and record the difference in §8.

The same applies to the aligned end of a vertical bar, and to the clock at the trailing end of any bar.

The **reserved AppBar rectangle is fixed and never re-registered during steady-state operation.** Changing it reflows every maximised window on the desktop. Drift happens strictly inside it. Resolution change, DPI change and monitor hotplug are the exceptions and need an explicit policy — see §11.2 Q4.

### 6.4 Clock

The clock has the static-segment problem in miniature: digits change, digit *positions* do not, and the colon never changes at all.

- Drifts with the content group.
- Plus an **independent micro-jitter frequency** — 71 s / 97 s against the icon row's 60 s / 83 s.
- Seconds are not displayed at Tier 0 or 1 — a ticking seconds field is high-frequency change in a tiny fixed area with almost no informational value.
- The colon does **not** blink. Blinking halves its duty cycle but doubles the transition count; it is not worth the visual noise.

**v1 correction.** v1 specified "an independent micro-jitter *phase* offset (~40% of cycle) so clock and icon-row wear patterns decorrelate." A phase offset on the same frequency produces an identical accumulated spatial distribution, merely time-shifted. Since wear integrates over many cycles, phase offset achieves literally nothing for the stated purpose — it decorrelates the instantaneous appearance only. Decorrelating the *wear pattern* requires different frequencies, as now specified.

### 6.5 Hit testing

Per P5, hit rectangles are computed from the **undrifted** layout and never move. Drawn content moves only within its own rectangle.

v1 listed the visual/clickable disengagement as "the main risk in the whole motion design." Under P5 that risk is eliminated by construction: peak total offset is ~75% of the gutter, so drawn content is always well inside its own hit rect, and clearance to the neighbouring slot never falls below 2.6 px at 100% scaling.

Remaining properties, now guaranteed rather than mitigated:

- Clicks land where the user expects at any drift phase, because what they see never leaves the region that responds.
- Icons never appear to belong to a slot other than their own.

**Start and the screen corner (v3 correction).** v2 claimed the Start button "retains the screen corner as an infinite-depth Fitts target." That was wrong about the reference: Windows 11's own left-aligned taskbar insets the Start button from the corner (§6.3), so Windows 11 does not have this property either.

P5 lets us have it anyway, for free. **Draw** the Start glyph at the Win11 inset, matching fidelity; **extend its hit rect** to the screen corner, which costs nothing because hit geometry is invisible. The result is strictly better than Windows 11 at zero visual cost — a corner slam always opens Start, at any drift phase. Listed in §8.

### 6.6 When motion stops

All motion — drift and micro-jitter — pauses on:

- Session lock, user idle beyond 5 minutes, and display power-down (DPMS off).
- Exclusive fullscreen (the bar is hidden anyway; the render loop should also stop).
- The system "show animations in Windows" setting being off, and any OS reduced-motion signal. See §11.2 Q5.

The idle and DPMS cases are not merely a power optimisation. **WOLED panels run their own compensation cycles when idle or on standby**, and a process that repaints continuously can keep the display awake and prevent those cycles from running. A motion layer that suppresses the panel's own far more effective mitigation would be a net loss. Motion resumes from its stored phase, not from zero.

**Caveat added in v3 — this may not be sufficient.** ManagedShell runs **two permanent 100 ms polling timers** regardless of what our render loop does: one re-asserting tray window Z-order, one re-hiding Explorer's taskbar (§11.1). That is 20 wake-ups per second that we do not control and cannot pause without losing the tray and letting the Explorer taskbar reappear.

Pausing our own motion is therefore necessary but possibly not sufficient for the display to idle. **Measure this before relying on §6.6** — if ManagedShell's timers alone defeat display idle, the entire idle-compensation argument collapses and the only remedies are patching ManagedShell to back its timers off when idle, or accepting it. Listed as §11.2 Q2.

---

## 7. Orientation specifications

Windows 11 supports bottom only. Top, left and right are original design work — there is no Microsoft reference to match, so the goal is *plausible* Windows 11, not *identical*.

### 7.0 The Start menu constraint

This governs all four orientations, so it comes first.

**The Start menu cannot be repositioned.** It is `StartMenuExperienceHost.exe`, a separate process that positions itself from where Explorer believes the taskbar to be — the bottom edge — and from `TaskbarAl`. No API accepts a position. Moving it requires DLL injection into that process and hooking `dwmapi.dll!DwmSetWindowAttribute`, which is out of scope (§11.1).

Because we *hide* Explorer's taskbar rather than move it, it stays logically on the bottom edge, and the Start menu opens there regardless of which edge we occupy.

**Decision: accept this and ship all four edges.** §10's non-goal stands; no custom launcher is built. The mitigation is layout, not code — place the Start button as near as possible to where the menu will appear, per orientation below.

**In the default configuration the problem does not arise.** Bottom edge + left-aligned icons + `TaskbarAl = 0` puts the menu directly beneath our Start button. The mismatch exists only on the three non-default edges.

`TaskbarAl` (`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`, 0 = left, 1 = centre) should be **set to match our own alignment on startup and restored on exit**, exactly as ManagedShell already saves and restores Explorer's autohide state (§11.1). We are modifying that key's neighbours already; this is a strictly smaller intrusion and it is what makes the default configuration coherent.

### 7.1 Bottom — default and reference

- Reserved height 56 px, content band 48 px, icons 24 px in 44 px hit targets.
- **Icon row left-aligned by default**, centred as a user setting. This is a deviation from the Windows 11 default (centred) and is listed in §8 — it is chosen because left-alignment makes the Start menu land correctly (§7.0).
- Start button leftmost. Glyph drawn at the Windows 11 left inset; hit rect extended to the screen corner (§6.5).
- Row inset from the left by the Win11 default padding, which also provides drift headroom (§6.3).
- Clock bottom-right, horizontal `HH:MM` over `ddd d MMM`.
- Tray overflow flyout expands **upward**.
- DPI: all metrics scale with per-monitor DPI. Drift amplitudes are derived from pitch and icon extent (§6.2), so they scale automatically and are never expressed as absolute pixels.

**This is the shipping default and therefore the placement whose problems must be solved rather than avoided** — notably the autohide contention in §11.1, which is specific to the bottom edge.

### 7.2 Top

Mirror of bottom. Flyouts expand downward. Clock top-right. Icons left-aligned by default, as bottom.

Start menu opens at the bottom of the screen — the maximum possible distance from the button that invoked it. No layout mitigation is available for this orientation; the button is at the top by definition. Accepted per §7.0.

Conflicts to resolve: application menu bars, Windows' own centred notification and focus toasts, the drag-to-top-edge maximise gesture, and the Snap Layouts hover zone. Not a blocker, but test with maximised legacy Win32 apps early.

### 7.3 Left and right

- Reserved width 64 px, content band 56 px.
- Icons 24 px in 44 × 44 hit targets, stacked vertically, aligned to the **bottom** of the long axis by default (mirroring the bottom bar's left-alignment), centred as a user setting.
- Running indicator pill rotates 90° — it sits on the screen-edge side of the icon, not below it.
- **Start button at the bottom of a vertical bar by default** (v3 change; v2 said top), user-overridable. The Start menu will open at the bottom of the screen, so a bottom-anchored Start button sits adjacent to it rather than diagonally opposite. On a left bar this produces something close to the correct Windows 11 relationship.
- Row inset from the bottom end per §6.3.
- Flyouts expand toward screen centre.

**Clock, vertical.** No Microsoft reference exists. Recommended:

| Tier | Display |
| :---- | :---- |
| 0 | `HH` over `MM`, two stacked lines, no separator |
| 1 | same, plus day-of-month beneath |
| 2 | flyout with full date and time, expanding toward screen centre |

Do not rotate text 90°. Rotated text at 56 px width is unreadable at a glance and defeats the purpose of having a clock. Stacked is the right answer.

**Tray, vertical.** Icons stack; the overflow chevron sits at the end of the stack and its flyout is a grid, expanding toward screen centre. This is the least-solved area in existing vertical bars — worth surveying how YASB and Zebar handle it before committing.

---

## 8. Deviations from Windows 11 fidelity

Complete list. Anything not here should match Windows 11.

| Deviation | Reason |
| :---- | :---- |
| Background pure black, no acrylic/mica | P1 — the largest single wear saving available |
| Icons monochrome at rest, full chroma on hover | P4 + P3 — colour costs most and is needed least at rest |
| **Hover colour is luminance-capped to L ≤ 0.45** | §3.2 — full chroma at ~40% of Win11's luminance; removes the design's sensitivity to unmeasured Tier 2 duty |
| Running-indicator pill is monochrome, not accent-coloured | P4; accent colour returns at Tier 2 only |
| Everything drifts, within its own slot | Core thesis, bounded by P5 |
| Start glyph is an outline, not the four-colour logo | Most temporally invariant element on the entire screen; four saturated squares is the worst case for it |
| Greyscale AA only | Subpixel fringing is fixed-position differential wear |
| Bar dims to near-invisible when the pointer is far | P3 — converts the 92% duty state into near-zero cost |
| No seconds on the clock | High-frequency change in a fixed micro-area, negligible information |
| **Notification badge pulses rather than holding steady** | §3.3 — quarters the duty of the brightest static element in the design |
| Reserved band is 8 px larger than content | Short-axis drift and corner rounding *(v1 attributed this to long-axis drift headroom, which §6.3 no longer requires)* |
| **Icon row left-aligned by default** (Win11 defaults to centred) | §7.0 — left alignment plus `TaskbarAl = 0` makes the Start menu open directly beneath our Start button |
| **Start button hit rect extends to the screen corner** | §6.5 — Win11 insets its Start button and loses the Fitts corner target. P5 lets us draw at the Win11 inset while claiming the corner in hit geometry only. Free, and strictly better |
| **Start button at the bottom of vertical bars** | §7.3 — puts it adjacent to where the Start menu will actually open |
| **`TaskbarAl` is written on startup and restored on exit** | §7.0 — required for the default configuration to be coherent |

---

## 9. Wear accounting

The capability that only a replacement can offer: you know exactly what you drew and where.

**Implementation**

- **`float64`** accumulator over the reserved rect. At 3840 × 56 that is 1680 KB — still trivial, and see below for why `float32` is not an option.
- Each tick, add **`L_linear(pixel)^n × Δt`** where `L_linear` is the post-gamma-decode luminance of what was actually rendered, including the drift transform. Store *n* alongside the data so the ranking can be recomputed for a different exponent without re-collecting.
- Sample at 1 Hz. Persist every 5 minutes; flush on exit and on session lock.
- Store as raw accumulator plus a monotonic wall-clock total so the data survives config changes.
- Accumulate from the known scene graph and transform, not by GPU readback of the framebuffer. It is cheaper, exact, and does not force a pipeline stall once per second.

**Two v1 corrections.**

*The exponent.* v1 specified `L_linear × Δt` — linear in luminance. That contradicts §2's central claim that wear goes as `L^1.7`, and it measures emitted light rather than degradation. As written it would systematically under-rank bright elements, which is precisely backwards for a tool whose job is to find them.

*The precision.* v1 specified `float32`. Simulating naive per-second accumulation at a typical pinned-icon rate, `float32` drifts **4.2% off the true total within 5 million additions** — under two months of runtime — and the error compounds. `float32` carries ~7 significant digits, so once the accumulator reaches the millions, per-second increments of ~0.003 fall below the representable step and are discarded entirely. The bug is silent and produces a plausible-looking heatmap. Use `float64`; if memory ever matters, accumulate per-second into a small `float32` buffer and fold it into a `float64` accumulator hourly, which the same simulation shows tracks the exact value to 5 decimal places.

**Phase discipline**

Phase 1 is **instrumentation only** — accumulate, persist, and provide a heatmap view. No layout feedback. Watch several weeks of real data before letting anything respond to it. A feedback loop built on an unvalidated model will produce confident nonsense, and you will not be able to tell.

**Stated limits** (put these in the README, not just the code):

- **Relative, not absolute.** The model ranks wear within the bar. It cannot compare bar wear to desktop wear, and it has no calibration against actual panel degradation. §2.4's comparison is arithmetic on assumed content luminances, not measurement.
- **Blind to panel compensation.** WOLED panels run their own compensation cycles on standby and track their own usage internally. Those effects are invisible to this model and may partly or wholly cancel what it measures.
- **Ignores everything above the bar.** The other ~95% of the screen ages independently, and per §2.4 it very likely ages faster.
- **Assumes a fixed SDR brightness.** The nits column and all absolute claims scale with the user's actual panel setting; the model does not observe it.

Overclaiming here is the fastest way to make the project look unserious. Under-claim it and let the heatmap speak.

---

## 10. Non-goals

- Replacing or reimplementing the Windows 11 Start menu (we invoke it; the menu's position is not ours to control — see §7.0).
- Cross-platform anything.
- Widgets, weather, news, Copilot surface.
- Animation for its own sake — any animation that keeps otherwise-dark pixels lit is a regression.
- Theming engines and skin formats. One design, executed correctly.

---

## 11. Shell integration — resolved

**§11.1 supersedes v2's Q1 and Q2, both of which are now answered.** Remaining open items are in §11.2.

### 11.1 How Explorer suppression and tray hosting actually work

Established by reading ManagedShell's source rather than by inference. Recorded here because three constraints fall out of it that the design must respect.

**Explorer taskbar suppression** (`ExplorerHelper`) is three steps: `SetTaskbarState(ABState.AutoHide)` via `SHAppBarMessage(ABM_SETSTATE)`, then `SetWindowPos(..., HWND_BOTTOM, SWP_HIDEWINDOW)` on `Shell_TrayWnd`, the Start button (class atom `0xC017`) and every `Shell_SecondaryTrayWnd`, then a 100 ms watchdog that re-hides whenever Explorer re-shows. The autohide call is the load-bearing one — **it is what releases Explorer's reserved work area**, without which both bars reserve space. Original state is saved and restored on clean shutdown.

**Tray hosting** (`TrayService`) works by Z-order interception, which is not obvious. ManagedShell registers its own window class *literally named* `Shell_TrayWnd` in our process and creates a hidden topmost popup of that class. `FindWindow("Shell_TrayWnd", null)` returns the topmost match, so raising ours and sinking Explorer's makes applications deliver their tray data to us. A second 100 ms timer re-asserts this. `SendTaskbarCreated()` broadcasts the `TaskbarCreated` message so apps re-add their icons. Anything we do not handle is **forwarded on to Explorer's real tray** — it is a proxy, not a replacement, so Explorer's own tray keeps working underneath.

`SetTrayHostSizeData` / `FillTrayHostSizeData` report our notification area's rectangle for `ABM_GETTASKBARPOS`. **This is the hook that makes four-edge flyout anchoring possible** — confirm it is called on every layout change, including drift.

**Three constraints this imposes:**

1. **Never register an autohide AppBar.** Windows permits one autohide AppBar per screen edge, and Explorer's taskbar holds that slot on its edge as a consequence of the suppression method. Our bar must be a normal space-reserving AppBar. This is compatible with the design as written — Tier 0 is a reserved bar painted black, not an autohiding one — but it permanently forecloses adding a real auto-hide option later.
2. **Two 100 ms timers run permanently and we do not control them.** See the §6.6 caveat.
3. **Crash recovery must restore more than our own AppBar.** A hard kill leaves the user's Explorer taskbar autohidden *and* `TaskbarAl` overwritten (§7.0). Both need restoring, not just `ABM_REMOVE`.

**The bottom edge has a contention problem, and it is our default.** With Explorer's taskbar autohidden on the bottom, pointer travel to that edge triggers its reveal; the watchdog re-hides it within 100 ms. RetroBar users report the visible symptoms — a persistent 1 px line and flicker. Our bar sits exactly there and its Tier 2 trigger is proximity to that same edge, so the two systems contend precisely where the user interacts most.

Since §7.1 makes bottom the shipping default, **this must be solved rather than avoided.** Bring the bar up on the **left** edge first to validate it in isolation, then move to bottom immediately and treat the contention as a release blocker. Severity is currently unmeasured.

**The Start menu is resolved as a product decision, not a technical one** — see §7.0. It cannot be repositioned without DLL injection into `StartMenuExperienceHost.exe`; we accept that and ship all four edges. §10's non-goal stands.

### 11.2 Open questions

Ordered by how much they affect the build.

1. **Is the motion layer worth shipping at all?** §6.0 concedes that slot-bounded drift does nothing for icon interiors, and §2.4 shows the bar is 86–191× below light-mode desktop content — though it is 5.8× *above* a dark-mode surface, which is the case that keeps the question open. The honest possibility is that under P5 the motion layer's remaining benefit — edge and stroke protection — does not justify its complexity, and that pure black plus a dark Tier 0 is the whole product. (v4 removed outline mode from that list on the same kind of reasoning, one step ahead of this question — see §5.3.) **Build the drift layer early, run the wear accumulator with drift on and off for a week each, and let §9's heatmap decide.** This is now the most important experiment in the project.

2. **Does ManagedShell's polling defeat display idle?** Two 100 ms timers run regardless of our render loop (§11.1). If they alone keep the display awake, §6.6's whole argument — pause motion so the panel can run its compensation cycles — collapses, and with it a meaningful part of the wear case. Measure before relying on it. Remedies if it fails: patch ManagedShell to back its timers off when idle, or accept it and delete the claim.

3. **Bottom-edge autohide contention severity.** §11.1 identifies the mechanism; nobody has measured how bad it looks. This gates the shipping default, so it needs a number early.

4. **AppBar lifecycle.** §6.3 forbids runtime re-registration, but resolution change, DPI change, monitor hotplug and display topology changes all force it. Define the policy. Separately: if the process crashes with an AppBar registered, the reserved space leaks until Explorer restarts — needs an `ABM_REMOVE` on unhandled exception plus a heartbeat or watchdog for hard kills, and per §11.1 it must also restore Explorer's autohide state and `TaskbarAl`.

5. **Accessibility, and whether Tier 0 is defensible.** A bar at 6.6 nits is comfortable in a dim room and effectively invisible in a daylit one; the design silently assumes the former. It also fails every contrast guideline that exists. At minimum: a user-configurable Tier 0 floor, honouring Windows high-contrast mode by disabling the tier system outright, and honouring the reduced-motion signal by disabling §6 entirely. Decide whether Tier 0 should default to something legible and let enthusiasts opt down, rather than the reverse.

6. **QD-OLED.** P4's efficiency argument is specific to WRGB panels with an unfiltered white emitter. Samsung QD-OLED panels — a large share of desktop OLEDs — have no white subpixel, so desaturation buys substantially less there. P1, P3 and P5 are unaffected. Decide whether to detect panel type, expose it as a setting, or simply scope the README to WOLED and say so plainly.

7. **Keyboard and touch paths.** `Win+`number and `Win+T` activation with Explorer's taskbar hidden — does the shell still route them, and to what? Tier 2 on keyboard focus is specified in §3 but the focus model itself is not. And touch/pen have no pointer proximity at all, so they need a fallback that pins to Tier 1 (v1's Q6, unresolved).

8. **UI Automation.** A custom WPF bar is invisible to screen readers without explicit `AutomationPeer` implementations. Scope this before the settings UI, not after.

9. **Unspecified surfaces.** Thumbnail previews, jump lists, right-click context menus, and drag-onto-taskbar targets are all Windows 11 fidelity requirements and all large bright transients. They are almost certainly fine under P3, but they are currently absent from the tier model and should be placed in it explicitly rather than by omission.

10. **~~Outline mode quality.~~ Withdrawn in v4.** Asked whether auto-generated outlines looked acceptable across a real pin set. It was built and measured, and the answer was yes for one real set — but outline mode itself is gone (§5.3), so the question no longer bears on anything. Kept numbered so Q11 and Q12 keep their references.

11. **Intra-icon luminance ramp.** A very slow, low-amplitude spatial luminance gradient sweeping across an icon would redistribute interior wear *without moving anything*, sidestepping P5 entirely. Roughly ±15% over ~20 minutes should be imperceptible on a dim monochrome icon. Speculative, possibly ugly, but it is the only mechanism identified that addresses interiors without removing them. Prototype after Q1 resolves.

12. **Multi-monitor with a non-OLED secondary.** Does the OLED treatment apply per-monitor, or globally? Per-monitor is correct and more work.

---

## 12. Change log

### v1 → v2 — errors corrected

| Location | v1 | v2 |
| :---- | :---- | :---- |
| §2 | "Full white on hover costs an order of magnitude less than even a very dark rest state" — cited as the document's most important number | It was 3×, not 10×. Corrected, and the claim is made true again by the Tier 2 cap (§2.3) |
| §2 | Rest states compared at 100% duty in isolation | Duty-weighted budget (§2.2), which shows Tier 1 was 33–43% of the total |
| §5.1 | P90 normalisation "makes the wear budget predictable per slot" | Withdrawn — P90 equalises peak, not energy. Optional integrated-luminance pass added |
| §6.2 | "Triangular with eased turnarounds" | Constant velocity. Easing concentrated residence time at the extremes, creating hot bands |
| §6.4 | Clock decorrelated by a phase offset on the same frequency | Phase offset is a no-op for accumulated wear. Different frequencies specified |
| §9 | Accumulate `L_linear × Δt` | `L_linear^n × Δt`. v1 contradicted §2 and under-ranked bright elements |
| §9 | `float32` accumulator | `float64`. `float32` drifts 4.2% within two months and then silently discards increments |
| §5.2 | `TextRenderingMode="Grayscale"` only | Adds `TextFormattingMode="Ideal"`, which v1 omitted and whose default silently defeats §6.1 |

### v1 → v2 — design changes

- **P5 strengthened to slot containment**, per the product constraint that no app may appear outside its usual place. Resolves v1's drift-versus-click conflict by construction rather than by trade-off.
- **Drift amplitude derived from layout metrics** (±5.5 px at 100% scaling) instead of asserted (±64 px). Scales with DPI automatically.
- **§6.0 added**, conceding plainly that slot-bounded drift cannot protect icon interiors, and reassigning that job to outline mode.
- **Tier 2 luminance-capped** at L ≤ 0.45 with full chroma retained. Removes the design's dependence on unmeasured hover duty.
- **Tier 1 darkened one step; proximity band 200 px → 120 px and now DPI-scaled.**
- **Notification badge given actual mitigation** — pulse, independent phase, decay when ignored.
- **Micro-jitter update rate specified at 2 Hz**, down from an implied 60 Hz doing 0.0035 px per frame.
- **Drift period 45 min → 20 min**, so short sessions traverse the full range.
- **§6.6 added** — motion pauses on lock, idle and DPMS off, so the panel's own compensation cycles can run.
- **§2.4 added** — absolute context. The bar is 86–191× below light-mode desktop content but ~6× *above* a dark-mode application surface. This identifies who the project is actually for.
- **Long-axis reservation of ≥64 px per side deleted** — no longer needed.

**Deferred to §11** — accessibility and the Tier 0 contrast floor, reduced-motion, AppBar lifecycle and crash recovery, QD-OLED scope, keyboard and touch paths, UI Automation, and the unspecified Win11 surfaces (previews, jump lists, context menus, drag targets).

### v2 → v3

**Resolved**

| Item | Resolution |
| :---- | :---- |
| v2 §11 Q1 — Start menu anchoring | **Cannot be repositioned without DLL injection.** Accepted; ship all four edges; no launcher. §10 unchanged. Now §7.0 |
| v2 §11 Q2 — tray hosting | **Solved.** Mechanism documented in §11.1 — Z-order interception of `FindWindow("Shell_TrayWnd")`, with unhandled messages proxied to Explorer |

**Design changes**

- **Default configuration is now bottom edge with left-aligned icons** (§7.1). Win11 defaults to centred, so this is a deviation — taken because left-alignment plus `TaskbarAl = 0` makes the Start menu open directly beneath our Start button, which removes the §7.0 problem entirely in the shipping default.
- **`TaskbarAl` is written on startup and restored on exit** (§7.0), alongside the Explorer autohide state ManagedShell already saves.
- **Start button moves to the bottom of vertical bars** (§7.3; v2 said top), so it sits adjacent to where the menu will actually open.
- **Row inset at the aligned end** (§6.3). Left-alignment leaves no room for negative drift at the corner. Windows 11 already pads before the first icon when left-aligned, so matching that inset satisfies fidelity and drift headroom simultaneously — not a deviation.
- **§6.5 corrected.** v2 claimed the Start button "retains the screen corner as an infinite-depth Fitts target." Windows 11 insets its own Start button, so it has no such property. P5 lets us draw at the Win11 inset while extending the hit rect to the corner — free, and strictly better than the reference.
- **Never register an autohide AppBar** (§11.1). Explorer's taskbar holds the autohide slot on its edge as a consequence of how it is suppressed. Forecloses a real auto-hide option later.
- **§6.6 caveat added.** ManagedShell runs two permanent 100 ms timers we do not control, so pausing our render loop may not be sufficient for the display to idle. Now §11.2 Q2.
- **Crash recovery scope widened** (§11.2 Q4) to restoring Explorer's autohide state and `TaskbarAl`, not just `ABM_REMOVE`.
- **Bottom-edge autohide contention identified** (§11.1) and, because bottom is now the default, promoted from "avoid it" to a release blocker (§11.2 Q3).
- **Build order revised** — bring up on the left edge first to isolate our bar from Explorer's autohide reveal, then move to bottom immediately.

### v3 → v4

**One change, and it is a removal.**

| Location | v3 | v4 |
| :---- | :---- | :---- |
| §5.3 | Outline mode, promoted to the **primary defence for icon interiors** | **Removed from the design.** Built, measured against a real icon set, and judged not worth its complexity |
| §6.0 | Icon interiors covered by "outline mode, plus the Tier 0 luminance floor" | Icon interiors covered by **nothing** but the Tier 0 floor. Accepted, and said plainly |
| §5.1 step 6 | Flagged icons could be dimmed *or* offered outline mode | Diagnostic only. There is no second remedy left to offer |
| §11.2 Q10 | Open — do auto-generated outlines look acceptable? | Withdrawn. Moot |
| Build order 6 | Outline mode, "now load-bearing" | Struck. Numbering preserved so steps 7–9 keep their references |

**The evidence.** `docs/measurements.md` step 6 measured the built implementation across nine real icons: **34.6%** of lit area removed row-wide against §5.3's claimed 70–85%, two thirds of it from two icons, two icons unchanged. §5.3's premise — "pin sets dominated by large solid-fill logos" — does not describe modern icon sets.

**What it costs.** The design no longer has any mechanism for icon interiors. Under P5 drift cannot reach them by construction (§6.0), and outline mode was the only other thing proposed. §2.4 is what makes this survivable rather than fatal: a Tier 0 interior at L = 3.3% is 86–191× below light-mode desktop content, so for most users this was already a rounding error. For the dark-mode user §2.4 identifies as the project's real audience, it is a genuine give-up. §11.2 Q11's intra-icon luminance ramp remains the only untried idea that would address interiors without removing them.

**What survives it.** The §5.1 integrated-luminance diagnostic stays — it never depended on outline mode, and "this icon costs 4× the row median" is still worth telling the user. The ridge-detection work is in the git history if it is ever wanted back.

---

## Appendix — build order

1. Bar that renders, registers a space-reserving AppBar, shows a live window list. No tray, Explorer alive with its taskbar hidden. **Bring up on the left edge first** — it is the only edge with no autohide contention, so it isolates our bar's behaviour from Explorer's (§11.1).
2. Move to the bottom edge and characterise the contention. This is the shipping default, so this is a release blocker rather than a curiosity (§11.2 Q3).
3. Black background, monochrome normalisation, tier system, hover proximity. Left-aligned layout with the row inset (§6.3).
4. Wear accumulator and heatmap, instrumentation only. **Ahead of the motion layer** — §11.2 Q1 cannot be answered without it.
5. Motion layer — micro-jitter then slot-bounded drift. **Run a week with drift on and a week with it off, and compare heatmaps.** If the difference does not justify the code, delete it and ship without.
6. ~~Outline mode.~~ Built, measured, and **removed in v4** — see §5.3. The step is kept numbered because steps 7–9 are referenced by number throughout the docs.
7. Tray hosting via ManagedShell.
8. Start button and menu invocation, `TaskbarAl` handling.
9. Pinning, drag-reorder, per-monitor config, settings UI.

Steps 1–5 produce something you can evaluate the entire thesis against. If the design is wrong, it is wrong before any shell plumbing is written.

The interactive prototype (`oled-taskbar-prototype.html`) covers steps 3 and 5 in the browser and can be used to settle layout and perceptibility questions before any C# is written. It is not a substitute for step 4 — only real rendering on the real panel produces meaningful wear data.
