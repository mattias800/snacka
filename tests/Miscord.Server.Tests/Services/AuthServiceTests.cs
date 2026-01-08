using Microsoft.Extensions.Options;
using Miscord.Server.DTOs;
using Miscord.Server.Services;

namespace Miscord.Server.Tests.Services;

[TestClass]
public class AuthServiceTests
{
    private static IOptions<JwtSettings> CreateJwtSettings() => Options.Create(new JwtSettings
    {
        SecretKey = "ThisIsATestSecretKeyThatIsLongEnoughForHmacSha256!",
        Issuer = "TestIssuer",
        Audience = "TestAudience",
        AccessTokenExpirationMinutes = 60,
        RefreshTokenExpirationDays = 7
    });

    private static async Task<(AuthService service, string inviteCode)> CreateServiceWithInviteAsync(Data.MiscordDbContext db)
    {
        var inviteService = new ServerInviteService(db);
        var service = new AuthService(db, CreateJwtSettings(), inviteService);

        // Create a test invite code
        var invite = await inviteService.CreateInviteAsync(null, maxUses: 0);
        return (service, invite.Code);
    }

    [TestMethod]
    public async Task RegisterAsync_WithValidData_CreatesUserAndReturnsTokens()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, inviteCode) = await CreateServiceWithInviteAsync(db);
        var request = new RegisterRequest("testuser", "test@example.com", "Password123!", inviteCode);

        // Act
        var result = await service.RegisterAsync(request);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("testuser", result.Username);
        Assert.AreEqual("test@example.com", result.Email);
        Assert.IsFalse(string.IsNullOrEmpty(result.AccessToken));
        Assert.IsFalse(string.IsNullOrEmpty(result.RefreshToken));
        Assert.IsTrue(result.ExpiresAt > DateTime.UtcNow);
    }

    [TestMethod]
    public async Task RegisterAsync_FirstUser_BecomesServerAdmin()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, inviteCode) = await CreateServiceWithInviteAsync(db);
        var request = new RegisterRequest("firstuser", "first@example.com", "Password123!", inviteCode);

        // Act
        var result = await service.RegisterAsync(request);

        // Assert
        Assert.IsTrue(result.IsServerAdmin, "First user should be server admin");
    }

    [TestMethod]
    public async Task RegisterAsync_SecondUser_IsNotServerAdmin()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var inviteService = new ServerInviteService(db);
        var service = new AuthService(db, CreateJwtSettings(), inviteService);

        var invite1 = await inviteService.CreateInviteAsync(null, maxUses: 0);
        var invite2 = await inviteService.CreateInviteAsync(null, maxUses: 0);

        // First user
        await service.RegisterAsync(new RegisterRequest("firstuser", "first@example.com", "Password123!", invite1.Code));

        // Act - second user
        var result = await service.RegisterAsync(new RegisterRequest("seconduser", "second@example.com", "Password123!", invite2.Code));

        // Assert
        Assert.IsFalse(result.IsServerAdmin, "Second user should not be server admin");
    }

    [TestMethod]
    public async Task RegisterAsync_WithInvalidInviteCode_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, _) = await CreateServiceWithInviteAsync(db);
        var request = new RegisterRequest("testuser", "test@example.com", "Password123!", "invalidcode");

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.RegisterAsync(request));
        Assert.AreEqual("Invalid or expired invite code.", exception.Message);
    }

    [TestMethod]
    public async Task RegisterAsync_WithDuplicateEmail_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var inviteService = new ServerInviteService(db);
        var service = new AuthService(db, CreateJwtSettings(), inviteService);
        var invite1 = await inviteService.CreateInviteAsync(null);
        var invite2 = await inviteService.CreateInviteAsync(null);
        await service.RegisterAsync(new RegisterRequest("user1", "duplicate@example.com", "Password123!", invite1.Code));

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.RegisterAsync(new RegisterRequest("user2", "duplicate@example.com", "Password123!", invite2.Code)));
        Assert.AreEqual("Email is already registered.", exception.Message);
    }

    [TestMethod]
    public async Task RegisterAsync_WithDuplicateUsername_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var inviteService = new ServerInviteService(db);
        var service = new AuthService(db, CreateJwtSettings(), inviteService);
        var invite1 = await inviteService.CreateInviteAsync(null);
        var invite2 = await inviteService.CreateInviteAsync(null);
        await service.RegisterAsync(new RegisterRequest("duplicateuser", "user1@example.com", "Password123!", invite1.Code));

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.RegisterAsync(new RegisterRequest("duplicateuser", "user2@example.com", "Password123!", invite2.Code)));
        Assert.AreEqual("Username is already taken.", exception.Message);
    }

    [TestMethod]
    public async Task LoginAsync_WithValidCredentials_ReturnsTokens()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, inviteCode) = await CreateServiceWithInviteAsync(db);
        await service.RegisterAsync(new RegisterRequest("testuser", "test@example.com", "Password123!", inviteCode));

        // Act
        var result = await service.LoginAsync(new LoginRequest("test@example.com", "Password123!"));

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("testuser", result.Username);
        Assert.IsFalse(string.IsNullOrEmpty(result.AccessToken));
    }

    [TestMethod]
    public async Task LoginAsync_WithInvalidPassword_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, inviteCode) = await CreateServiceWithInviteAsync(db);
        await service.RegisterAsync(new RegisterRequest("testuser", "test@example.com", "Password123!", inviteCode));

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.LoginAsync(new LoginRequest("test@example.com", "WrongPassword!")));
        Assert.AreEqual("Invalid email or password.", exception.Message);
    }

    [TestMethod]
    public async Task LoginAsync_WithNonExistentEmail_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, _) = await CreateServiceWithInviteAsync(db);

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.LoginAsync(new LoginRequest("nonexistent@example.com", "Password123!")));
        Assert.AreEqual("Invalid email or password.", exception.Message);
    }

    [TestMethod]
    public async Task GetProfileAsync_WithValidUserId_ReturnsProfile()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, inviteCode) = await CreateServiceWithInviteAsync(db);
        var authResult = await service.RegisterAsync(new RegisterRequest("testuser", "test@example.com", "Password123!", inviteCode));

        // Act
        var profile = await service.GetProfileAsync(authResult.UserId);

        // Assert
        Assert.IsNotNull(profile);
        Assert.AreEqual("testuser", profile.Username);
        Assert.AreEqual("test@example.com", profile.Email);
    }

    [TestMethod]
    public async Task GetProfileAsync_WithInvalidUserId_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, _) = await CreateServiceWithInviteAsync(db);

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.GetProfileAsync(Guid.NewGuid()));
        Assert.AreEqual("User not found.", exception.Message);
    }

    [TestMethod]
    public async Task UpdateProfileAsync_WithValidData_UpdatesProfile()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, inviteCode) = await CreateServiceWithInviteAsync(db);
        var authResult = await service.RegisterAsync(new RegisterRequest("testuser", "test@example.com", "Password123!", inviteCode));
        var updateRequest = new UpdateProfileRequest("newusername", "Cool Display Name 🎮", "Hello!");

        // Act
        var profile = await service.UpdateProfileAsync(authResult.UserId, updateRequest);

        // Assert
        Assert.AreEqual("newusername", profile.Username);
        Assert.AreEqual("Hello!", profile.Status);

        // Verify display name was saved by checking the database
        var user = await db.Users.FindAsync(authResult.UserId);
        Assert.AreEqual("Cool Display Name 🎮", user?.DisplayName);
    }

    [TestMethod]
    public async Task UpdateProfileAsync_WithDuplicateUsername_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var inviteService = new ServerInviteService(db);
        var service = new AuthService(db, CreateJwtSettings(), inviteService);
        var invite1 = await inviteService.CreateInviteAsync(null);
        var invite2 = await inviteService.CreateInviteAsync(null);
        await service.RegisterAsync(new RegisterRequest("existinguser", "existing@example.com", "Password123!", invite1.Code));
        var authResult = await service.RegisterAsync(new RegisterRequest("testuser", "test@example.com", "Password123!", invite2.Code));

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.UpdateProfileAsync(authResult.UserId, new UpdateProfileRequest("existinguser", null, null)));
        Assert.AreEqual("Username is already taken.", exception.Message);
    }

    [TestMethod]
    public async Task RefreshTokenAsync_WithValidToken_ReturnsNewTokens()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (service, inviteCode) = await CreateServiceWithInviteAsync(db);
        var authResult = await service.RegisterAsync(new RegisterRequest("testuser", "test@example.com", "Password123!", inviteCode));

        // Act
        var newTokens = await service.RefreshTokenAsync(authResult.RefreshToken);

        // Assert
        Assert.IsNotNull(newTokens);
        Assert.AreEqual(authResult.UserId, newTokens.UserId);
        Assert.IsFalse(string.IsNullOrEmpty(newTokens.AccessToken));
    }
}
