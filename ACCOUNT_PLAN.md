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

**ServerInvite** (`src/Miscord.Shared/Models/ServerInvite.cs`)
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

**User Model Updates** (`src/Miscord.Shared/Models/User.cs`)
```csharp
// Add to existing User class:
public bool IsServerAdmin { get; set; } = false;
public bool EmailVerified { get; set; } = false;  // For future use
public Guid? InvitedById { get; set; }  // Who invited this user
public User? InvitedBy { get; set; }
```

### DbContext Updates

**File:** `src/Miscord.Server/Data/MiscordDbContext.cs`

- Add `DbSet<ServerInvite> ServerInvites`
- Configure ServerInvite entity with unique index on Code
- Configure User.InvitedBy relationship

---

## Phase 2: Server Invite Service [COMPLETED]

**New File:** `src/Miscord.Server/Services/IServerInviteService.cs`
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

**New File:** `src/Miscord.Server/Services/ServerInviteService.cs`
- Implements the interface
- Code generation: 8 character alphanumeric (e.g., "a1b2c3d4")
- Validation checks: not revoked, not expired, uses not exceeded

---

## Phase 3: Auth Service Updates [COMPLETED]

**File:** `src/Miscord.Server/Services/AuthService.cs`

Update `RegisterAsync` to:
1. Require invite code in request
2. Validate invite code via IServerInviteService
3. If first user (no users exist), make them server admin
4. Mark invite as used (increment CurrentUses)
5. Link user to inviter (InvitedById)

**File:** `src/Miscord.Server/DTOs/AuthDtos.cs`

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

**New File:** `src/Miscord.Server/Controllers/AdminController.cs`

Endpoints (require server admin):
- `GET /api/admin/invites` - List all invites
- `POST /api/admin/invites` - Create new invite
- `DELETE /api/admin/invites/{id}` - Revoke invite
- `GET /api/admin/users` - List all users
- `PUT /api/admin/users/{id}/admin` - Toggle user admin status
- `DELETE /api/admin/users/{id}` - Delete user account

**New File:** `src/Miscord.Server/DTOs/AdminDtos.cs`
```csharp
public record CreateInviteRequest(int MaxUses = 0, DateTime? ExpiresAt = null);
public record ServerInviteResponse(Guid Id, string Code, int MaxUses, int CurrentUses,
    DateTime? ExpiresAt, bool IsRevoked, string? CreatedByUsername, DateTime CreatedAt);
public record AdminUserResponse(Guid Id, string Username, string Email, bool IsServerAdmin,
    bool IsOnline, DateTime CreatedAt, string? InvitedByUsername);
```

---

## Phase 5: Bootstrap Invite Endpoint [COMPLETED]

**File:** `src/Miscord.Server/Controllers/HealthController.cs`

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

**File:** `src/Miscord.Server/Controllers/AuthController.cs`

New endpoints:
- `PUT /api/users/me/password` - Change password
- `DELETE /api/users/me` - Delete own account

**File:** `src/Miscord.Server/DTOs/AuthDtos.cs`
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

**File:** `src/Miscord.Client/Services/ApiClient.cs`

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

**File:** `src/Miscord.Client/ViewModels/RegisterViewModel.cs`

- Add `InviteCode` property
- On load, call `GetServerInfoAsync()` to check if server has users
- If no users, auto-fill the bootstrap invite code
- Show message: "You will be the server administrator"

**File:** `src/Miscord.Client/Views/RegisterView.axaml`

- Add invite code input field
- Show bootstrap message when applicable

**File:** `src/Miscord.Client/ViewModels/LoginViewModel.cs`

- On load, call `GetServerInfoAsync()`
- If no users exist, show "Set up server" message and switch to register

---

## Phase 9: Client - Account Settings UI [COMPLETED]

**File:** `src/Miscord.Client/ViewModels/SettingsViewModel.cs`

Enable "My Account" section:
- Edit username
- Edit status
- Change password (current + new + confirm)
- Delete account (with confirmation)

**File:** `src/Miscord.Client/Views/SettingsView.axaml`

- Implement account settings tab content
- Add password change form
- Add delete account button with confirmation dialog

---

## Phase 10: Client - Admin Panel [COMPLETED]

**New File:** `src/Miscord.Client/ViewModels/AdminPanelViewModel.cs`
**New File:** `src/Miscord.Client/Views/AdminPanelView.axaml`

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

**File:** `src/Miscord.Client/ViewModels/MainAppViewModel.cs`
- Add `IsServerAdmin` property
- Load from user info on login

---

## Files Created

| File | Purpose |
|------|---------|
| `src/Miscord.Shared/Models/ServerInvite.cs` | ServerInvite model |
| `src/Miscord.Server/Services/IServerInviteService.cs` | Service interface |
| `src/Miscord.Server/Services/ServerInviteService.cs` | Service implementation |
| `src/Miscord.Server/Controllers/AdminController.cs` | Admin API endpoints |
| `src/Miscord.Server/DTOs/AdminDtos.cs` | Admin DTOs |
| `src/Miscord.Client/ViewModels/AdminPanelViewModel.cs` | Admin panel VM |
| `src/Miscord.Client/ViewModels/AccountSettingsViewModel.cs` | Account settings VM |
| `src/Miscord.Client/Views/AdminPanelView.axaml` | Admin panel view |
| `src/Miscord.Client/Views/AdminPanelView.axaml.cs` | Admin panel code-behind |
| `src/Miscord.Client/Views/AccountSettingsView.axaml` | Account settings view |
| `src/Miscord.Client/Views/AccountSettingsView.axaml.cs` | Account settings code-behind |
| `tests/Miscord.Server.Tests/Api/AdminApiTests.cs` | Admin API tests |

## Files Modified

| File | Changes |
|------|---------|
| `src/Miscord.Shared/Models/User.cs` | Add IsServerAdmin, EmailVerified, InvitedById |
| `src/Miscord.Server/Data/MiscordDbContext.cs` | Add ServerInvites DbSet, configure relationships |
| `src/Miscord.Server/Services/AuthService.cs` | Require invite code, first user = admin |
| `src/Miscord.Server/Services/IAuthService.cs` | Update interface |
| `src/Miscord.Server/DTOs/AuthDtos.cs` | Add InviteCode to RegisterRequest, new DTOs |
| `src/Miscord.Server/Controllers/AuthController.cs` | Add server-info, password change endpoints |
| `src/Miscord.Server/Controllers/HealthController.cs` | Add server-info endpoint |
| `src/Miscord.Server/Program.cs` | Register IServerInviteService |
| `src/Miscord.Client/Services/ApiClient.cs` | Add admin and account methods |
| `src/Miscord.Client/Services/ApiModels.cs` | Add new DTOs |
| `src/Miscord.Client/Services/IApiClient.cs` | Add new method signatures |
| `src/Miscord.Client/Services/ServerConnection.cs` | Update ServerInfoResponse |
| `src/Miscord.Client/ViewModels/RegisterViewModel.cs` | Add invite code handling |
| `src/Miscord.Client/Views/RegisterView.axaml` | Add invite code field |
| `src/Miscord.Client/ViewModels/SettingsViewModel.cs` | Add account settings and admin panel |
| `src/Miscord.Client/Views/SettingsView.axaml` | Add account settings and admin panel UI |
| `src/Miscord.Client/ViewModels/MainWindowViewModel.cs` | Pass IsServerAdmin to settings |
| `tests/Miscord.Server.Tests/Api/AuthApiTests.cs` | Update tests for invite system |
| `tests/Miscord.Server.Tests/IntegrationTestBase.cs` | Add invite code helpers |
| `tests/Miscord.Server.Tests/Services/AuthServiceTests.cs` | Update tests for invite system |

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
- User profile editing (username, status)
- Community-level roles and permissions
