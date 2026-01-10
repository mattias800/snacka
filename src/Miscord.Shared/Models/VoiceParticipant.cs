namespace Miscord.Shared.Models;

public class VoiceParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid UserId { get; set; }
    public User? User { get; set; }
    public required Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }
    public bool IsMuted { get; set; }
    public bool IsDeafened { get; set; }
    public bool IsServerMuted { get; set; }     // Admin muted - user cannot unmute themselves
    public bool IsServerDeafened { get; set; }  // Admin deafened - user cannot undeafen themselves
    public bool IsScreenSharing { get; set; }
    public bool ScreenShareHasAudio { get; set; }  // Whether the screen share includes audio
    public bool IsCameraOn { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
