namespace LeaseBook.Web.Seeding;

/// <summary>
/// Shared environment guard for the demo and cutover seeders. Both
/// provision a well-known, source-committed admin password, so running either in Production would be
/// an account-takeover vector. They are development / demo / e2e fixtures only — real orgs are
/// provisioned through the M7 onboarding/invite flow. The guard fails closed: an unset environment
/// defaults to Production, so a deployment that forgets to mark itself non-Production is also refused.
/// </summary>
internal static class SeederGuard
{
    public static void RequireNonProduction(IServiceProvider services)
    {
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Demo/cutover seeders ship a well-known admin password and must never run in " +
                "Production (account-takeover risk). Provision real orgs via the M7 onboarding/invite " +
                "flow; run the fixture seeders only in Development or another non-Production environment.");
        }
    }
}
