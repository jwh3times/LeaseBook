using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Web.Operations;

/// <summary>The one mapping between public bulk-run route names and Operations run types.</summary>
internal static class RunTypeRoute
{
    public static RunType Parse(string raw) => TryParse(raw, out var runType)
        ? runType
        : throw new InvalidOperationException("A bulk-run query was dispatched without valid input.");

    public static bool TryParse(string raw, out RunType runType)
    {
        runType = raw.ToLowerInvariant() switch
        {
            "rent" => RunType.Rent,
            "latefee" => RunType.LateFee,
            "disbursement" => RunType.Disbursement,
            _ => (RunType)(-1),
        };
        return (int)runType >= 0;
    }
}
