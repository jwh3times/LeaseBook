using LeaseBook.Web.Security;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// F9 / ADR-041: which proxies are believed for <c>X-Forwarded-*</c>, and the refusal to be
/// half-configured.
/// <para>
/// The per-IP auth rate limiter partitions on <c>RemoteIpAddress</c>. Behind a reverse proxy that is
/// the proxy's address unless its forwarded header is honoured, so every client shares one partition.
/// Believing the header is a trust decision — anyone who can reach the app directly can then claim
/// any client address — which is why this is off unless deployment config names the ingress.
/// </para>
/// <para>
/// No container needed: this is the pure settings contract.
/// </para>
/// </summary>
public sealed class ForwardedHeadersSettingsTests
{
    [Fact]
    public void Disabled_by_default_and_resolves_to_trusting_nobody()
    {
        var settings = new ForwardedHeadersSettings();

        settings.Enabled.ShouldBeFalse(
            "a direct-connect environment must not honour a header the client itself can set");

        var (proxies, networks) = settings.Resolve();
        proxies.ShouldBeEmpty();
        networks.ShouldBeEmpty();
    }

    /// <summary>
    /// The failure this guard exists for. ASP.NET Core pre-trusts loopback, so enabling forwarded
    /// headers without naming a proxy silently ignores every header a real ingress sends — leaving
    /// the rate-limit partition exactly as collapsed as before, while the configuration reads as
    /// though the problem were fixed.
    /// </summary>
    [Fact]
    public void Enabled_with_nothing_named_is_refused_rather_than_silently_trusting_loopback()
    {
        var settings = new ForwardedHeadersSettings { Enabled = true };

        var ex = Should.Throw<InvalidOperationException>(() => settings.Resolve());
        ex.Message.ShouldContain("KnownProxies");
        ex.Message.ShouldContain("KnownNetworks");
    }

    [Fact]
    public void Named_proxies_and_networks_are_parsed()
    {
        var settings = new ForwardedHeadersSettings
        {
            Enabled = true,
            KnownProxies = ["10.1.2.3"],
            KnownNetworks = ["10.0.0.0/8", "192.168.0.0/16"],
        };

        var (proxies, networks) = settings.Resolve();

        proxies.Select(p => p.ToString()).ShouldBe(["10.1.2.3"]);
        networks.Count.ShouldBe(2);
        networks[0].ToString().ShouldBe("10.0.0.0/8");
    }

    [Fact]
    public void A_malformed_proxy_address_is_refused()
    {
        var settings = new ForwardedHeadersSettings
        {
            Enabled = true,
            KnownProxies = ["not-an-address"],
        };

        Should.Throw<InvalidOperationException>(() => settings.Resolve())
            .Message.ShouldContain("not an IP address");
    }

    [Fact]
    public void A_malformed_network_is_refused()
    {
        var settings = new ForwardedHeadersSettings
        {
            Enabled = true,
            KnownNetworks = ["10.0.0.0"], // no prefix length
        };

        Should.Throw<InvalidOperationException>(() => settings.Resolve())
            .Message.ShouldContain("CIDR");
    }

    /// <summary>
    /// One hop by default: with a single ingress in front of the app, walking further back would let
    /// a client prepend its own entries and have them believed.
    /// </summary>
    [Fact]
    public void Forward_limit_defaults_to_one_and_must_be_positive()
    {
        new ForwardedHeadersSettings().ForwardLimit.ShouldBe(1);

        var settings = new ForwardedHeadersSettings
        {
            Enabled = true,
            KnownNetworks = ["10.0.0.0/8"],
            ForwardLimit = 0,
        };

        Should.Throw<InvalidOperationException>(() => settings.Resolve())
            .Message.ShouldContain("ForwardLimit");
    }
}
