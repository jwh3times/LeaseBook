namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// Base of every typed domain rejection raised by the Operations run pipeline. Carries a stable
/// <see cref="Code"/> that the host's single Operations exception handler maps to an HTTP status on
/// ADR-025's contract — the same shape <c>AccountingDomainException</c> has had since M1.
/// <para>
/// <b>Why the code lives on the exception rather than in the handler.</b> The first typed Operations
/// error hard-coded its code and status inside a dedicated handler, which works exactly once: the
/// second one either duplicates the handler or starts a type-switch that has to be kept in sync with
/// a list of exception types nobody can see from the throw site. Putting the code on the exception
/// makes the vocabulary reviewable where it is raised, keeps <c>Program.cs</c>'s handler chain at one
/// entry per module, and means adding an error is one new subclass and one <c>switch</c> arm.
/// </para>
/// <para>
/// <b>The code is API surface.</b> Clients branch on it (the SPA re-previews on
/// <c>capabilities_changed</c> and must not on anything else), so a code is renamed only as a
/// deliberate breaking change alongside its consumers.
/// </para>
/// </summary>
public abstract class OperationsDomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
