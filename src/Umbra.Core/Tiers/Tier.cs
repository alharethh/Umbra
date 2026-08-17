namespace Umbra.Core.Tiers;

/// <summary>
/// The four state tiers of design-spec §3. Every element declares its appearance in each.
/// </summary>
/// <remarks>
/// Tier 0 and Tier 1 together run ~100% duty and together constitute the entire wear budget (P3).
/// v1 treated Tier 1 as transient and optimised the wrong state — Tier 1 <i>is</i> rest, it just
/// has a pointer nearby.
/// </remarks>
public enum Tier
{
    /// <summary>Pointer beyond the ambient band. ~92% duty. The bar is nearly invisible.</summary>
    Dormant = 0,

    /// <summary>Pointer within the ambient band. ~8% duty. Legible and scannable.</summary>
    Ambient = 1,

    /// <summary>
    /// Pointer within the engaged band, over the bar, or keyboard focus. ~0.1% duty.
    /// Full Windows 11 fidelity and full chroma, luminance-capped per §3.2.
    /// </summary>
    Engaged = 2,

    /// <summary>Unread notification or attention flash. Rare, and must be visible from Tier 0.</summary>
    Alert = 3,
}

/// <summary>Elements that declare a per-tier appearance (§3.1).</summary>
public enum BarElement
{
    /// <summary>The bar itself. Black at every tier — not near-black (§4).</summary>
    Background,

    /// <summary>A pinned application icon with nothing running.</summary>
    PinnedIcon,

    /// <summary>A running application's icon.</summary>
    RunningIcon,

    /// <summary>The running-indicator pill. Monochrome, not accent-coloured, until Tier 2.</summary>
    RunningIndicator,

    /// <summary>The clock. No seconds below Tier 2 (§6.4).</summary>
    Clock,

    /// <summary>The Start glyph — an outline, never the four-colour logo (§8).</summary>
    StartGlyph,

    /// <summary>A notification-area icon.</summary>
    TrayIcon,

    /// <summary>The unread-notification badge. The brightest static element in the design (§3.3).</summary>
    NotificationBadge,
}
