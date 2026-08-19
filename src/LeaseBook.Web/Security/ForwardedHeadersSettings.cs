using System.Net;

namespace LeaseBook.Web.Security;

/// <summary>
/// Whether, and whom, to trust for <c>X-Forwarded-*</c> (F9 / ADR-041). Bound from the
/// <c>ForwardedHeaders</c> configuration section.
/// <para>
/// The per-IP auth rate limiter partitions on <c>RemoteIpAddress</c>. Behind a reverse proxy that
/// address is the proxy's, so every client shares one partition unless the proxy is named here and
/// its <c>X-Forwarded-For</c> is honoured. Naming it is a <b>trust</b> decision, not a formatting
/// one: whoever can reach the app directly can claim any client address once the header is believed.
/// That is why this is off by default and why an enabled-but-empty configuration is refused rather
/// than accepted.
/// </para>
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// Off by default. Local and container-less development connects directly, so the connection
    /// address already <i>is</i> the client and honouring a forwarded header would only let a client
    /// choose its own partition.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Individual proxy addresses to trust, e.g. the ingress hop.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Proxy networks to trust, in CIDR form, e.g. the ingress subnet.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// How many proxy hops to walk back through. One by default: with a single ingress in front of
    /// the app, a longer limit would let a client prepend its own entries and be believed.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Validates the trust configuration, throwing rather than degrading.
    /// <para>
    /// <b>Enabled with nothing named is refused.</b> ASP.NET Core's default known-networks list is
    /// loopback, so an enabled-but-empty configuration silently ignores every forwarded header from a
    /// real proxy and leaves the limiter exactly as broken as it was — a misconfiguration that looks
    /// like success. Failing at startup is the same fail-closed posture the organization context and
    /// actor attribution already take.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Enabled with no proxies or networks, or an entry that is not a valid address/CIDR.
    /// </exception>
    public (List<IPAddress> Proxies, List<IPNetwork> Networks) Resolve()
    {
        if (!Enabled)
        {
            return ([], []);
        }

        if (KnownProxies.Length == 0 && KnownNetworks.Length == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Enabled is true but neither {SectionName}:KnownProxies nor " +
                $"{SectionName}:KnownNetworks names anything. The framework would then trust only " +
                "loopback, so forwarded headers from the real ingress would be ignored and the " +
                "per-client rate-limit partition would stay collapsed. Name the ingress, or set " +
                $"{SectionName}:Enabled to false.");
        }

        if (ForwardLimit < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ForwardLimit must be at least 1 when forwarded headers are enabled.");
        }

        var proxies = new List<IPAddress>();
        foreach (var proxy in KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out var address))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownProxies contains '{proxy}', which is not an IP address.");
            }

            proxies.Add(address);
        }

        var networks = new List<IPNetwork>();
        foreach (var network in KnownNetworks)
        {
            if (!IPNetwork.TryParse(network, out var parsed))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownNetworks contains '{network}', which is not CIDR notation " +
                    "(for example 10.0.0.0/8).");
            }

            networks.Add(parsed);
        }

        return (proxies, networks);
    }
}
