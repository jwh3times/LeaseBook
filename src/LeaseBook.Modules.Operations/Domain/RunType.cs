namespace LeaseBook.Modules.Operations.Domain;

/// <summary>The kind of bulk run — determines which <c>IRunStrategy</c> the engine routes to.</summary>
public enum RunType
{
    Rent,
    LateFee,
    Disbursement,
}
