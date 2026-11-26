namespace Snacka.Shared.Models;

public class Community
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required Guid OwnerId { get; set; }
    public User? Owner { get; set; }
    public string? Icon { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Channel> Channels { get; set; } = new List<Channel>();
    public ICollection<UserCommunity> UserCommunities { get; set; } = new List<UserCommunity>();
}
