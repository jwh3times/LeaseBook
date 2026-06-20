using LeaseBook.Modules.Accounting.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// Maps a typed <see cref="AccountingDomainException"/> from the posting engine to an RFC 7807
/// ProblemDetails with the §C.5 status: 422 for malformed-entry errors, 409 for state conflicts. M1
/// has no write endpoints, so this surfaces only through tests/seeder today — but the mapping is wired
/// now so the M3 composer inherits it.
/// </summary>
public sealed class AccountingExceptionHandler : IExceptionHandler
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
            "reconciliation_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict, // period_closed, insufficient_liability, reserve_floor,
                                                // already_reversed, duplicate_source_ref, account_period_locked,
                                                // reconciliation_unbalanced, reconciliation_state
        };

        var extensions = new Dictionary<string, object?> { ["code"] = domain.Code };
        if (domain is DuplicateSourceRefException duplicate)
        {
            extensions["existingEntryId"] = duplicate.ExistingEntryId;
        }

        await Results.Problem(detail: domain.Message, statusCode: status, title: domain.Code, extensions: extensions)
            .ExecuteAsync(httpContext);
        return true;
    }
}
