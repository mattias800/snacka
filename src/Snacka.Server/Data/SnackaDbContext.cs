using Microsoft.EntityFrameworkCore;
using Snacka.Shared.Models;

namespace Snacka.Server.Data;

public sealed class SnackaDbContext : DbContext
{
    public SnackaDbContext(DbContextOptions<SnackaDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Community> Communities => Set<Community>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();
    public DbSet<UserCommunity> UserCommunities => Set<UserCommunity>();
    public DbSet<VoiceParticipant> VoiceParticipants => Set<VoiceParticipant>();
    public DbSet<ServerInvite> ServerInvites => Set<ServerInvite>();
    public DbSet<ChannelReadState> ChannelReadStates => Set<ChannelReadState>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<CommunityInvite> CommunityInvites => Set<CommunityInvite>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User configuration
        modelBuilder.Entity<User>()
            .HasKey(u => u.Id);
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();
        modelBuilder.Entity<User>()
            .Property(u => u.DisplayName)
            .HasMaxLength(32);
        modelBuilder.Entity<User>()
            .Ignore(u => u.EffectiveDisplayName);  // Computed property, not stored

        // Community configuration
        modelBuilder.Entity<Community>()
            .HasKey(c => c.Id);
        modelBuilder.Entity<Community>()
            .HasOne(c => c.Owner)
            .WithMany(u => u.OwnedCommunities)
            .HasForeignKey(c => c.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        // Channel configuration
        modelBuilder.Entity<Channel>()
            .HasKey(c => c.Id);
        modelBuilder.Entity<Channel>()
            .HasOne(c => c.Community)
            .WithMany(com => com.Channels)
            .HasForeignKey(c => c.CommunityId)
            .OnDelete(DeleteBehavior.Cascade);

        // Message configuration
        modelBuilder.Entity<Message>()
            .HasKey(m => m.Id);
        modelBuilder.Entity<Message>()
            .HasOne(m => m.Author)
            .WithMany(u => u.Messages)
            .HasForeignKey(m => m.AuthorId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Message>()
            .HasOne(m => m.Channel)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Message>()
            .HasOne(m => m.ReplyTo)
            .WithMany()
            .HasForeignKey(m => m.ReplyToId)
            .OnDelete(DeleteBehavior.SetNull);
        // Thread parent relationship - cascade delete all thread messages when parent is deleted
        modelBuilder.Entity<Message>()
            .HasOne(m => m.ThreadParentMessage)
            .WithMany(m => m.ThreadReplies)
            .HasForeignKey(m => m.ThreadParentMessageId)
            .OnDelete(DeleteBehavior.Cascade);
        // Index for efficient thread queries
        modelBuilder.Entity<Message>()
            .HasIndex(m => m.ThreadParentMessageId);

        // DirectMessage configuration
        modelBuilder.Entity<DirectMessage>()
            .HasKey(dm => dm.Id);
        modelBuilder.Entity<DirectMessage>()
            .HasOne(dm => dm.Sender)
            .WithMany(u => u.SentMessages)
            .HasForeignKey(dm => dm.SenderId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DirectMessage>()
            .HasOne(dm => dm.Recipient)
            .WithMany(u => u.ReceivedMessages)
            .HasForeignKey(dm => dm.RecipientId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserCommunity configuration
        modelBuilder.Entity<UserCommunity>()
            .HasKey(uc => uc.Id);
        modelBuilder.Entity<UserCommunity>()
            .HasOne(uc => uc.User)
            .WithMany(u => u.UserCommunities)
            .HasForeignKey(uc => uc.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserCommunity>()
            .HasOne(uc => uc.Community)
            .WithMany(c => c.UserCommunities)
            .HasForeignKey(uc => uc.CommunityId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserCommunity>()
            .HasIndex(uc => new { uc.UserId, uc.CommunityId })
            .IsUnique();
        modelBuilder.Entity<UserCommunity>()
            .Property(uc => uc.DisplayNameOverride)
            .HasMaxLength(32);

        // VoiceParticipant configuration
        modelBuilder.Entity<VoiceParticipant>()
            .HasKey(vp => vp.Id);
        modelBuilder.Entity<VoiceParticipant>()
            .HasOne(vp => vp.User)
            .WithMany(u => u.VoiceParticipants)
            .HasForeignKey(vp => vp.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<VoiceParticipant>()
            .HasOne(vp => vp.Channel)
            .WithMany(c => c.VoiceParticipants)
            .HasForeignKey(vp => vp.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        // ServerInvite configuration
        modelBuilder.Entity<ServerInvite>()
            .HasKey(si => si.Id);
        modelBuilder.Entity<ServerInvite>()
            .HasIndex(si => si.Code)
            .IsUnique();
        modelBuilder.Entity<ServerInvite>()
            .HasOne(si => si.CreatedBy)
            .WithMany()
            .HasForeignKey(si => si.CreatedById)
            .OnDelete(DeleteBehavior.SetNull);

        // User.InvitedBy self-referential relationship
        modelBuilder.Entity<User>()
            .HasOne(u => u.InvitedBy)
            .WithMany()
            .HasForeignKey(u => u.InvitedById)
            .OnDelete(DeleteBehavior.SetNull);

        // ChannelReadState configuration
        modelBuilder.Entity<ChannelReadState>()
            .HasKey(crs => crs.Id);
        modelBuilder.Entity<ChannelReadState>()
            .HasOne(crs => crs.User)
            .WithMany(u => u.ChannelReadStates)
            .HasForeignKey(crs => crs.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ChannelReadState>()
            .HasOne(crs => crs.Channel)
            .WithMany(c => c.ChannelReadStates)
            .HasForeignKey(crs => crs.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ChannelReadState>()
            .HasOne(crs => crs.LastReadMessage)
            .WithMany()
            .HasForeignKey(crs => crs.LastReadMessageId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ChannelReadState>()
            .HasIndex(crs => new { crs.UserId, crs.ChannelId })
            .IsUnique();

        // MessageReaction configuration
        modelBuilder.Entity<MessageReaction>()
            .HasKey(mr => mr.Id);
        modelBuilder.Entity<MessageReaction>()
            .HasOne(mr => mr.Message)
            .WithMany(m => m.Reactions)
            .HasForeignKey(mr => mr.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MessageReaction>()
            .HasOne(mr => mr.User)
            .WithMany(u => u.Reactions)
            .HasForeignKey(mr => mr.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        // Ensure a user can only react with the same emoji once per message
        modelBuilder.Entity<MessageReaction>()
            .HasIndex(mr => new { mr.MessageId, mr.UserId, mr.Emoji })
            .IsUnique();

        // MessageAttachment configuration
        modelBuilder.Entity<MessageAttachment>()
            .HasKey(ma => ma.Id);
        modelBuilder.Entity<MessageAttachment>()
            .HasOne(ma => ma.Message)
            .WithMany(m => m.Attachments)
            .HasForeignKey(ma => ma.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MessageAttachment>()
            .HasIndex(ma => ma.MessageId);

        // CommunityInvite configuration
        modelBuilder.Entity<CommunityInvite>()
            .HasKey(ci => ci.Id);
        modelBuilder.Entity<CommunityInvite>()
            .HasOne(ci => ci.Community)
            .WithMany()
            .HasForeignKey(ci => ci.CommunityId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CommunityInvite>()
            .HasOne(ci => ci.InvitedUser)
            .WithMany()
            .HasForeignKey(ci => ci.InvitedUserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CommunityInvite>()
            .HasOne(ci => ci.InvitedBy)
            .WithMany()
            .HasForeignKey(ci => ci.InvitedById)
            .OnDelete(DeleteBehavior.Cascade);
        // Prevent duplicate pending invites for the same user to the same community
        modelBuilder.Entity<CommunityInvite>()
            .HasIndex(ci => new { ci.CommunityId, ci.InvitedUserId, ci.Status });

        // Notification configuration
        modelBuilder.Entity<Notification>()
            .HasKey(n => n.Id);
        modelBuilder.Entity<Notification>()
            .HasOne(n => n.Recipient)
            .WithMany()
            .HasForeignKey(n => n.RecipientId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Notification>()
            .HasOne(n => n.Actor)
            .WithMany()
            .HasForeignKey(n => n.ActorId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Notification>()
            .HasOne(n => n.Community)
            .WithMany()
            .HasForeignKey(n => n.CommunityId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Notification>()
            .HasOne(n => n.Channel)
            .WithMany()
            .HasForeignKey(n => n.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);
        // Index for efficient notification queries (main query pattern)
        modelBuilder.Entity<Notification>()
            .HasIndex(n => new { n.RecipientId, n.IsDismissed, n.CreatedAt })
            .HasDatabaseName("IX_Notifications_Recipient_NotDismissed_Date");
        // Index for unread count queries
        modelBuilder.Entity<Notification>()
            .HasIndex(n => new { n.RecipientId, n.IsRead, n.IsDismissed })
            .HasDatabaseName("IX_Notifications_Recipient_Unread");
    }
}
