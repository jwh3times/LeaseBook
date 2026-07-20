using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace LeaseBook.Tests.Integration.Observability;

public sealed class AccountingExceptionStatusTests
{
    // Plain array, not TheoryData, so the completeness fact below can iterate it without
    // depending on xunit's row API. The Theory adapts it.
    private static readonly (AccountingDomainException Exception, int Status)[] Rows =
    [
        (new UnbalancedEntryException("Cash", 10m, 20m), StatusCodes.Status422UnprocessableEntity),
        (new InvalidLineException(InvalidLineReason.NoLines), StatusCodes.Status422UnprocessableEntity),
        (new UnknownAccountException("X"), StatusCodes.Status422UnprocessableEntity),
        (new PmIncomeOwnerDimException(), StatusCodes.Status422UnprocessableEntity),
        (new PeriodClosedException(2026, 1), StatusCodes.Status409Conflict),
        (new InsufficientLiabilityException(LiabilityKind.Deposit, 10m, 5m), StatusCodes.Status409Conflict),
        (new InsufficientReceivableException(ReceivableSource.Deposit, 10m, 5m), StatusCodes.Status409Conflict),
        (new ReserveFloorException(10m, 5m, 3m, Guid.NewGuid()), StatusCodes.Status409Conflict),
        (new AlreadyReversedException(Guid.NewGuid(), AlreadyReversedReason.AlreadyReversed), StatusCodes.Status409Conflict),
        (new AccountPeriodLockedException(Guid.NewGuid(), 2026, 1), StatusCodes.Status409Conflict),
        (new ReconciliationUnbalancedException(1.23m), StatusCodes.Status409Conflict),
        (new ReconciliationStateException(ReconciliationStateProblem.AlreadyFinalized), StatusCodes.Status409Conflict),
        (new ReconciliationNotFoundException(Guid.NewGuid()), StatusCodes.Status404NotFound),
        (new EntryNotFoundException(Guid.NewGuid()), StatusCodes.Status404NotFound),
        (new DuplicateSourceRefException("ref", Guid.NewGuid()), StatusCodes.Status409Conflict),
        // Task 14 adds NoTrustAccountException
    ];

    public static TheoryData<int> RowIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Rows.Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RowIndexes))]
    public async Task Every_domain_exception_maps_to_its_documented_status(int rowIndex)
    {
        var ct = TestContext.Current.CancellationToken;
        var (exception, expectedStatus) = Rows[rowIndex];
        var handler = new AccountingExceptionHandler(
            NullLogger<AccountingExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        httpContext.Response.Body = new MemoryStream();

        (await handler.TryHandleAsync(httpContext, exception, ct)).ShouldBeTrue();
        httpContext.Response.StatusCode.ShouldBe(
            expectedStatus, $"{exception.GetType().Name} ({exception.Code})");
    }

    [Fact]
    public void The_matrix_covers_every_concrete_exception_type()
    {
        // A new exception type must be added to Rows — this is what makes the Theory a
        // full-switch guard rather than a sample.
        var concrete = typeof(AccountingDomainException).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(AccountingDomainException).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n);

        var covered = Rows.Select(r => r.Exception.GetType().Name).Distinct().OrderBy(n => n);

        covered.ShouldBe(concrete);
    }
}
