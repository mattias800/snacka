using System.ComponentModel.DataAnnotations;

namespace Snacka.Server.DTOs;

/// <summary>
/// Full conversation details including participants.
/// </summary>
public record ConversationResponse(
    Guid Id,
    string? Name,
    string? IconFileName,
    bool IsGroup,
    DateTime CreatedAt,
    List<ParticipantInfo> Participants,
    ConversationMessageResponse? LastMessage,
    int UnreadCount
);

/// <summary>
/// Summary of a conversation for list views.
/// </summary>
public record ConversationSummaryResponse(
    Guid Id,
    string DisplayName,
    string? IconFileName,
    bool IsGroup,
    bool IsOnline,
    ConversationMessageResponse? LastMessage,
    int UnreadCount
);

public record ParticipantInfo(
    Guid UserId,
    string Username,
    string EffectiveDisplayName,
    string? Avatar,
    bool IsOnline,
    DateTime JoinedAt
);

public record CreateConversationRequest(
    [Required, MinLength(1)] List<Guid> ParticipantIds,
    string? Name
);

public record AddParticipantRequest([Required] Guid UserId);

public record UpdateConversationRequest(string? Name, string? IconFileName);

public record SendConversationMessageRequest(
    [Required, StringLength(2000, MinimumLength = 1)] string Content
);

public record ConversationMessageResponse(
    Guid Id,
    Guid ConversationId,
    string Content,
    Guid SenderId,
    string SenderUsername,
    string SenderEffectiveDisplayName,
    string? SenderAvatar,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);
