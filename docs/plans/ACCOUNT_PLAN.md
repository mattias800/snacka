# Plan: Server Invite System & User Account Management

## Status

All 10 phases have been implemented and committed.

## Summary

Implement a secure self-hosted server model where:
1. Registration always requires a valid invite code
2. First registered user becomes server admin
3. Server admin can create/manage invite codes
4. Users can edit their profile and change password

---

## Phase 1: Database Models [COMPLETED]

### New Models

**ServerInvite** (`src/Snacka.Shared/Models/ServerInvite.cs`)
```csharp
public class ServerInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }  // e.g., "abc123xyz"
    public Guid? CreatedById { get; set; }     // null for bootstrap invite
    public User? CreatedBy { get; set; }
    public int MaxUses { get; set; } = 0;      // 0 = unlimited
    public int CurrentUses { get; set; } = 0;
    public DateTime? ExpiresAt { get; set; }   // null = never expires
    public bool IsRevoked { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

**User Model Updates** (`src/Snacka.Shared/Models/User.cs`)
```csharp
// Add to existing User class:
public bool IsServerAdmin { get; set; } = false;
public bool EmailVerified { get; set; } = false;  // For future use
public Guid? InvitedById { get; set; }  // Who invited this user
public User? InvitedBy { get; set; }
```

### DbContext Updates

**File:** `src/Snacka.Server/Data/SnackaDbContext.cs`

- Add `DbSet<ServerInvite> ServerInvites`
- Configure ServerInvite entity with unique index on Code
- Configure User.InvitedBy relationship

---

## Phase 2: Server Invite Service [COMPLETED]

**New File:** `src/Snacka.Server/Services/IServerInviteService.cs`
```csharp
public interface IServerInviteService
{
    Task<ServerInvite> CreateInviteAsync(Guid? creatorId, int maxUses = 0, DateTime? expiresAt = null);
    Task<ServerInvite?> ValidateInviteCodeAsync(string code);
    Task UseInviteAsync(string code);
    Task<IEnumerable<ServerInviteResponse>> GetAllInvitesAsync();
    Task RevokeInviteAsync(Guid inviteId, Guid requestingUserId);
    Task<bool> HasAnyUsersAsync();  // Check if server has any users
    Task<string> GetOrCreateBootstrapInviteAsync();  // For first-time setup
}
```

**New File:** `src/Snacka.Server/Services/ServerInviteService.cs`
- Implements the interface
- Code generation: 8 character alphanumeric (e.g., "a1b2c3d4")
- Validation checks: not revoked, not expired, uses not exceeded

---

## Phase 3: Auth Service Updates [COMPLETED]

**File:** `src/Snacka.Server/Services/AuthService.cs`

Update `RegisterAsync` to:
1. Require invite code in request
2. Validate invite code via IServerInviteService
3. If first user (no users exist), make them server admin
4. Mark invite as used (increment CurrentUses)
5. Link user to inviter (InvitedById)

**File:** `src/Snacka.Server/DTOs/AuthDtos.cs`

Update `RegisterRequest`:
```csharp
public record RegisterRequest(
    [Required, StringLength(50, MinimumLength = 3)] string Username,
    [Required, EmailAddress] string Email,
    [Required, StringLength(100, MinimumLength = 8)] string Password,
    [Required] string InviteCode  // NEW: always required
);
```

---

## Phase 4: Server Admin APIs [COMPLETED]

**New File:** `src/Snacka.Server/Controllers/AdminController.cs`

Endpoints (require server admin):
- `GET /api/admin/invites` - List all invites
- `POST /api/admin/invites` - Create new invite
- `DELETE /api/admin/invites/{id}` - Revoke invite
- `GET /api/admin/users` - List all users
- `PUT /api/admin/users/{id}/admin` - Toggle user admin status
- `DELETE /api/admin/users/{id}` - Delete user account

**New File:** `src/Snacka.Server/DTOs/AdminDtos.cs`
```csharp
public record CreateInviteRequest(int MaxUses = 0, DateTime? ExpiresAt = null);
public record ServerInviteResponse(Guid Id, string Code, int MaxUses, int CurrentUses,
    DateTime? ExpiresAt, bool IsRevoked, string? CreatedByUsername, DateTime CreatedAt);
public record AdminUserResponse(Guid Id, string Username, string Email, bool IsServerAdmin,
    bool IsOnline, DateTime CreatedAt, string? InvitedByUsername);
```

---

## Phase 5: Bootstrap Invite Endpoint [COMPLETED]

**File:** `src/Snacka.Server/Controllers/HealthController.cs`

New endpoint (no auth required):
- `GET /api/health/server-info` - Returns server name, has users, bootstrap invite (if no users)

```csharp
public record ServerInfoResponse(
    string ServerName,
    bool HasUsers,
    string? BootstrapInviteCode  // Only returned if no users exist
);
```

This allows the first user to get an invite code to register.

---

## Phase 6: Account Settings APIs [COMPLETED]

**File:** `src/Snacka.Server/Controllers/AuthController.cs`

New endpoints:
- `PUT /api/users/me/password` - Change password
- `DELETE /api/users/me` - Delete own account

**File:** `src/Snacka.Server/DTOs/AuthDtos.cs`
```csharp
public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, StringLength(100, MinimumLength = 8)] string NewPassword
);
```

Update existing `PUT /api/users/me` to allow updating:
- Username
- Avatar
- Status

---

## Phase 7: Client - API Client Updates [COMPLETED]

**File:** `src/Snacka.Client/Services/ApiClient.cs`

Add methods:
- `GetServerInfoAsync()` - Get server info for login screen
- `CreateInviteAsync(request)` - Admin: create invite
- `GetInvitesAsync()` - Admin: list invites
- `RevokeInviteAsync(id)` - Admin: revoke invite
- `GetAllUsersAsync()` - Admin: list users
- `ChangePasswordAsync(request)` - Change password
- `DeleteAccountAsync()` - Delete own account

---

## Phase 8: Client - Login/Register UI Updates [COMPLETED]

**File:** `src/Snacka.Client/ViewModels/RegisterViewModel.cs`

- Add `InviteCode` property
- On load, call `GetServerInfoAsync()` to check if server has users
- If no users, auto-fill the bootstrap invite code
- Show message: "You will be the server administrator"

**File:** `src/Snacka.Client/Views/RegisterView.axaml`

- Add invite code input field
- Show bootstrap message when applicable

**File:** `src/Snacka.Client/ViewModels/LoginViewModel.cs`

- On load, call `GetServerInfoAsync()`
- If no users exist, show "Set up server" message and switch to register

---

## Phase 9: Client - Account Settings UI [COMPLETED]

**File:** `src/Snacka.Client/ViewModels/SettingsViewModel.cs`

Enable "My Account" section:
- Edit username
- Edit status
- Change password (current + new + confirm)
- Delete account (with confirmation)

**File:** `src/Snacka.Client/Views/SettingsView.axaml`

- Implement account settings tab content
- Add password change form
- Add delete account button with confirmation dialog

---

## Phase 10: Client - Admin Panel [COMPLETED]

**New File:** `src/Snacka.Client/ViewModels/AdminPanelViewModel.cs`
**New File:** `src/Snacka.Client/Views/AdminPanelView.axaml`

Admin panel accessible from settings (if user is server admin):
- **Invites Tab:**
  - List all invites with code, uses, expiration
  - Create new invite button
  - Revoke invite button
  - Copy invite link button
- **Users Tab:**
  - List all users
  - Show who invited whom
  - Toggle admin status
  - Delete user

**File:** `src/Snacka.Client/ViewModels/MainAppViewModel.cs`
- Add `IsServerAdmin` property
- Load from user info on login

---

## Files Created

| File | Purpose |
|------|---------|
| `src/Snacka.Shared/Models/ServerInvite.cs` | ServerInvite model |
| `src/Snacka.Server/Services/IServerInviteService.cs` | Service interface |
| `src/Snacka.Server/Services/ServerInviteService.cs` | Service implementation |
| `src/Snacka.Server/Controllers/AdminController.cs` | Admin API endpoints |
| `src/Snacka.Server/DTOs/AdminDtos.cs` | Admin DTOs |
| `src/Snacka.Client/ViewModels/AdminPanelViewModel.cs` | Admin panel VM |
| `src/Snacka.Client/ViewModels/AccountSettingsViewModel.cs` | Account settings VM |
| `src/Snacka.Client/Views/AdminPanelView.axaml` | Admin panel view |
| `src/Snacka.Client/Views/AdminPanelView.axaml.cs` | Admin panel code-behind |
| `src/Snacka.Client/Views/AccountSettingsView.axaml` | Account settings view |
| `src/Snacka.Client/Views/AccountSettingsView.axaml.cs` | Account settings code-behind |
| `tests/Snacka.Server.Tests/Api/AdminApiTests.cs` | Admin API tests |

## Files Modified

| File | Changes |
|------|---------|
| `src/Snacka.Shared/Models/User.cs` | Add IsServerAdmin, EmailVerified, InvitedById |
| `src/Snacka.Server/Data/SnackaDbContext.cs` | Add ServerInvites DbSet, configure relationships |
| `src/Snacka.Server/Services/AuthService.cs` | Require invite code, first user = admin |
| `src/Snacka.Server/Services/IAuthService.cs` | Update interface |
| `src/Snacka.Server/DTOs/AuthDtos.cs` | Add InviteCode to RegisterRequest, new DTOs |
| `src/Snacka.Server/Controllers/AuthController.cs` | Add server-info, password change endpoints |
| `src/Snacka.Server/Controllers/HealthController.cs` | Add server-info endpoint |
| `src/Snacka.Server/Program.cs` | Register IServerInviteService |
| `src/Snacka.Client/Services/ApiClient.cs` | Add admin and account methods |
| `src/Snacka.Client/Services/ApiModels.cs` | Add new DTOs |
| `src/Snacka.Client/Services/IApiClient.cs` | Add new method signatures |
| `src/Snacka.Client/Services/ServerConnection.cs` | Update ServerInfoResponse |
| `src/Snacka.Client/ViewModels/RegisterViewModel.cs` | Add invite code handling |
| `src/Snacka.Client/Views/RegisterView.axaml` | Add invite code field |
| `src/Snacka.Client/ViewModels/SettingsViewModel.cs` | Add account settings and admin panel |
| `src/Snacka.Client/Views/SettingsView.axaml` | Add account settings and admin panel UI |
| `src/Snacka.Client/ViewModels/MainWindowViewModel.cs` | Pass IsServerAdmin to settings |
| `tests/Snacka.Server.Tests/Api/AuthApiTests.cs` | Update tests for invite system |
| `tests/Snacka.Server.Tests/IntegrationTestBase.cs` | Add invite code helpers |
| `tests/Snacka.Server.Tests/Services/AuthServiceTests.cs` | Update tests for invite system |

---

## User Flow

### First User (Server Setup)
1. Open client, connect to server
2. Client calls `GET /api/health/server-info`
3. Server returns `{ hasUsers: false, bootstrapInviteCode: "abc123" }`
4. Client shows: "Set up your server" with pre-filled invite code
5. User registers, becomes server admin
6. Bootstrap invite is invalidated

### Subsequent Users
1. Server admin creates invite in Admin Panel
2. Admin shares invite code/link with friend
3. Friend opens client, enters invite code on register screen
4. Friend registers successfully

### Account Management
1. User opens Settings > My Account
2. Can change username, status, password
3. Can delete account (with confirmation)

### Admin Management
1. Server admin opens Settings > Admin Panel
2. Can create/revoke invites
3. Can view all users, promote to admin, delete users

---

## Future Enhancements

- Email verification flow
- Password reset via email
- Invite links (URL with embedded code)
- Invite expiration UI (date picker)
- Community-level roles and permissions

---
---

# Plan: User Profile Editing

## Status

Not started.

## Summary

Allow users to customize their profile with:
1. **Display Name** - A customizable name separate from username (UTF-8 with emojis)
2. **Per-Community Display Name Override** - Optional nickname per community
3. **Avatar Image** - Upload with zoom and crop functionality
4. **Display Name Resolution** - Community override → Display name → Username

---

## Phase 11: Database Model Updates

### User Model Updates

**File:** `src/Snacka.Shared/Models/User.cs`

```csharp
// Add to existing User class:
public string? DisplayName { get; set; }  // UTF-8, emojis allowed, max 32 chars
public string? AvatarFileName { get; set; }  // Stored filename (GUID-based)
```

**Display logic helper (computed, not stored):**
```csharp
public string EffectiveDisplayName => DisplayName ?? Username;
```

### UserCommunity Model Updates

**File:** `src/Snacka.Shared/Models/UserServer.cs`

```csharp
// Add to existing UserCommunity class:
public string? DisplayNameOverride { get; set; }  // Per-community nickname, UTF-8, max 32 chars
```

### DbContext Updates

**File:** `src/Snacka.Server/Data/SnackaDbContext.cs`

- Configure DisplayName with max length 32
- Configure DisplayNameOverride with max length 32
- Both support Unicode/UTF-8 natively in EF Core

---

## Phase 12: Avatar Storage Service

### Image Processing Service

**New File:** `src/Snacka.Server/Services/IImageProcessingService.cs`
```csharp
public interface IImageProcessingService
{
    Task<Stream> CropAndResizeAsync(
        Stream imageStream,
        int cropX, int cropY,
        int cropWidth, int cropHeight,
        int outputSize = 256);

    bool IsValidImageFormat(string contentType);
}
```

**New File:** `src/Snacka.Server/Services/ImageProcessingService.cs`
- Uses SkiaSharp or ImageSharp for cross-platform image processing
- Crops image based on client-provided coordinates
- Resizes to standard avatar sizes (256x256, with optional 64x64 thumbnail)
- Outputs as WebP or PNG for optimal quality/size
- Validates image format and dimensions

### Avatar Storage Settings

**File:** `src/Snacka.Server/Services/FileStorageSettings.cs`
```csharp
// Add to existing settings:
public string AvatarsPath { get; set; } = "./uploads/avatars";
public int AvatarMaxSize { get; set; } = 256;  // pixels
public int AvatarThumbnailSize { get; set; } = 64;  // pixels
public long AvatarMaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;  // 5MB upload limit
```

---

## Phase 13: Profile API Updates

### Avatar Upload Endpoint

**File:** `src/Snacka.Server/Controllers/AuthController.cs` (UsersController)

New endpoints:
```csharp
// Upload avatar with crop parameters
[HttpPost("me/avatar")]
[RequestSizeLimit(5_000_000)]  // 5MB
public async Task<ActionResult<AvatarResponse>> UploadAvatar(
    [FromForm] IFormFile file,
    [FromForm] int cropX,
    [FromForm] int cropY,
    [FromForm] int cropWidth,
    [FromForm] int cropHeight)

// Delete avatar
[HttpDelete("me/avatar")]
public async Task<ActionResult> DeleteAvatar()

// Get avatar file
[HttpGet("{userId:guid}/avatar")]
[AllowAnonymous]  // Avatars are public
public async Task<IActionResult> GetAvatar(Guid userId)
```

### Profile Update Endpoint Updates

**File:** `src/Snacka.Server/Controllers/AuthController.cs`

Update `PUT /api/users/me`:
```csharp
public record UpdateProfileRequest(
    [StringLength(32)] string? DisplayName,  // UTF-8, emojis OK
    string? Status
);
// Note: Avatar is handled separately via upload endpoint
// Note: Username change removed (or kept with restrictions)
```

### DTOs Updates

**File:** `src/Snacka.Server/DTOs/AuthDtos.cs`
```csharp
public record UserProfileResponse(
    Guid Id,
    string Username,
    string? DisplayName,
    string EffectiveDisplayName,  // Computed: DisplayName ?? Username
    string Email,
    string? AvatarUrl,  // Full URL to avatar endpoint
    string? Status,
    bool IsOnline,
    bool IsServerAdmin,
    DateTime CreatedAt
);

public record AvatarResponse(string AvatarUrl);
```

---

## Phase 14: Community Nickname API

### Community Member Nickname Endpoint

**File:** `src/Snacka.Server/Controllers/CommunityController.cs`

New endpoint:
```csharp
// Set/update community nickname
[HttpPut("{communityId:guid}/members/me/nickname")]
public async Task<ActionResult<CommunityMemberResponse>> UpdateNickname(
    Guid communityId,
    [FromBody] UpdateNicknameRequest request)

// Clear community nickname
[HttpDelete("{communityId:guid}/members/me/nickname")]
public async Task<ActionResult> ClearNickname(Guid communityId)
```

### DTOs

**File:** `src/Snacka.Server/DTOs/ServerDtos.cs`
```csharp
public record UpdateNicknameRequest(
    [StringLength(32)] string? Nickname  // null to clear
);

// Update existing CommunityMemberResponse:
public record CommunityMemberResponse(
    Guid UserId,
    string Username,
    string? DisplayName,
    string? DisplayNameOverride,  // Community-specific
    string EffectiveDisplayName,  // Override ?? DisplayName ?? Username
    string? AvatarUrl,
    bool IsOnline,
    UserRole Role,
    DateTime JoinedAt
);
```

### Service Updates

**File:** `src/Snacka.Server/Services/CommunityMemberService.cs`
```csharp
// Add methods:
Task<CommunityMemberResponse> UpdateNicknameAsync(
    Guid communityId, Guid userId, string? nickname);
```

---

## Phase 15: Update All Display Name References

### Message Responses

**File:** `src/Snacka.Server/DTOs/ServerDtos.cs`
```csharp
public record MessageResponse(
    // ... existing fields ...
    string AuthorUsername,
    string? AuthorDisplayName,
    string? AuthorDisplayNameOverride,  // From UserCommunity if in community context
    string AuthorEffectiveDisplayName,  // Computed with fallback
    string? AuthorAvatarUrl,
    // ...
);
```

### Direct Message Responses

**File:** `src/Snacka.Server/DTOs/DirectMessageDtos.cs`
```csharp
public record ConversationSummary(
    Guid UserId,
    string Username,
    string? DisplayName,
    string EffectiveDisplayName,
    string? AvatarUrl,
    bool IsOnline,
    DirectMessageResponse? LastMessage,
    int UnreadCount
);
```

### Service Updates

Update all services that return user info to include display name resolution:
- `MessageService` - Include author display names
- `DirectMessageService` - Include participant display names
- `CommunityMemberService` - Include member display names
- `AuthService` - Include display name in profile

---

## Phase 16: Client API Updates

**File:** `src/Snacka.Client/Services/ApiClient.cs`

Add methods:
```csharp
// Avatar
Task<ApiResult<AvatarResponse>> UploadAvatarAsync(
    Stream imageStream, string fileName,
    int cropX, int cropY, int cropWidth, int cropHeight);
Task<ApiResult> DeleteAvatarAsync();

// Profile
Task<ApiResult<UserProfileResponse>> UpdateDisplayNameAsync(string? displayName);

// Community nickname
Task<ApiResult<CommunityMemberResponse>> UpdateCommunityNicknameAsync(
    Guid communityId, string? nickname);
Task<ApiResult> ClearCommunityNicknameAsync(Guid communityId);
```

**File:** `src/Snacka.Client/Services/ApiModels.cs`
```csharp
// Update existing models to include DisplayName fields
public record UserProfileResponse(
    Guid Id,
    string Username,
    string? DisplayName,
    string EffectiveDisplayName,
    string Email,
    string? AvatarUrl,
    string? Status,
    bool IsOnline,
    bool IsServerAdmin,
    DateTime CreatedAt
);
```

---

## Phase 17: Client Profile Editing UI

### Account Settings Updates

**File:** `src/Snacka.Client/ViewModels/AccountSettingsViewModel.cs`

Add properties and commands:
```csharp
// Display name editing
public string? DisplayName { get; set; }
public ReactiveCommand<Unit, Unit> SaveDisplayNameCommand { get; }

// Avatar
public Bitmap? CurrentAvatar { get; set; }
public ReactiveCommand<Unit, Unit> UploadAvatarCommand { get; }
public ReactiveCommand<Unit, Unit> DeleteAvatarCommand { get; }
```

**File:** `src/Snacka.Client/Views/AccountSettingsView.axaml`

Add sections:
- Avatar display with "Change" and "Remove" buttons
- Display name text field with save button
- Character count indicator (max 32)
- Preview of effective display name

---

## Phase 18: Client Avatar Upload with Crop

### Image Cropper Component

**New File:** `src/Snacka.Client/Controls/ImageCropper.axaml`
**New File:** `src/Snacka.Client/Controls/ImageCropper.axaml.cs`

Features:
- Load image from file picker
- Drag to pan image
- Zoom in/out (mouse wheel or slider)
- Square crop overlay (fixed aspect ratio)
- Preview of cropped result
- Returns crop coordinates (x, y, width, height)

### Avatar Upload Dialog

**New File:** `src/Snacka.Client/ViewModels/AvatarUploadViewModel.cs`
**New File:** `src/Snacka.Client/Views/AvatarUploadDialog.axaml`

Features:
- File picker button
- ImageCropper control
- Zoom slider
- Preview thumbnail
- Cancel / Save buttons
- Progress indicator during upload

---

## Phase 19: Client Community Nickname UI

### Member Context Menu

**File:** `src/Snacka.Client/Views/MemberListView.axaml` (or similar)

Add to user's own context menu:
- "Change Nickname" option (opens nickname dialog)

### Nickname Dialog

**New File:** `src/Snacka.Client/ViewModels/NicknameDialogViewModel.cs`
**New File:** `src/Snacka.Client/Views/NicknameDialog.axaml`

Features:
- Current nickname display
- Text field for new nickname (UTF-8, max 32 chars)
- Character count
- Reset to default button
- Cancel / Save buttons

### Community Settings Integration

If community has a settings view, add nickname section there as well.

---

## Phase 20: Tests

### Server Tests

**New File:** `tests/Snacka.Server.Tests/Api/ProfileApiTests.cs`
- Test display name update with UTF-8/emojis
- Test avatar upload with crop
- Test avatar delete
- Test display name length limits

**New File:** `tests/Snacka.Server.Tests/Api/CommunityNicknameTests.cs`
- Test nickname set/update/clear
- Test nickname in message responses
- Test nickname in member list

**Update:** `tests/Snacka.Server.Tests/Services/AuthServiceTests.cs`
- Test EffectiveDisplayName resolution

---

## Files to Create

| File | Purpose |
|------|---------|
| `src/Snacka.Server/Services/IImageProcessingService.cs` | Image processing interface |
| `src/Snacka.Server/Services/ImageProcessingService.cs` | Crop/resize implementation |
| `src/Snacka.Client/Controls/ImageCropper.axaml` | Image crop control |
| `src/Snacka.Client/Controls/ImageCropper.axaml.cs` | Image crop code-behind |
| `src/Snacka.Client/ViewModels/AvatarUploadViewModel.cs` | Avatar upload dialog VM |
| `src/Snacka.Client/Views/AvatarUploadDialog.axaml` | Avatar upload dialog |
| `src/Snacka.Client/ViewModels/NicknameDialogViewModel.cs` | Nickname dialog VM |
| `src/Snacka.Client/Views/NicknameDialog.axaml` | Nickname dialog |
| `tests/Snacka.Server.Tests/Api/ProfileApiTests.cs` | Profile API tests |
| `tests/Snacka.Server.Tests/Api/CommunityNicknameTests.cs` | Nickname tests |

## Files to Modify

| File | Changes |
|------|---------|
| `src/Snacka.Shared/Models/User.cs` | Add DisplayName, AvatarFileName |
| `src/Snacka.Shared/Models/UserServer.cs` | Add DisplayNameOverride |
| `src/Snacka.Server/Data/SnackaDbContext.cs` | Configure new fields |
| `src/Snacka.Server/Controllers/AuthController.cs` | Avatar endpoints, profile updates |
| `src/Snacka.Server/Controllers/CommunityController.cs` | Nickname endpoints |
| `src/Snacka.Server/DTOs/AuthDtos.cs` | Update profile DTOs |
| `src/Snacka.Server/DTOs/ServerDtos.cs` | Update member/message DTOs |
| `src/Snacka.Server/DTOs/DirectMessageDtos.cs` | Update conversation DTOs |
| `src/Snacka.Server/Services/AuthService.cs` | Profile service updates |
| `src/Snacka.Server/Services/CommunityMemberService.cs` | Nickname methods |
| `src/Snacka.Server/Services/FileStorageSettings.cs` | Avatar settings |
| `src/Snacka.Server/Program.cs` | Register ImageProcessingService |
| `src/Snacka.Client/Services/ApiClient.cs` | New API methods |
| `src/Snacka.Client/Services/ApiModels.cs` | Updated DTOs |
| `src/Snacka.Client/ViewModels/AccountSettingsViewModel.cs` | Profile editing |
| `src/Snacka.Client/Views/AccountSettingsView.axaml` | Profile editing UI |

---

## Implementation Order

1. **Phase 11:** Database model updates (DisplayName, AvatarFileName, DisplayNameOverride)
2. **Phase 12:** Avatar storage service (image processing with crop/resize)
3. **Phase 13:** Profile API updates (avatar upload, display name)
4. **Phase 14:** Community nickname API
5. **Phase 15:** Update all display name references in DTOs/services
6. **Phase 16:** Client API updates
7. **Phase 17:** Client profile editing UI
8. **Phase 18:** Client avatar upload with crop component
9. **Phase 19:** Client community nickname UI
10. **Phase 20:** Tests

---

## Display Name Resolution Logic

```
EffectiveDisplayName =
    UserCommunity.DisplayNameOverride  // If in community context and set
    ?? User.DisplayName                 // If set
    ?? User.Username                    // Fallback
```

This is computed server-side and returned in all relevant responses.

---

## User Flow

### Editing Profile
1. User opens Settings > My Account
2. Sees current avatar (or placeholder)
3. Can click "Change Avatar" → Opens upload dialog with crop
4. Can edit Display Name field (UTF-8, emojis, max 32 chars)
5. Saves changes

### Uploading Avatar
1. Click "Change Avatar"
2. File picker opens (images only)
3. Image loads in cropper
4. User zooms/pans to frame face
5. Click "Save" → Image cropped and uploaded
6. Avatar updates throughout app

### Setting Community Nickname
1. In community, right-click own name or open community settings
2. Select "Change Nickname"
3. Enter nickname (UTF-8, emojis, max 32 chars)
4. Save → Nickname appears in that community only
5. Can "Reset" to use default display name

---

## UTF-8 / Emoji Support

- Database: nvarchar/text columns support UTF-8 natively
- Validation: Allow any Unicode characters
- Length limit: 32 characters (not bytes)
- Client: TextBox with proper font fallback for emoji rendering
- Examples of valid display names:
  - `John 🎮`
  - `日本語ユーザー`
  - `Müller`
  - `🔥FireLord🔥`
