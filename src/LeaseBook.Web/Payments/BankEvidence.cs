using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeaseBook.Modules.Payments.Processing;

namespace LeaseBook.Web.Payments;

/// <summary>
/// What the fixture operator says a bank received for one Stripe payout (ADR-054): the connected
/// account the payout is on, Stripe's id for it, the amount and the day. Stripe never says this, so
/// in the sandbox a person does, with a command.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BankEvidenceNotice(string Account, string PayoutId, decimal BankAmount, DateOnly BankDate);

/// <summary>What became of bank evidence handed to the runner: an engine outcome, or <see cref="Refused"/>.</summary>
public sealed record BankEvidenceDelivery(string Outcome, Guid? Id = null, string? Status = null, string? Reason = null)
{
    /// <summary>Not taken at all: not signed with the fixture key, not readable, or not in a sandbox host.</summary>
    public const string Refused = "Refused";
}

/// <summary>
/// Signs and verifies bank evidence with the fixture's signing key, as the simulator's drivers sign
/// what they deliver. The key is in a fixture manifest and nowhere else, so only someone at the
/// fixture's own machine can produce evidence. There is no route that takes it: the one caller is
/// the fixture command. It is verified in a Stripe sandbox host only; the simulator's payouts arrive
/// with their bank evidence, and no other mode has payouts.
/// </summary>
public sealed class BankEvidenceSigner(SimulationSettings settings, TimeProvider clock)
{
    // Not the simulator's scheme under another name: the purpose is part of what is signed, so
    // nothing signed as a simulator notice can be presented as bank evidence, or the other way round.
    private const string Purpose = "bank-evidence.";
    private const int MaxBytes = 4096;
    private const int ToleranceSeconds = 300;

    public string Sign(byte[] body)
    {
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return timestamp + "." + Convert.ToHexStringLower(Signature(body, timestamp));
    }

    /// <summary>The evidence a body holds, when the body proves authentic and recent in a sandbox host; otherwise null.</summary>
    public BankEvidenceNotice? Verify(byte[] body, string? signature)
    {
        if (settings.Mode != PaymentModes.StripeSandbox || settings.SigningKey.Length < 32 || body.Length > MaxBytes
            || signature is null) { return null; }
        var parts = signature.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > 253402300799
            || Math.Abs((clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).TotalSeconds) > ToleranceSeconds)
        { return null; }
        byte[] supplied;
        try { supplied = Convert.FromHexString(parts[1]); } catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(supplied, Signature(body, parts[0]))) { return null; }
        // Nothing is parsed before the signature matches.
        try
        {
            var notice = JsonSerializer.Deserialize<BankEvidenceNotice>(body);
            return notice is { Account.Length: > 0, PayoutId.Length: > 0 } ? notice : null;
        }
        catch (JsonException) { return null; }
    }

    private byte[] Signature(byte[] body, string timestamp) => HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(settings.SigningKey), (byte[])[.. Encoding.UTF8.GetBytes(Purpose + timestamp + "."), .. body]);
}
