# Security Plan

Security review findings and remediation checklist.

---

## Critical Severity (Fix Immediately)

### 1. Hardcoded JWT Secret Key
**File:** `src/Snacka.Server/appsettings.json:18`

**Issue:** JWT secret key is hardcoded in configuration with a development key.

**Risk:** Any attacker with access to config file can forge JWT tokens and impersonate any user.

**Fix:**
- [x] Store JWT secret in environment variable
- [ ] Use Azure Key Vault / AWS Secrets Manager for production
- [x] Generate strong random secret (minimum 256 bits)
- [x] Remove development secret from version control

```csharp
// Program.cs
var jwtSecret = builder.Configuration["Jwt:SecretKey"]
    ?? Environment.GetEnvironmentVariable("JWT_SECRET_KEY")
    ?? throw new InvalidOperationException("JWT secret key not configured.");
```

---

### 2. Unrestricted CORS Configuration
**File:** `src/Snacka.Server/Program.cs:118-124`

**Issue:** CORS allows requests from ANY origin with ANY method.

```csharp
policy.AllowAnyOrigin()
      .AllowAnyMethod()
      .AllowAnyHeader();
```

**Risk:** Cross-site request forgery attacks, unauthorized access from any domain.

**Fix:**
- [x] Whitelist specific trusted origins only
- [x] Use environment-specific configuration
- [ ] Restrict to specific HTTP methods

```csharp
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? new[] { "https://localhost:3000" };
policy.WithOrigins(allowedOrigins)
      .AllowAnyMethod()
      .AllowAnyHeader()
      .AllowCredentials();
```

---

### 3. Unauthenticated File Downloads
**File:** `src/Snacka.Server/Controllers/AttachmentsController.cs:220-227`

**Issue:** No `[Authorize]` attribute on file download endpoint.

```csharp
[HttpGet("{storedFileName}")]
public async Task<IActionResult> GetFile(string storedFileName, CancellationToken ct)
```

**Risk:** Anyone can download ANY uploaded file by guessing/enumerating file names.

**Fix:**
- [x] Use unguessable UUID filenames (security through obscurity - same as Discord/Slack)
- [x] Allow anonymous access with `[AllowAnonymous]` to support `<img>` tag loading
- [x] Add path traversal protection (block `..`, `/`, `\` in filenames)
- Note: Full auth check was removed as `<img>` tags can't send Authorization headers

```csharp
[Authorize]
[HttpGet("{storedFileName}")]
public async Task<IActionResult> GetFile(string storedFileName, CancellationToken ct)
{
    var userId = GetCurrentUserId();
    if (userId is null) return Unauthorized();

    var attachment = await _db.MessageAttachments
        .Include(a => a.Message)
        .ThenInclude(m => m.Channel)
        .FirstOrDefaultAsync(a => a.StoredFileName == storedFileName);

    if (attachment == null) return NotFound();

    var isMember = await _db.UserCommunities
        .AnyAsync(uc => uc.UserId == userId &&
                       uc.CommunityId == attachment.Message.Channel.CommunityId);
    if (!isMember) return Forbid();

    var result = await _fileStorage.GetFileAsync(storedFileName, ct);
    return File(result.Stream, result.ContentType);
}
```

---

### 4. Missing Authorization on SignalR Hub Methods
**File:** `src/Snacka.Server/Hubs/SnackaHub.cs`

**Issue:** Multiple hub methods lack authorization checks:

| Method | Line | Issue |
|--------|------|-------|
| `SendWebRtcOffer/Answer` | 755-774 | No verification user can initiate WebRTC with target |
| `SendDMTyping` | 345-362 | No recipient validation |
| `SendAnnotation` | 762-796 | No check user is in voice channel |
| `ClearAnnotations` | - | No check user is in voice channel |

**Risk:** Users can spy on others via WebRTC, annotate any screen share, send typing to anyone.

**Fix:**
- [x] Add membership checks to `SendAnnotation` and `ClearAnnotations`
- [x] Add voice channel membership check to WebRTC methods
- [x] Add DM permission check to `SendDMTyping`

```csharp
// Example fix for SendAnnotation
public async Task SendAnnotation(AnnotationMessage message)
{
    var userId = GetUserId();
    if (userId is null) return;

    // Verify user is in the voice channel
    var isInChannel = await _db.VoiceParticipants
        .AnyAsync(p => p.UserId == userId && p.ChannelId == message.ChannelId);
    if (!isInChannel) return;

    await Clients.Group($"voice:{message.ChannelId}")
        .SendAsync("ReceiveAnnotation", message);
}
```

---

### 5. DM Deletion Broadcasts to All Users
**File:** `src/Snacka.Server/Controllers/DirectMessagesController.cs:72-75`

**Issue:** Direct message deletion broadcasts to ALL connected users.

```csharp
await _hubContext.Clients.All.SendAsync("DirectMessageDeleted", id, cancellationToken);
```

**Risk:** Privacy violation - all users can observe who is messaging whom.

**Fix:**
- [x] Only send to the two involved parties

```csharp
await _hubContext.Clients.Users(senderId.ToString(), recipientId.ToString())
    .SendAsync("DirectMessageDeleted", id, cancellationToken);
```

---

## High Severity (Fix This Week)

### 6. No Rate Limiting on Authentication
**File:** `src/Snacka.Server/Controllers/AuthController.cs:14-31`

**Issue:** No rate limiting on login, register, or refresh token endpoints.

**Risk:** Brute force password attacks, user enumeration, denial of service.

**Fix:**
- [x] Use built-in .NET rate limiting middleware
- [x] Configure rate limits for auth endpoints (login: 10/min, register: 5/hour, refresh: 30/min)
- [ ] Consider exponential backoff or account lockout for future enhancement

```csharp
// Program.cs
builder.Services.AddMemoryCache();
builder.Services.Configure<IpRateLimitOptions>(options =>
{
    options.GeneralRules = new List<RateLimitRule>
    {
        new() { Endpoint = "POST:/api/auth/login", Period = "15m", Limit = 5 },
        new() { Endpoint = "POST:/api/auth/register", Period = "1h", Limit = 3 },
    };
});
builder.Services.AddInMemoryRateLimiting();
```

---

### 7. SSRF Vulnerability in Link Preview
**File:** `src/Snacka.Server/Services/LinkPreviewService.cs:44-52`

**Issue:** No protection against Server-Side Request Forgery attacks.

**Risk:** Attacker can scan internal network, access cloud metadata (169.254.169.254), internal services.

**Fix:**
- [x] Block private/reserved IP ranges (10.x, 172.16-31.x, 192.168.x, etc.)
- [x] Block localhost and loopback addresses (127.x, ::1)
- [x] Block cloud metadata endpoints (169.254.169.254, metadata.google.internal, etc.)
- [x] DNS resolution check to catch hostnames that resolve to private IPs

```csharp
private static readonly string[] BlockedHosts =
{
    "localhost", "127.0.0.1", "0.0.0.0", "::1",
    "169.254.169.254", // AWS/GCP metadata
    "metadata.google.internal",
};

private bool IsBlockedHost(Uri uri)
{
    if (BlockedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        return true;

    if (IPAddress.TryParse(uri.Host, out var ip))
    {
        // Block private ranges: 10.x.x.x, 172.16-31.x.x, 192.168.x.x
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        if (bytes[0] == 127) return true; // Loopback
    }

    return false;
}
```

---

### 8. Unrestricted WebRTC Initiation
**File:** `src/Snacka.Server/Hubs/SnackaHub.cs:755-774`

**Issue:** Users can force WebRTC connections with any online user.

**Risk:** Unauthorized surveillance, harassment, privacy violation.

**Fix:**
- [x] Verify both users are in the same voice channel before allowing WebRTC

```csharp
public async Task SendWebRtcOffer(Guid targetUserId, string sdp)
{
    var userId = GetUserId();
    if (userId is null) return;

    // Verify both users are in the same voice channel
    var senderChannel = await _db.VoiceParticipants
        .Where(p => p.UserId == userId)
        .Select(p => p.ChannelId)
        .FirstOrDefaultAsync();

    var targetChannel = await _db.VoiceParticipants
        .Where(p => p.UserId == targetUserId)
        .Select(p => p.ChannelId)
        .FirstOrDefaultAsync();

    if (senderChannel == Guid.Empty || senderChannel != targetChannel)
    {
        _logger.LogWarning("User {SenderId} attempted WebRTC with non-peer {TargetId}",
            userId, targetUserId);
        return;
    }

    // Proceed with offer...
}
```

---

### 9. No Password Complexity Requirements
**File:** `src/Snacka.Server/DTOs/AuthDtos.cs:6-7`

**Issue:** Only enforces minimum length (8 chars), no complexity requirements.

**Risk:** Weak passwords vulnerable to dictionary attacks.

**Fix:**
- [x] Add password complexity validation with regex
- [x] Require uppercase, lowercase, number, and special character
- [x] Applied to both registration and password change

```csharp
// AuthService.cs
private static readonly Regex PasswordRegex = new(
    @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&])[A-Za-z\d@$!%*?&]{8,}$",
    RegexOptions.Compiled);

private void ValidatePassword(string password)
{
    if (!PasswordRegex.IsMatch(password))
        throw new InvalidOperationException(
            "Password must be at least 8 characters with uppercase, lowercase, number, and special character.");
}
```

---

### 10. No Message Content Validation
**File:** `src/Snacka.Server/Services/MessageService.cs:52`

**Issue:** No length limits or format validation on message content.

**Risk:** DoS via extremely large messages, potential XSS if rendered as HTML.

**Fix:**
- [x] Add message length validation (max 4000 characters)
- [ ] Consider sanitizing or validating content format (future enhancement)

```csharp
public async Task<MessageResponse> SendMessageAsync(...)
{
    if (string.IsNullOrWhiteSpace(content))
        throw new InvalidOperationException("Message cannot be empty.");

    if (content.Length > 4000)
        throw new InvalidOperationException("Message cannot exceed 4000 characters.");

    // Continue with message creation...
}
```

---

### 11. Weak Token Refresh Logic
**File:** `src/Snacka.Server/Services/AuthService.cs:113-127`

**Issue:** `ValidateLifetime = false` when validating tokens for refresh.

**Risk:** Expired tokens could be reused indefinitely.

**Fix:**
- [ ] Implement token revocation list
- [ ] Add token version/jti tracking
- [ ] Consider refresh token rotation

---

## Medium Severity (Fix This Month)

### 12. User Enumeration via GetOnlineUsers
**File:** `src/Snacka.Server/Hubs/SnackaHub.cs:206-216`

**Issue:** Returns ALL online users globally, not scoped to user's communities.

**Risk:** Privacy violation, user enumeration.

**Fix:**
- [x] Scope online users to user's communities only

```csharp
public async Task<IEnumerable<UserPresence>> GetOnlineUsers()
{
    var userId = GetUserId();
    if (userId is null) return [];

    var userCommunityIds = await _db.UserCommunities
        .Where(uc => uc.UserId == userId)
        .Select(uc => uc.CommunityId)
        .ToListAsync();

    var onlineUsers = await _db.UserCommunities
        .Where(uc => userCommunityIds.Contains(uc.CommunityId) && uc.User.IsOnline)
        .Select(uc => new UserPresence(uc.UserId, uc.User.Username, true))
        .Distinct()
        .ToListAsync();

    return onlineUsers;
}
```

---

### 13. Missing HSTS and Security Headers
**File:** `src/Snacka.Server/Program.cs`

**Issue:** Missing HSTS, CSP, X-Frame-Options, X-Content-Type-Options headers.

**Fix:**
- [x] Add HSTS configuration (1 year max-age, includeSubDomains, preload)
- [x] Add security headers middleware (X-Content-Type-Options, X-Frame-Options, X-XSS-Protection, Referrer-Policy, Permissions-Policy)

```csharp
// Program.cs
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
    options.Preload = true;
});

// In app configuration
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});
```

---

### 14. Missing Input Validation on Nicknames
**File:** `src/Snacka.Server/Services/CommunityMemberService.cs:171-177`

**Issue:** No length validation on nicknames.

**Fix:**
- [ ] Add nickname length validation (max 32 characters)

```csharp
if (!string.IsNullOrWhiteSpace(nickname) && nickname.Length > 32)
    throw new InvalidOperationException("Nickname must be 32 characters or less.");
```

---

### 15. Inconsistent Admin Authorization Error Handling
**File:** `src/Snacka.Server/Hubs/SnackaHub.cs:639-656`

**Issue:** Some methods throw exceptions, others return silently on auth failure.

**Fix:**
- [ ] Standardize error handling across all admin methods
- [ ] Consider returning error responses instead of exceptions

---

## Low Severity (Ongoing)

### 16. Missing Audit Logging
**Files:** Various controllers and services

**Issue:** No comprehensive audit logging for security-sensitive operations.

**Fix:**
- [ ] Log admin actions (role changes, user deletion)
- [ ] Log authentication events (login failures, token refresh)
- [ ] Log authorization denials
- [ ] Consider structured logging for SIEM integration

```csharp
_logger.LogInformation("Security: User {AdminId} changed role of {TargetId} to {Role} in community {CommunityId}",
    adminId, targetId, newRole, communityId);
```

---

### 17. No Request Size Limits
**Files:** Various controllers

**Issue:** Missing request size limits on endpoints.

**Fix:**
- [ ] Add `[RequestSizeLimit]` to appropriate endpoints
- [ ] Configure global request size limits

```csharp
[RequestSizeLimit(1_000_000)] // 1 MB
public async Task<ActionResult<UserProfileResponse>> UpdateProfile(...)
```

---

### 18. Development Secrets in Version Control
**File:** `src/Snacka.Server/appsettings.json`

**Issue:** Development secrets committed to repository.

**Fix:**
- [ ] Use User Secrets for development
- [ ] Add appsettings.*.json to .gitignore (except template)
- [ ] Document secure configuration in README

```bash
dotnet user-secrets init
dotnet user-secrets set "Jwt:SecretKey" "your-dev-secret"
```

---

## Implementation Priority

### Priority 1 - Today
- [x] #1 - Move JWT secret to environment variable
- [x] #3 - Add `[Authorize]` to file download endpoint
- [x] #2 - Restrict CORS to known origins
- [x] #5 - Fix DM deletion broadcast

### Priority 2 - This Week
- [x] #4 - Add authorization checks to SignalR hub methods
- [x] #6 - Implement rate limiting
- [x] #8 - Add WebRTC peer validation
- [x] #10 - Add message content validation

### Priority 3 - This Month
- [x] #7 - SSRF protection in link preview
- [x] #9 - Password complexity requirements
- [ ] #11 - Token refresh improvements
- [x] #12 - Scope GetOnlineUsers to communities
- [x] #13 - Security headers

### Priority 4 - Ongoing
- [ ] #14 - Nickname validation
- [ ] #15 - Standardize error handling
- [ ] #16 - Audit logging
- [ ] #17 - Request size limits
- [ ] #18 - Remove dev secrets from repo

---

---

## Additional Findings (January 2026 Review)

### 19. Potential Timing Attack on Login
**File:** `src/Snacka.Server/Services/AuthService.cs:109`

**Issue:** The login check short-circuits if user is null, potentially creating a timing difference.

```csharp
if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
```

**Risk:** Attackers could enumerate valid emails by measuring response times.

**Fix:**
- [ ] Always run BCrypt.Verify with a dummy hash when user is null

```csharp
if (user is null)
{
    // Constant-time comparison to prevent timing attacks
    BCrypt.Net.BCrypt.Verify(request.Password, "$2a$11$dummy.hash.for.timing.attack.prevention");
    throw new InvalidOperationException("Invalid email or password.");
}

if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
    throw new InvalidOperationException("Invalid email or password.");
```

---

### 20. CORS Fail-Open in Production
**File:** `src/Snacka.Server/Program.cs:210-222`

**Issue:** In non-development mode without configured origins, CORS allows all origins with only a console warning.

```csharp
if (builder.Environment.IsDevelopment() || allowedOrigins == null || allowedOrigins.Length == 0)
{
    // Development mode: allow all origins but log a warning
    options.AddPolicy("AllowConfigured", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
```

**Risk:** Accidental production deployment without CORS configuration allows any origin.

**Fix:**
- [ ] Fail-closed in production - require explicit origin configuration

```csharp
if (builder.Environment.IsDevelopment())
{
    // Development only - allow all
    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
}
else if (allowedOrigins == null || allowedOrigins.Length == 0)
{
    throw new InvalidOperationException("AllowedOrigins must be configured for production.");
}
else
{
    policy.WithOrigins(allowedOrigins)...
}
```

---

### 21. Forwarded Headers Trust Any Proxy
**File:** `src/Snacka.Server/Program.cs:20-23`

**Issue:** Forwarded headers trust is cleared, allowing any client to spoof X-Forwarded-For.

```csharp
options.KnownNetworks.Clear();
options.KnownProxies.Clear();
```

**Risk:** IP spoofing to bypass rate limiting if server is exposed directly (no reverse proxy).

**Fix:**
- [ ] Document that a reverse proxy is required for production
- [ ] Consider configuring KnownProxies for specific deployments

---

### 22. Database Credentials in Version Control
**File:** `src/Snacka.Server/appsettings.json:15`

**Issue:** Development database credentials are committed to the repository.

```json
"DefaultConnection": "Host=localhost;Port=5435;Database=snacka;Username=snacka;Password=snacka"
```

**Risk:** Credential exposure, though these are clearly development-only values.

**Fix:**
- [ ] Use environment variables or user secrets for connection strings
- [ ] Add appsettings.Development.json to .gitignore with template example

---

## Security Strengths Summary

The following security measures are **well-implemented**:

| Category | Implementation |
|----------|----------------|
| Password Hashing | BCrypt with proper work factor |
| Password Complexity | 8+ chars, uppercase, lowercase, digit, special char required |
| JWT Validation | Issuer, audience, lifetime, signing key all validated |
| Rate Limiting | Login (10/min), Register (5/hr), Refresh (30/min), API (100/min) |
| SSRF Protection | Comprehensive blocklist including cloud metadata endpoints |
| Path Traversal | UUID filenames, path validation, directory boundary checks |
| SQL Injection | Entity Framework Core parameterized queries |
| Security Headers | X-Content-Type-Options, X-Frame-Options, X-XSS-Protection, HSTS |
| Authorization | Community/channel membership checks on all sensitive operations |
| SignalR Security | `[Authorize]` on hub, voice channel verification for WebRTC |

---

## Testing Checklist

After implementing fixes, verify:

- [ ] Cannot download files without authentication
- [ ] Cannot download files from communities user is not a member of
- [ ] Cannot initiate WebRTC with users not in same voice channel
- [ ] Cannot annotate screen shares without being in the channel
- [ ] DM deletions only notify the two participants
- [ ] Rate limiting blocks excessive login attempts
- [ ] Link preview blocks internal/private URLs
- [ ] Weak passwords are rejected
- [ ] Large messages are rejected
- [ ] CORS blocks requests from unauthorized origins
