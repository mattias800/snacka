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
    public DbSet<GamingStation> GamingStations => Set<GamingStation>();
    public DbSet<StationAccessGrant> StationAccessGrants => Set<StationAccessGrant>();
    public DbSet<StationSession> StationSessions => Set<StationSession>();
    public DbSet<StationSessionUser> StationSessionUsers => Set<StationSessionUser>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<ConversationReadState> ConversationReadStates => Set<ConversationReadState>();

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
            .WithMany(u => u.DirectMessages)
            .HasForeignKey(dm => dm.SenderId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DirectMessage>()
            .HasOne(dm => dm.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(dm => dm.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DirectMessage>()
            .HasIndex(dm => dm.ConversationId);

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

        // GamingStation configuration
        modelBuilder.Entity<GamingStation>()
            .HasKey(gs => gs.Id);
        modelBuilder.Entity<GamingStation>()
            .HasOne(gs => gs.Owner)
            .WithMany()
            .HasForeignKey(gs => gs.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GamingStation>()
            .HasIndex(gs => gs.OwnerId);
        modelBuilder.Entity<GamingStation>()
            .HasIndex(gs => gs.ConnectionId);
        modelBuilder.Entity<GamingStation>()
            .HasIndex(gs => new { gs.OwnerId, gs.MachineId })
            .IsUnique();

        // StationAccessGrant configuration
        modelBuilder.Entity<StationAccessGrant>()
            .HasKey(sag => sag.Id);
        modelBuilder.Entity<StationAccessGrant>()
            .HasOne(sag => sag.Station)
            .WithMany(gs => gs.AccessGrants)
            .HasForeignKey(sag => sag.StationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StationAccessGrant>()
            .HasOne(sag => sag.User)
            .WithMany()
            .HasForeignKey(sag => sag.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StationAccessGrant>()
            .HasOne(sag => sag.GrantedBy)
            .WithMany()
            .HasForeignKey(sag => sag.GrantedById)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<StationAccessGrant>()
            .HasIndex(sag => new { sag.StationId, sag.UserId })
            .IsUnique();

        // StationSession configuration
        modelBuilder.Entity<StationSession>()
            .HasKey(ss => ss.Id);
        modelBuilder.Entity<StationSession>()
            .HasOne(ss => ss.Station)
            .WithMany(gs => gs.Sessions)
            .HasForeignKey(ss => ss.StationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StationSession>()
            .HasIndex(ss => new { ss.StationId, ss.EndedAt });

        // StationSessionUser configuration
        modelBuilder.Entity<StationSessionUser>()
            .HasKey(ssu => ssu.Id);
        modelBuilder.Entity<StationSessionUser>()
            .HasOne(ssu => ssu.Session)
            .WithMany(ss => ss.ConnectedUsers)
            .HasForeignKey(ssu => ssu.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StationSessionUser>()
            .HasOne(ssu => ssu.User)
            .WithMany()
            .HasForeignKey(ssu => ssu.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StationSessionUser>()
            .HasIndex(ssu => ssu.ConnectionId);
        modelBuilder.Entity<StationSessionUser>()
            .HasIndex(ssu => new { ssu.SessionId, ssu.UserId })
            .IsUnique();

        // Conversation configuration
        modelBuilder.Entity<Conversation>()
            .HasKey(c => c.Id);
        modelBuilder.Entity<Conversation>()
            .HasOne(c => c.CreatedBy)
            .WithMany()
            .HasForeignKey(c => c.CreatedById)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Conversation>()
            .HasIndex(c => c.CreatedAt);

        // ConversationParticipant configuration
        modelBuilder.Entity<ConversationParticipant>()
            .HasKey(cp => cp.Id);
        modelBuilder.Entity<ConversationParticipant>()
            .HasOne(cp => cp.Conversation)
            .WithMany(c => c.Participants)
            .HasForeignKey(cp => cp.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ConversationParticipant>()
            .HasOne(cp => cp.User)
            .WithMany(u => u.ConversationParticipants)
            .HasForeignKey(cp => cp.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ConversationParticipant>()
            .HasOne(cp => cp.AddedBy)
            .WithMany()
            .HasForeignKey(cp => cp.AddedById)
            .OnDelete(DeleteBehavior.SetNull);
        // Ensure a user can only be in a conversation once
        modelBuilder.Entity<ConversationParticipant>()
            .HasIndex(cp => new { cp.ConversationId, cp.UserId })
            .IsUnique();

        // ConversationReadState configuration
        modelBuilder.Entity<ConversationReadState>()
            .HasKey(crs => crs.Id);
        modelBuilder.Entity<ConversationReadState>()
            .HasOne(crs => crs.Conversation)
            .WithMany(c => c.ReadStates)
            .HasForeignKey(crs => crs.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ConversationReadState>()
            .HasOne(crs => crs.User)
            .WithMany(u => u.ConversationReadStates)
            .HasForeignKey(crs => crs.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ConversationReadState>()
            .HasOne(crs => crs.LastReadMessage)
            .WithMany()
            .HasForeignKey(crs => crs.LastReadMessageId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ConversationReadState>()
            .HasIndex(crs => new { crs.ConversationId, crs.UserId })
            .IsUnique();
    }
}
