using System.Security.Cryptography;
using System.Text.Json;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Portal;

/// <summary>Purpose-bound proof authenticates the organization before any org-scoped database read.</summary>
public sealed class PortalInvitationProof(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("LeaseBook.PortalInvitation.v1");
    public string Issue(Guid orgId, Guid id) => _protector.Protect($"{orgId:D}:{id:D}");
    public (Guid OrgId, Guid Id) Read(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) { throw PortalEnrollmentService.Invalid(); }
        try
        {
            var parts = _protector.Unprotect(token).Split(':');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var org) && org != Guid.Empty
                && Guid.TryParse(parts[1], out var id) && id != Guid.Empty) { return (org, id); }
        }
        catch (CryptographicException) { }
        throw PortalEnrollmentService.Invalid();
    }
}

/// <summary>Transport seam. A successful call means durable test-inbox storage, never real email delivery.</summary>
public interface IPortalInvitationDelivery
{
    Task DeliverAsync(Guid orgId, PortalTestMessage message, CancellationToken ct);
    Task<PortalTestMessage?> ReadAsync(Guid orgId, Guid invitationId, CancellationToken ct);
}

public sealed class LocalPortalInvitationDelivery(IWebHostEnvironment environment) : IPortalInvitationDelivery
{
    private static string FileName(Guid orgId, Guid id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeaseBook", "portal-test-inbox", orgId.ToString("N"), id.ToString("N") + ".json");

    public async Task DeliverAsync(Guid orgId, PortalTestMessage message, CancellationToken ct)
    {
        RequireDevelopment();
        var path = FileName(orgId, message.InvitationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) { return; }
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(message), ct);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<PortalTestMessage?> ReadAsync(Guid orgId, Guid invitationId, CancellationToken ct)
    {
        RequireDevelopment();
        var path = FileName(orgId, invitationId);
        return File.Exists(path) ? JsonSerializer.Deserialize<PortalTestMessage>(await File.ReadAllTextAsync(path, ct)) : null;
    }

    private void RequireDevelopment()
    {
        if (!environment.IsDevelopment()) { throw new InvalidOperationException("The portal test inbox requires Development."); }
    }
}

/// <summary>Durable outbox consumer: rows become visible only after the manager's transaction commits.</summary>
public sealed class PortalInvitationDispatcher(AppDbContext db, IOrgContext org, PortalInvitationProof proof,
    IPortalInvitationDelivery delivery, IConfiguration configuration, TimeProvider clock)
{
    public async Task DispatchAsync(CancellationToken ct)
    {
        var orgId = org.OrgId ?? throw new InvalidOperationException("Invitation delivery requires organization scope.");
        await LockAsync(db, orgId, ct);
        var rows = await db.Set<PortalInvitation>().Where(i => i.Status == "pending" && i.DeliveryStatus == "pending"
            && i.ExpiresAt > clock.GetUtcNow()).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.DeliveryAttempts++;
            try
            {
                var baseUrl = configuration["PortalEnrollment:BaseUrl"] ?? "http://localhost:5373";
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) || (origin.Scheme != "http" && origin.Scheme != "https")
                    || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
                { throw new InvalidOperationException("Invalid portal enrollment base URL."); }
                await delivery.DeliverAsync(orgId, new(row.Id, row.Email,
                    new Uri(origin, "/portal/enroll").AbsoluteUri + "#token=" + proof.Issue(orgId, row.Id)), ct);
                row.DeliveryStatus = "delivered";
                row.DeliveryError = null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Transport errors may contain credentials or message content. Neither logs nor audits receive them.
                row.DeliveryStatus = "failed";
                row.DeliveryError = "Test delivery failed. Retry the invitation.";
            }
        }
        await db.SaveChangesAsync(ct);
    }

    internal static Task<int> LockAsync(AppDbContext db, Guid orgId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"portal-enrollment:" + orgId.ToString("D")}, 0))", ct);
}

public sealed class PortalInvitationWorker(IServiceScopeFactory scopes, ILogger<PortalInvitationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var listing = scopes.CreateAsyncScope();
                var orgs = await listing.ServiceProvider.GetRequiredService<AppDbContext>().Orgs.Select(o => o.Id).ToListAsync(stoppingToken);
                foreach (var orgId in orgs)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "portal-invitation-delivery",
                        () => scope.ServiceProvider.GetRequiredService<PortalInvitationDispatcher>().DispatchAsync(stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Portal test delivery could not scan pending invitations; it will retry.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
