using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Controllers;

/// <summary>
/// Controller for handling message attachments.
/// </summary>
[ApiController]
[Route("api/channels/{channelId:guid}/messages")]
[Authorize]
public class AttachmentsController : ControllerBase
{
    private readonly SnackaDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IHubContext<SnackaHub> _hubContext;
    private readonly FileStorageSettings _settings;
    private readonly ILogger<AttachmentsController> _logger;

    public AttachmentsController(
        SnackaDbContext db,
        IFileStorageService fileStorage,
        IHubContext<SnackaHub> hubContext,
        IOptions<FileStorageSettings> settings,
        ILogger<AttachmentsController> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _hubContext = hubContext;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Send a message with file attachments.
    /// </summary>
    [HttpPost("with-attachments")]
    [RequestSizeLimit(100_000_000)] // 100MB total limit
    public async Task<ActionResult<MessageResponse>> SendMessageWithAttachments(
        Guid channelId,
        [FromForm] string? content,
        [FromForm] Guid? replyToId,
        [FromForm] List<IFormFile> files,
        [FromServices] IChannelActivityService activity,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        // Validate file count
        if (files.Count == 0)
            return BadRequest(new { error = "At least one file is required" });

        if (files.Count > _settings.MaxFilesPerMessage)
            return BadRequest(new { error = $"Maximum {_settings.MaxFilesPerMessage} files allowed per message" });

        // Validate all files first
        foreach (var file in files)
        {
            var validation = _fileStorage.ValidateFile(file.FileName, file.Length, file.ContentType);
            if (!validation.IsValid)
                return BadRequest(new { error = $"File '{file.FileName}': {validation.ErrorMessage}" });
        }

        // Verify channel exists and user has access
        var channel = await _db.Channels
            .Include(c => c.Community)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        if (channel is null)
            return NotFound(new { error = "Channel not found" });

        // Check if user is a member of the community
        var isMember = await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId && uc.CommunityId == channel.CommunityId, ct);

        if (!isMember)
            return Forbid();

        var author = await _db.Users.FindAsync([userId], ct);
        if (author is null)
            return Unauthorized();

        // Only messages in this channel can be replied to (the reply shows a preview of it).
        var replyTo = replyToId is { } rid
            ? await _db.Messages.Include(m => m.Author).FirstOrDefaultAsync(m => m.Id == rid && m.ChannelId == channelId, ct)
            : null;

        // Create message
        var message = new Message
        {
            Content = content ?? string.Empty,
            AuthorId = userId.Value,
            ChannelId = channelId,
            ReplyToId = replyTo?.Id
        };

        _db.Messages.Add(message);

        // Store files and create attachment records
        var attachments = new List<MessageAttachment>();
        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            var result = await _fileStorage.StoreFileAsync(stream, file.FileName, file.ContentType, ct);

            var attachment = new MessageAttachment
            {
                MessageId = message.Id,
                FileName = result.SanitizedFileName,
                StoredFileName = result.StoredFileName,
                ContentType = file.ContentType,
                FileSize = file.Length,
                IsImage = result.IsImage,
                IsAudio = result.IsAudio
            };

            attachments.Add(attachment);
            _db.MessageAttachments.Add(attachment);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} sent message with {AttachmentCount} attachments to channel {ChannelId}",
            userId, attachments.Count, channelId);

        // Build response
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var response = new MessageResponse(
            message.Id,
            message.Content,
            message.AuthorId,
            author.Username,
            author.EffectiveDisplayName,
            author.AvatarFileName,
            message.ChannelId,
            message.CreatedAt,
            message.UpdatedAt,
            message.IsEdited,
            message.ReplyToId,
            replyTo is null ? null : new ReplyPreview(
                replyTo.Id,
                replyTo.Content,
                replyTo.AuthorId,
                replyTo.Author?.Username ?? "Unknown",
                replyTo.Author?.EffectiveDisplayName ?? replyTo.Author?.Username ?? "Unknown"),
            null, // Reactions
            message.IsPinned,
            message.PinnedAt,
            null, // PinnedByUsername
            null, // PinnedByEffectiveDisplayName
            attachments.Select(a => new AttachmentResponse(
                a.Id,
                a.FileName,
                a.ContentType,
                a.FileSize,
                a.IsImage,
                a.IsAudio,
                $"{baseUrl}/api/attachments/{a.StoredFileName}"
            )).ToList()
        );

        // Broadcast via SignalR
        await _hubContext.Clients.Group($"channel:{channelId}")
            .SendAsync("ReceiveChannelMessage", response, ct);
        await activity.MessagePostedAsync(response, ct);

        return Ok(response);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

/// <summary>
/// Controller for serving attachment files.
/// </summary>
[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentFilesController : ControllerBase
{
    private readonly SnackaDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<AttachmentFilesController> _logger;

    public AttachmentFilesController(
        SnackaDbContext db,
        IFileStorageService fileStorage,
        ILogger<AttachmentFilesController> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    /// <summary>
    /// Download or view an attachment file.
    /// Authentication via query string (?access_token=xxx) is supported for img tags.
    /// User must be a member of the community where the attachment was uploaded.
    /// </summary>
    [HttpGet("{storedFileName}")]
    public async Task<IActionResult> GetFile(string storedFileName, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        // Validate filename format to prevent path traversal
        if (string.IsNullOrEmpty(storedFileName) ||
            storedFileName.Contains("..") ||
            storedFileName.Contains('/') ||
            storedFileName.Contains('\\'))
        {
            return BadRequest();
        }

        // Find the attachment and verify the user has access
        var attachment = await _db.MessageAttachments
            .Include(a => a.Message)
            .ThenInclude(m => m.Channel)
            .FirstOrDefaultAsync(a => a.StoredFileName == storedFileName, ct);

        if (attachment?.Message?.Channel is null)
            return NotFound();

        // Check if user is a member of the community containing this attachment
        var isMember = await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId &&
                           uc.CommunityId == attachment.Message.Channel.CommunityId, ct);

        if (!isMember)
        {
            _logger.LogWarning("User {UserId} attempted to access attachment {FileName} without community membership",
                userId, storedFileName);
            return Forbid();
        }

        var result = await _fileStorage.GetFileAsync(storedFileName, ct);

        if (result is null)
            return NotFound();

        return File(result.Stream, result.ContentType);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
