using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Miscord.Server.Data;
using Miscord.Server.DTOs;
using Miscord.Server.Hubs;
using Miscord.Server.Services;
using Miscord.Shared.Models;

namespace Miscord.Server.Controllers;

/// <summary>
/// Controller for handling message attachments.
/// </summary>
[ApiController]
[Route("api/channels/{channelId:guid}/messages")]
[Authorize]
public class AttachmentsController : ControllerBase
{
    private readonly MiscordDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IHubContext<MiscordHub> _hubContext;
    private readonly FileStorageSettings _settings;
    private readonly ILogger<AttachmentsController> _logger;

    public AttachmentsController(
        MiscordDbContext db,
        IFileStorageService fileStorage,
        IHubContext<MiscordHub> hubContext,
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

        // Create message
        var message = new Message
        {
            Content = content ?? string.Empty,
            AuthorId = userId.Value,
            ChannelId = channelId,
            ReplyToId = replyToId
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
            null, // ReplyTo preview (simplified for now)
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
public class AttachmentFilesController : ControllerBase
{
    private readonly IFileStorageService _fileStorage;

    public AttachmentFilesController(IFileStorageService fileStorage)
    {
        _fileStorage = fileStorage;
    }

    /// <summary>
    /// Download or view an attachment file.
    /// </summary>
    [HttpGet("{storedFileName}")]
    public async Task<IActionResult> GetFile(string storedFileName, CancellationToken ct)
    {
        var result = await _fileStorage.GetFileAsync(storedFileName, ct);

        if (result is null)
            return NotFound();

        return File(result.Stream, result.ContentType);
    }
}
