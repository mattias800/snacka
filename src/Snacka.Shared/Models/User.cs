using System.ComponentModel.DataAnnotations;

namespace Snacka.Shared.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Username { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }

    /// <summary>
    /// Custom display name shown instead of username. Supports UTF-8 including emojis.
    /// </summary>
    [MaxLength(32)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The effective display name to show (DisplayName if set, otherwise Username).
    /// </summary>
    public string EffectiveDisplayName => DisplayName ?? Username;

    /// <summary>
    /// Stored filename (GUID-based) for the user's avatar image.
    /// </summary>
    public string? AvatarFileName { get; set; }

    public string? Status { get; set; }
    public bool IsOnline { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this user is a server administrator (can manage invites, users, etc.)
    /// </summary>
    public bool IsServerAdmin { get; set; } = false;

    /// <summary>
    /// Whether the user's email has been verified. Reserved for future use.
    /// </summary>
    public bool EmailVerified { get; set; } = false;

    /// <summary>
    /// The user who invited this user to the server.
    /// </summary>
    public Guid? InvitedById { get; set; }
    public User? InvitedBy { get; set; }

    public ICollection<Community> OwnedCommunities { get; set; } = new List<Community>();
    public ICollection<UserCommunity> UserCommunities { get; set; } = new List<UserCommunity>();
    public ICollection<DirectMessage> DirectMessages { get; set; } = new List<DirectMessage>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<VoiceParticipant> VoiceParticipants { get; set; } = new List<VoiceParticipant>();
    public ICollection<ChannelReadState> ChannelReadStates { get; set; } = new List<ChannelReadState>();
    public ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();
    public ICollection<ConversationParticipant> ConversationParticipants { get; set; } = new List<ConversationParticipant>();
    public ICollection<ConversationReadState> ConversationReadStates { get; set; } = new List<ConversationReadState>();
}
