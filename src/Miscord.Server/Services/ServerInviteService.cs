using Microsoft.EntityFrameworkCore;
using Miscord.Server.Data;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public sealed class ServerInviteService : IServerInviteService
{
    private readonly MiscordDbContext _db;
    private static readonly Random _random = new();
    private const string InviteCodeChars = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int InviteCodeLength = 8;

    public ServerInviteService(MiscordDbContext db)
    {
        _db = db;
    }

    public async Task<ServerInvite> CreateInviteAsync(
        Guid? creatorId,
        int maxUses = 0,
        DateTime? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var code = GenerateInviteCode();

        // Ensure code is unique
        while (await _db.ServerInvites.AnyAsync(i => i.Code == code, cancellationToken))
        {
            code = GenerateInviteCode();
        }

        var invite = new ServerInvite
        {
            Code = code,
            CreatedById = creatorId,
            MaxUses = maxUses,
            ExpiresAt = expiresAt
        };

        _db.ServerInvites.Add(invite);
        await _db.SaveChangesAsync(cancellationToken);

        return invite;
    }

    public async Task<ServerInvite?> ValidateInviteCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var invite = await _db.ServerInvites
            .FirstOrDefaultAsync(i => i.Code == code, cancellationToken);

        if (invite == null)
            return null;

        // Check if revoked
        if (invite.IsRevoked)
            return null;

        // Check if expired
        if (invite.ExpiresAt.HasValue && invite.ExpiresAt.Value < DateTime.UtcNow)
            return null;

        // Check if max uses exceeded
        if (invite.MaxUses > 0 && invite.CurrentUses >= invite.MaxUses)
            return null;

        return invite;
    }

    public async Task UseInviteAsync(string code, CancellationToken cancellationToken = default)
    {
        var invite = await _db.ServerInvites
            .FirstOrDefaultAsync(i => i.Code == code, cancellationToken);

        if (invite != null)
        {
            invite.CurrentUses++;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IEnumerable<ServerInvite>> GetAllInvitesAsync(CancellationToken cancellationToken = default)
    {
        return await _db.ServerInvites
            .Include(i => i.CreatedBy)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RevokeInviteAsync(Guid inviteId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        // Verify the requesting user is a server admin
        var user = await _db.Users.FindAsync(new object[] { requestingUserId }, cancellationToken);
        if (user == null || !user.IsServerAdmin)
            throw new UnauthorizedAccessException("Only server administrators can revoke invites.");

        var invite = await _db.ServerInvites.FindAsync(new object[] { inviteId }, cancellationToken);
        if (invite == null)
            throw new InvalidOperationException("Invite not found.");

        invite.IsRevoked = true;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> HasAnyUsersAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Users.AnyAsync(cancellationToken);
    }

    public async Task<string?> GetOrCreateBootstrapInviteAsync(CancellationToken cancellationToken = default)
    {
        // First, check for any unlimited bootstrap invite (created by null user, not revoked, not expired)
        var unlimitedBootstrap = await _db.ServerInvites
            .FirstOrDefaultAsync(i =>
                i.CreatedById == null &&
                i.MaxUses == 0 &&  // Unlimited invites
                !i.IsRevoked &&
                (i.ExpiresAt == null || i.ExpiresAt > DateTime.UtcNow),
                cancellationToken);

        if (unlimitedBootstrap != null)
            return unlimitedBootstrap.Code;

        // If no users exist, create a single-use bootstrap invite
        if (!await HasAnyUsersAsync(cancellationToken))
        {
            var invite = await CreateInviteAsync(null, maxUses: 1, cancellationToken: cancellationToken);
            return invite.Code;
        }

        // Users exist and no unlimited invite available
        return null;
    }

    public async Task<Guid?> GetInviterIdAsync(string code, CancellationToken cancellationToken = default)
    {
        var invite = await _db.ServerInvites
            .FirstOrDefaultAsync(i => i.Code == code, cancellationToken);

        return invite?.CreatedById;
    }

    private static string GenerateInviteCode()
    {
        var chars = new char[InviteCodeLength];
        for (int i = 0; i < InviteCodeLength; i++)
        {
            chars[i] = InviteCodeChars[_random.Next(InviteCodeChars.Length)];
        }
        return new string(chars);
    }
}
