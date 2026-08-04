using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LeaseBook.Modules.Capabilities.Contracts;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Modules.Capabilities.Resolution;

/// <summary>
/// Derives the opaque <see cref="CapabilitySet.Version"/> token. Kept separate from the reader
/// because Task 6's in-transaction resolution and Task 10's preview/confirm comparison must both
/// produce and compare tokens under exactly the same rule — two derivations would silently break the
/// concurrency check they exist to serve.
/// <para>
/// <b>The property.</b> Version equality must imply set equality, in both directions, across hosts
/// and across time. That is met by hashing a canonical encoding of the whole resolved set: same
/// pairs ⇒ same bytes ⇒ same digest; different pairs ⇒ different bytes.
/// </para>
/// <para>
/// <b>Why the registry discriminator.</b> Re-flagging a <see cref="Registry.Capability"/> in source —
/// flipping <c>RequiresGrant</c>, <c>DefaultEnabled</c> or <c>IsMoneyPath</c> — can leave every
/// resolved value unchanged (a live <c>feature_flags</c> row masks a default change entirely) while
/// the meaning of the set has moved. Folding the registry's shape into the digest makes a
/// code-side change break version equality even when no database row did.
/// </para>
/// <para>
/// <b>Two derivations that are wrong, and are not used here.</b> A cache-load timestamp: two hosts
/// resolving identical state at different instants produce different versions, so every
/// cross-instance preview/confirm pair is rejected spuriously — Task 10 compares these server-side
/// across hosts. A per-org mutation counter: it misses a deployment-wide flag flip, so two genuinely
/// different sets compare equal, which is the direction that lets a confirm post under capabilities
/// the preview never saw.
/// </para>
/// </summary>
public static class CapabilityVersion
{
    /// <summary>
    /// Bumped only if the canonical encoding below changes shape. It keeps an old token from
    /// colliding with a new one that happens to hash the same bytes under a different scheme.
    /// </summary>
    private const string Scheme = "v1";

    /// <summary>
    /// The source-code registry's shape, hashed once per process. Ordered by name so the digest does
    /// not depend on declaration order in <see cref="CapabilityCatalog.All"/>.
    /// </summary>
    private static readonly string RegistryShape = string.Concat(
        CapabilityCatalog.All
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => string.Create(
                CultureInfo.InvariantCulture,
                $"{Delimited(c.Name)}:{Flag(c.RequiresGrant)}{Flag(c.DefaultEnabled)}{Flag(c.IsMoneyPath)}{Flag(c.IsFixture)};")));

    /// <summary>
    /// Hashes the ordered <c>(name, value)</c> pairs of a resolved set plus the registry shape.
    /// Every pair in <paramref name="values"/> participates, including keys outside the registry, so
    /// the equality implication holds for the dictionary actually handed to
    /// <see cref="CapabilitySet.From"/> rather than for a filtered view of it.
    /// </summary>
    public static string Compute(IReadOnlyDictionary<string, bool> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var canonical = new StringBuilder()
            .Append(Scheme).Append('\n')
            .Append(Delimited(RegistryShape)).Append('\n');

        foreach (var pair in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            canonical.Append(Delimited(pair.Key)).Append('=').Append(Flag(pair.Value)).Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));

        // Base64Url, not plain Base64: the token travels in JSON today and may end up in a header or
        // a query string later, and '+' / '/' would need escaping in either.
        return $"{Scheme}.{Base64Url.EncodeToString(digest)}";
    }

    private static char Flag(bool value) => value ? '1' : '0';

    /// <summary>
    /// Length-prefixes a variable-length field, making the encoding <b>injective</b>.
    /// <para>
    /// Without this, the separators are ambiguous whenever a key can contain one: the single entry
    /// <c>{"a=1\nb": true}</c> and the pair <c>{"a": true, "b": true}</c> both encode to the bytes
    /// <c>a=1\nb=1\n</c>, so two different sets would share a version — precisely the failure
    /// <see cref="Compute"/> promises cannot happen. Unreachable through the registry today, but
    /// <see cref="Compute"/> is public and takes an arbitrary dictionary, so the guarantee has to
    /// hold for its actual input domain rather than for the inputs we expect. Rejecting the
    /// characters instead would trade a silent collision for a throw on data that is otherwise fine.
    /// </para>
    /// </summary>
    private static string Delimited(string field) =>
        string.Create(CultureInfo.InvariantCulture, $"{field.Length}:{field}");
}
