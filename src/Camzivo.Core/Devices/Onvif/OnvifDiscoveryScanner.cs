using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Camzivo.Core.Devices.Onvif;

/// <summary>
/// Sends the WS-Discovery probe to the multicast group and collects replies for a bounded window.
///
/// Expected failures are returned, not thrown: a network without multicast (VPN, VLAN) must produce "no devices
/// found", not an exception in the UI. The scanner never blocks longer than <c>duration</c>.
/// </summary>
public sealed class OnvifDiscoveryScanner
{
    public const int DiscoveryPort = 3702;

    public async Task<(IReadOnlyList<DiscoveredDevice> Devices, string? Warning)> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var devices = new List<DiscoveredDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var message = Encoding.UTF8.GetBytes(WsDiscoveryProbe.BuildMessage());

        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
        }
        catch (SocketException)
        {
            // Joining is best effort: some adapters allow sending but not receiving multicast.
        }

        await client.SendAsync(message, new IPEndPoint(IPAddress.Parse("239.255.255.250"), DiscoveryPort), cancellationToken)
            .ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);

        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var received = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var datagram = Encoding.UTF8.GetString(received.Buffer);

                foreach (var device in WsDiscoveryProbe.ParseDatagram(datagram))
                {
                    var key = string.IsNullOrEmpty(device.EndpointReference) ? device.DisplayName : device.EndpointReference;
                    if (seen.Add(key))
                    {
                        devices.Add(device);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // A single malformed or refused datagram must not end the scan.
            }
        }

        var warning = devices.Count == 0
            ? "No ONVIF devices answered. Check that the camera is on this network, ONVIF is enabled, and multicast is not blocked."
            : null;

        return (devices, warning);
    }
}
