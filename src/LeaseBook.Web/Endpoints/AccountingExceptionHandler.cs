using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Observability;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// Maps a typed <see cref="AccountingDomainException"/> from the posting engine to an RFC 7807
/// ProblemDetails with the §C.5 status: 422 for malformed-entry errors, 409 for state conflicts. M1
/// has no write endpoints, so this surfaces only through tests/seeder today — but the mapping is wired
/// now so the M3 composer inherits it.
/// </summary>
public sealed class AccountingExceptionHandler(ILogger<AccountingExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AccountingDomainException domain)
        {
            return false;
        }

        var status = domain.Code switch
        {
            "unbalanced_entry" or "invalid_line" or "unknown_account" or "pm_income_owner_dim"
                => StatusCodes.Status422UnprocessableEntity,
            "reconciliation_not_found" or "entry_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict, // period_closed, insufficient_liability, reserve_floor,
                                                // already_reversed, duplicate_source_ref, account_period_locked,
                                                // reconciliation_unbalanced, reconciliation_state,
                                                // held_fees_* / pm_income_owner_dimension (opening shape)
        };

        var extensions = new Dictionary<string, object?>();
        if (domain is DuplicateSourceRefException duplicate)
        {
            // Contractual, not a leak: LedgerComposer reads this to treat a double-submit as success.
            extensions["existingEntryId"] = duplicate.ExistingEntryId;
        }

        // Expected domain rejection: Warning, not Error. The typed properties carry the detail the
        // user-facing message deliberately omits.
        logger.LogWarning(
            LogEvents.DomainRejection,
            "Domain rejection {Code} mapped to {Status} for {ExceptionType}",
            domain.Code, status, domain.GetType().Name);

        await ProblemResults.Problem(httpContext, domain.Code, domain.Message, status, extensions)
            .ExecuteAsync(httpContext);
        return true;
    }
}
