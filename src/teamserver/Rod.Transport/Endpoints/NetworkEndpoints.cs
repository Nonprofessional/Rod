using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The host-network read view for the operator UI: the interfaces this
/// teamserver could bind an implant-facing listener to. The listener form
/// turns the list into its bind dropdown (plus the wildcard all-interfaces
/// entry the client adds itself), so an operator picks a real address instead
/// of typing one from memory. Global, not engagement-scoped: the host has one
/// network whatever engagements run on it, and the view is read-only -- it
/// carries no bind or socket state.
/// </summary>
public static class NetworkEndpoints
{
    public static IEndpointRouteBuilder MapNetworkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: the interface list requires an authenticated
        // operator session, like every other operator surface.
        var group = endpoints.MapGroup("/network").RequireAuthorization();
        group.MapGet("/interfaces", ListInterfacesAsync).WithName("ListNetworkInterfaces");
        return endpoints;
    }

    private static IResult ListInterfacesAsync()
    {
        // One entry per unicast address on an up interface, loopback included:
        // the loopback is a legitimate dev bind. Down interfaces and addressless
        // pseudo-devices drop out naturally under the operational filter.
        var entries = new List<InterfaceResponse>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily
                    is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                {
                    continue;
                }
                entries.Add(new InterfaceResponse(nic.Name, address.Address.ToString()));
            }
        }

        return Results.Ok(entries);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>One bindable address: the interface's OS name and the address itself.</summary>
    public sealed record InterfaceResponse(string Name, string Address);
}
