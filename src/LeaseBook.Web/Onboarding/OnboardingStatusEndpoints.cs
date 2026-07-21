using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Onboarding;

/// <summary>
/// WP-5 Task 5.2: derived onboarding wizard state (M7).
///
/// <c>GET /api/onboarding/status</c> — returns a six-flag snapshot of wizard progress, each
/// flag computed from existing data on the ambient RLS transaction (no dedicated status table).
/// The SPA checklist drives its step-gating from this response; the OpenAPI client types the
/// response record after regen. <c>HasJournalData</c> is the empty-dashboard-takeover gate: the
/// wizard only hijacks an org with no journal data, so an org with operational activity (e.g. the
/// seeded demo org) is never redirected into onboarding even when its import flags are all false.
/// </summary>
public sealed class OnboardingStatusEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/onboarding/status",
                async (ISender sender, DbContext db, CancellationToken ct) =>
                {
                    // banksConfigured: org has ≥1 bank account (active or inactive — any configured).
                    // Dispatches through the existing Directory ListBankAccounts query (ADR-007 boundary).
                    var banks = await sender.Query(new ListBankAccounts(ActiveOnly: false), ct);
                    var banksConfigured = banks.Count > 0;

                    // entitiesImported: ≥1 ImportBatch in an entity-kind family with a successful status.
                    // EntityKind.ToString() produces PascalCase names (Owners, Properties, Units, TenantsLeases).
                    var entityKinds = new[]
                    {
                        "Owners", "Properties", "Units", "TenantsLeases",
                    };
                    var entityStatuses = new[] { "posted", "posted_with_errors" };

                    var entitiesImported = await db.Set<ImportBatch>()
                        .AnyAsync(b => entityKinds.Contains(b.EntityKind)
                                       && entityStatuses.Contains(b.Status), ct);

                    // balancesImported: ≥1 ImportBatch in a balance-kind family with a successful status.
                    // EntityKind.ToString() produces PascalCase names (OwnerBalances, DepositLiabilities,
                    // BankBalances, TenantReceivables, HeldPmFees).
                    var balanceKinds = new[]
                    {
                        "OwnerBalances", "DepositLiabilities", "BankBalances", "TenantReceivables", "HeldPmFees",
                    };

                    var balancesImported = await db.Set<ImportBatch>()
                        .AnyAsync(b => balanceKinds.Contains(b.EntityKind)
                                       && entityStatuses.Contains(b.Status), ct);

                    // verified: ≥1 MigrationVerification row exists (regardless of sign-off).
                    var verified = await db.Set<MigrationVerification>()
                        .AnyAsync(ct);

                    // signedOff: ≥1 MigrationVerification row with SignedOffAt set.
                    var signedOff = await db.Set<MigrationVerification>()
                        .AnyAsync(v => v.SignedOffAt != null, ct);

                    // hasJournalData: ≥1 journal entry of any kind (the empty-dashboard-takeover gate).
                    // Dispatched to Accounting via ISender (ADR-007 — no cross-module journal SQL here).
                    var hasJournalData = await sender.Query(new HasJournalEntries(), ct);

                    return TypedResults.Ok(new OnboardingStatusResponse(
                        banksConfigured,
                        entitiesImported,
                        balancesImported,
                        verified,
                        signedOff,
                        hasJournalData));
                })
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Onboarding")
            .Produces<OnboardingStatusResponse>();
    }
}

/// <summary>
/// Derived wizard-step state for the M7 import-first onboarding checklist.
/// Each flag is computed from existing data; no dedicated status table exists.
/// </summary>
public sealed record OnboardingStatusResponse(
    /// <summary>True when the org has ≥1 configured bank account.</summary>
    bool BanksConfigured,
    /// <summary>True when ≥1 entity import batch (owners/properties/units/tenants_leases) has been posted (with or without errors).</summary>
    bool EntitiesImported,
    /// <summary>True when ≥1 balance import batch (owner_balances/deposit_liabilities/bank_balances/tenant_receivables) has been posted (with or without errors).</summary>
    bool BalancesImported,
    /// <summary>True when ≥1 migration verification has been run.</summary>
    bool Verified,
    /// <summary>True when ≥1 migration verification has been signed off.</summary>
    bool SignedOff,
    /// <summary>True when the org has ≥1 journal entry (any posted financial activity). The empty-dashboard-takeover gate: the wizard only appears for an org with no journal data.</summary>
    bool HasJournalData);
