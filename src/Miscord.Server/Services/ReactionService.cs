using Microsoft.EntityFrameworkCore;
using Miscord.Server.Data;
using Miscord.Server.DTOs;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public sealed class ReactionService : IReactionService
{
    private readonly MiscordDbContext _db;

    public ReactionService(MiscordDbContext db) => _db = db;

    public async Task<ReactionUpdatedEvent> AddReactionAsync(Guid messageId, Guid userId, string emoji, CancellationToken cancellationToken = default)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        var user = await _db.Users.FindAsync([userId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        // Check if user already reacted with this emoji
        var existingReaction = await _db.MessageReactions
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji, cancellationToken);

        if (existingReaction is null)
        {
            var reaction = new MessageReaction
            {
                Emoji = emoji,
                MessageId = messageId,
                UserId = userId
            };

            _db.MessageReactions.Add(reaction);
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Get updated count
        var count = await _db.MessageReactions
            .CountAsync(r => r.MessageId == messageId && r.Emoji == emoji, cancellationToken);

        return new ReactionUpdatedEvent(
            messageId,
            message.ChannelId,
            emoji,
            count,
            userId,
            user.Username,
            Added: true
        );
    }

    public async Task<ReactionUpdatedEvent?> RemoveReactionAsync(Guid messageId, Guid userId, string emoji, CancellationToken cancellationToken = default)
    {
        var message = await _db.Messages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        var user = await _db.Users.FindAsync([userId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var reaction = await _db.MessageReactions
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji, cancellationToken);

        if (reaction is null)
            return null;

        _db.MessageReactions.Remove(reaction);
        await _db.SaveChangesAsync(cancellationToken);

        // Get updated count
        var count = await _db.MessageReactions
            .CountAsync(r => r.MessageId == messageId && r.Emoji == emoji, cancellationToken);

        return new ReactionUpdatedEvent(
            messageId,
            message.ChannelId,
            emoji,
            count,
            userId,
            user.Username,
            Added: false
        );
    }

    public async Task<List<ReactionSummary>> GetReactionsAsync(Guid messageId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var reactions = await _db.MessageReactions
            .Include(r => r.User)
            .Where(r => r.MessageId == messageId)
            .ToListAsync(cancellationToken);

        // Group by emoji
        var grouped = reactions
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionSummary(
                g.Key,
                g.Count(),
                g.Any(r => r.UserId == currentUserId),
                g.Select(r => new ReactionUser(r.UserId, r.User?.Username ?? "Unknown")).ToList()
            ))
            .ToList();

        return grouped;
    }
}
