using System.Net.NetworkInformation;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>What the machine is connected through.</summary>
internal enum NetworkKind
{
    /// <summary>Nothing operational — the glyph shows a disconnected globe.</summary>
    Offline,

    /// <summary>A wired connection.</summary>
    Ethernet,

    /// <summary>A wireless one.</summary>
    WiFi,
}

/// <summary>
/// The network indicator, drawn by us because Windows 11 never sends one.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is not a notification icon on this machine and waiting for one is waiting for ever.</b>
/// Every arriving icon has been logged with its GUID across several sessions:
/// <c>NETWORK_GUID</c> never appears, while <c>VOLUME_GUID</c> does — so it is not that Windows 11
/// has stopped publishing system indicators, it is that the network one belongs to the XAML Quick
/// Settings cluster rather than to the notify-icon stream. The pinned list still carries
/// <c>NETWORK_GUID</c>, so if a build ever does send one it lands in the right place; this draws
/// the indicator in the meantime.
/// </para>
/// <para>
/// <b>Event-driven, with no timer.</b> <c>NetworkChange</c> raises on a thread-pool thread when an
/// address or availability changes, which is the whole of what this needs — §6.6 is about not
/// adding standing wake-ups, and a poll for something that changes twice a day would be exactly
/// that. The events fire off the UI thread, so the owner marshals <see cref="Changed"/>.
/// </para>
/// <para>
/// <b>Link state, not reachability.</b> This reports what the machine is connected <i>through</i>,
/// not whether the internet is on the other end. Windows draws a globe with a warning for
/// connected-but-no-internet and that distinction needs `INetworkListManager`'s connectivity
/// flags; it is deliberately not attempted here, so a dead upstream still shows as connected.
/// </para>
/// </remarks>
internal sealed class NetworkWatcher : IDisposable
{
    private bool _disposed;

    public NetworkWatcher()
    {
        Kind = Read();

        NetworkChange.NetworkAddressChanged += OnChanged;
        NetworkChange.NetworkAvailabilityChanged += OnChanged;

        BringUpLog.Write($"network indicator: {Kind} at startup");
    }

    /// <summary>Raised when the connection kind changes. <b>Not on the UI thread.</b></summary>
    public event Action? Changed;

    /// <summary>What the machine is currently connected through.</summary>
    public NetworkKind Kind { get; private set; }

    /// <summary>A description for the tooltip.</summary>
    public string Description => Kind switch
    {
        NetworkKind.Ethernet => "Connected — Ethernet",
        NetworkKind.WiFi => "Connected — Wi-Fi",
        _ => "No network connection",
    };

    private void OnChanged(object? sender, EventArgs e)
    {
        NetworkKind kind = Read();
        if (kind == Kind) return;

        Kind = kind;
        BringUpLog.Write($"network indicator: now {kind}");

        Changed?.Invoke();
    }

    /// <summary>
    /// The interface that is actually carrying traffic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Having a gateway is what distinguishes it</b>, and this is the part that is easy to get
    /// wrong. A developer machine has several interfaces that are "up" and mean nothing — Hyper-V
    /// and WSL virtual switches, VPN adapters, Bluetooth PAN. Picking the first operational
    /// non-loopback interface reports whichever of those Windows happened to enumerate first, and
    /// on a machine with Hyper-V that is usually a virtual Ethernet adapter, so the indicator
    /// would read Ethernet while the user is on Wi-Fi.
    /// </para>
    /// <para>
    /// Wireless wins a tie. Where both a cable and Wi-Fi hold a gateway, Windows shows the one it
    /// is routing over, which this cannot see — but a machine with both is far likelier to be a
    /// laptop in a dock, where saying Wi-Fi when the cable is live is the less confusing error.
    /// </para>
    /// </remarks>
    private static NetworkKind Read()
    {
        try
        {
            NetworkKind found = NetworkKind.Offline;

            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                IPInterfaceProperties properties = adapter.GetIPProperties();
                if (properties.GatewayAddresses.Count == 0) continue;

                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    return NetworkKind.WiFi;

                found = NetworkKind.Ethernet;
            }

            return found;
        }
        catch (NetworkInformationException)
        {
            // The stack can refuse mid-reconfiguration. Nothing is a safer answer than a wrong one.
            return NetworkKind.Offline;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NetworkChange.NetworkAddressChanged -= OnChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnChanged;
    }
}
