using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

public class AccountSettingsViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly Action _onAccountDeleted;
    private readonly Action<string>? _onUsernameChanged;
    private readonly Func<Task<IStorageFile?>>? _selectImageFile;

    // Profile editing
    private Guid _userId;
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _status = string.Empty;
    private string _email = string.Empty;
    private string? _profileMessage;
    private bool _profileMessageIsError;
    private bool _isSavingProfile;
    private bool _isLoadingProfile;

    // Avatar
    private string? _avatarUrl;
    private bool _hasAvatar;
    private Bitmap? _selectedImage;
    private string? _selectedImagePath;
    private string? _avatarMessage;
    private bool _avatarMessageIsError;
    private bool _isUploadingAvatar;

    // Original values for comparison
    private string _originalUsername = string.Empty;
    private string _originalDisplayName = string.Empty;
    private string _originalStatus = string.Empty;

    private string _currentPassword = string.Empty;
    private string _newPassword = string.Empty;
    private string _confirmPassword = string.Empty;
    private string? _passwordMessage;
    private bool _passwordMessageIsError;
    private bool _isChangingPassword;

    private string? _deleteAccountError;
    private bool _isDeletingAccount;
    private bool _showDeleteConfirmation;

    public AccountSettingsViewModel(
        IApiClient apiClient,
        Action onAccountDeleted,
        Action<string>? onUsernameChanged = null,
        Func<Task<IStorageFile?>>? selectImageFile = null)
    {
        _apiClient = apiClient;
        _onAccountDeleted = onAccountDeleted;
        _onUsernameChanged = onUsernameChanged;
        _selectImageFile = selectImageFile;

        // Profile commands
        var canSaveProfile = this.WhenAnyValue(
            x => x.IsSavingProfile,
            x => x.HasProfileChanges,
            (isSaving, hasChanges) => !isSaving && hasChanges);
        SaveProfileCommand = ReactiveCommand.CreateFromTask(SaveProfileAsync, canSaveProfile);
        ResetProfileCommand = ReactiveCommand.Create(ResetProfile);

        // Avatar commands
        var canSelectAvatar = this.WhenAnyValue(x => x.IsUploadingAvatar, isUploading => !isUploading);
        SelectAvatarCommand = ReactiveCommand.CreateFromTask(SelectAvatarAsync, canSelectAvatar);

        var canUploadAvatar = this.WhenAnyValue(
            x => x.IsUploadingAvatar,
            x => x.SelectedImage,
            (isUploading, image) => !isUploading && image is not null);
        UploadAvatarCommand = ReactiveCommand.CreateFromTask(UploadAvatarAsync, canUploadAvatar);

        var canDeleteAvatar = this.WhenAnyValue(
            x => x.IsUploadingAvatar,
            x => x.HasAvatar,
            (isUploading, hasAvatar) => !isUploading && hasAvatar);
        DeleteAvatarCommand = ReactiveCommand.CreateFromTask(DeleteAvatarAsync, canDeleteAvatar);

        CancelAvatarSelectionCommand = ReactiveCommand.Create(CancelAvatarSelection);

        var canChangePassword = this.WhenAnyValue(x => x.IsChangingPassword, isChanging => !isChanging);
        ChangePasswordCommand = ReactiveCommand.CreateFromTask(ChangePasswordAsync, canChangePassword);

        var canDelete = this.WhenAnyValue(x => x.IsDeletingAccount, isDeleting => !isDeleting);
        DeleteAccountCommand = ReactiveCommand.CreateFromTask(DeleteAccountAsync, canDelete);

        ShowDeleteConfirmationCommand = ReactiveCommand.Create(() => { ShowDeleteConfirmation = true; });
        CancelDeleteCommand = ReactiveCommand.Create(() => { ShowDeleteConfirmation = false; });

        // Load profile on initialization
        Observable.FromAsync(LoadProfileAsync).Subscribe();
    }

    // Profile properties
    public string Username
    {
        get => _username;
        set
        {
            this.RaiseAndSetIfChanged(ref _username, value);
            this.RaisePropertyChanged(nameof(HasProfileChanges));
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            this.RaiseAndSetIfChanged(ref _displayName, value);
            this.RaisePropertyChanged(nameof(HasProfileChanges));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(HasProfileChanges));
        }
    }

    public string Email
    {
        get => _email;
        set => this.RaiseAndSetIfChanged(ref _email, value);
    }

    public string? ProfileMessage
    {
        get => _profileMessage;
        set => this.RaiseAndSetIfChanged(ref _profileMessage, value);
    }

    public bool ProfileMessageIsError
    {
        get => _profileMessageIsError;
        set => this.RaiseAndSetIfChanged(ref _profileMessageIsError, value);
    }

    public bool IsSavingProfile
    {
        get => _isSavingProfile;
        set => this.RaiseAndSetIfChanged(ref _isSavingProfile, value);
    }

    public bool IsLoadingProfile
    {
        get => _isLoadingProfile;
        set => this.RaiseAndSetIfChanged(ref _isLoadingProfile, value);
    }

    public bool HasProfileChanges =>
        Username != _originalUsername ||
        DisplayName != _originalDisplayName ||
        Status != _originalStatus;

    // Avatar properties
    public string? AvatarUrl
    {
        get => _avatarUrl;
        set => this.RaiseAndSetIfChanged(ref _avatarUrl, value);
    }

    public bool HasAvatar
    {
        get => _hasAvatar;
        set => this.RaiseAndSetIfChanged(ref _hasAvatar, value);
    }

    public Bitmap? SelectedImage
    {
        get => _selectedImage;
        set => this.RaiseAndSetIfChanged(ref _selectedImage, value);
    }

    public string? AvatarMessage
    {
        get => _avatarMessage;
        set => this.RaiseAndSetIfChanged(ref _avatarMessage, value);
    }

    public bool AvatarMessageIsError
    {
        get => _avatarMessageIsError;
        set => this.RaiseAndSetIfChanged(ref _avatarMessageIsError, value);
    }

    public bool IsUploadingAvatar
    {
        get => _isUploadingAvatar;
        set => this.RaiseAndSetIfChanged(ref _isUploadingAvatar, value);
    }

    public bool HasSelectedImage => SelectedImage is not null;

    public string CurrentPassword
    {
        get => _currentPassword;
        set => this.RaiseAndSetIfChanged(ref _currentPassword, value);
    }

    public string NewPassword
    {
        get => _newPassword;
        set => this.RaiseAndSetIfChanged(ref _newPassword, value);
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set => this.RaiseAndSetIfChanged(ref _confirmPassword, value);
    }

    public string? PasswordMessage
    {
        get => _passwordMessage;
        set => this.RaiseAndSetIfChanged(ref _passwordMessage, value);
    }

    public bool PasswordMessageIsError
    {
        get => _passwordMessageIsError;
        set => this.RaiseAndSetIfChanged(ref _passwordMessageIsError, value);
    }

    public bool IsChangingPassword
    {
        get => _isChangingPassword;
        set => this.RaiseAndSetIfChanged(ref _isChangingPassword, value);
    }

    public string? DeleteAccountError
    {
        get => _deleteAccountError;
        set => this.RaiseAndSetIfChanged(ref _deleteAccountError, value);
    }

    public bool IsDeletingAccount
    {
        get => _isDeletingAccount;
        set => this.RaiseAndSetIfChanged(ref _isDeletingAccount, value);
    }

    public bool ShowDeleteConfirmation
    {
        get => _showDeleteConfirmation;
        set => this.RaiseAndSetIfChanged(ref _showDeleteConfirmation, value);
    }

    public ReactiveCommand<Unit, Unit> SaveProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAvatarCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadAvatarCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteAvatarCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAvatarSelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangePasswordCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowDeleteConfirmationCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteCommand { get; }

    private async Task LoadProfileAsync()
    {
        IsLoadingProfile = true;
        try
        {
            var result = await _apiClient.GetProfileAsync();
            if (result.Success && result.Data is not null)
            {
                _userId = result.Data.Id;
                Username = result.Data.Username;
                DisplayName = result.Data.DisplayName ?? string.Empty;
                Status = result.Data.Status ?? string.Empty;
                Email = result.Data.Email;

                _originalUsername = Username;
                _originalDisplayName = DisplayName;
                _originalStatus = Status;

                // Update avatar info
                HasAvatar = !string.IsNullOrEmpty(result.Data.Avatar);
                AvatarUrl = HasAvatar ? _apiClient.GetAvatarUrl(_userId) : null;

                this.RaisePropertyChanged(nameof(HasProfileChanges));
            }
        }
        finally
        {
            IsLoadingProfile = false;
        }
    }

    private async Task SelectAvatarAsync()
    {
        if (_selectImageFile is null)
            return;

        AvatarMessage = null;

        try
        {
            var file = await _selectImageFile();
            if (file is null)
                return;

            await using var stream = await file.OpenReadAsync();
            _selectedImagePath = file.Name;

            // Load the image for preview
            var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            SelectedImage?.Dispose();
            SelectedImage = new Bitmap(memoryStream);

            this.RaisePropertyChanged(nameof(HasSelectedImage));
        }
        catch (Exception ex)
        {
            AvatarMessage = $"Failed to load image: {ex.Message}";
            AvatarMessageIsError = true;
        }
    }

    private async Task UploadAvatarAsync()
    {
        if (SelectedImage is null || string.IsNullOrEmpty(_selectedImagePath))
            return;

        AvatarMessage = null;
        IsUploadingAvatar = true;

        try
        {
            // Re-read the file for upload
            if (_selectImageFile is null)
                return;

            // For simplicity, we'll save the bitmap to a memory stream
            using var memoryStream = new MemoryStream();
            SelectedImage.Save(memoryStream);
            memoryStream.Position = 0;

            // Upload without crop parameters (server will center-crop if needed)
            var result = await _apiClient.UploadAvatarAsync(memoryStream, _selectedImagePath);

            if (result.Success && result.Data is not null)
            {
                AvatarMessage = "Avatar updated successfully";
                AvatarMessageIsError = false;

                // Update avatar state
                HasAvatar = !string.IsNullOrEmpty(result.Data.Avatar);
                // Add cache buster to force reload
                AvatarUrl = HasAvatar ? $"{_apiClient.GetAvatarUrl(_userId)}?v={DateTime.UtcNow.Ticks}" : null;

                // Clear selection
                CancelAvatarSelection();
            }
            else
            {
                AvatarMessage = result.Error ?? "Failed to upload avatar";
                AvatarMessageIsError = true;
            }
        }
        catch (Exception ex)
        {
            AvatarMessage = $"Upload failed: {ex.Message}";
            AvatarMessageIsError = true;
        }
        finally
        {
            IsUploadingAvatar = false;
        }
    }

    private async Task DeleteAvatarAsync()
    {
        AvatarMessage = null;
        IsUploadingAvatar = true;

        try
        {
            var result = await _apiClient.DeleteAvatarAsync();

            if (result.Success)
            {
                AvatarMessage = "Avatar removed successfully";
                AvatarMessageIsError = false;
                HasAvatar = false;
                AvatarUrl = null;
            }
            else
            {
                AvatarMessage = result.Error ?? "Failed to remove avatar";
                AvatarMessageIsError = true;
            }
        }
        finally
        {
            IsUploadingAvatar = false;
        }
    }

    private void CancelAvatarSelection()
    {
        SelectedImage?.Dispose();
        SelectedImage = null;
        _selectedImagePath = null;
        this.RaisePropertyChanged(nameof(HasSelectedImage));
    }

    private async Task SaveProfileAsync()
    {
        ProfileMessage = null;

        if (string.IsNullOrWhiteSpace(Username))
        {
            ProfileMessage = "Username is required";
            ProfileMessageIsError = true;
            return;
        }

        if (Username.Length < 3)
        {
            ProfileMessage = "Username must be at least 3 characters";
            ProfileMessageIsError = true;
            return;
        }

        IsSavingProfile = true;
        try
        {
            var result = await _apiClient.UpdateProfileAsync(
                Username != _originalUsername ? Username : null,
                DisplayName != _originalDisplayName ? (string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName) : null,
                Status != _originalStatus ? (string.IsNullOrWhiteSpace(Status) ? null : Status) : null);

            if (result.Success && result.Data is not null)
            {
                ProfileMessage = "Profile updated successfully";
                ProfileMessageIsError = false;

                // Update original values
                _originalUsername = result.Data.Username;
                _originalDisplayName = result.Data.DisplayName ?? string.Empty;
                _originalStatus = result.Data.Status ?? string.Empty;

                Username = _originalUsername;
                DisplayName = _originalDisplayName;
                Status = _originalStatus;

                this.RaisePropertyChanged(nameof(HasProfileChanges));

                // Notify if username changed
                if (_onUsernameChanged is not null && result.Data.Username != _originalUsername)
                {
                    _onUsernameChanged(result.Data.Username);
                }
            }
            else
            {
                ProfileMessage = result.Error ?? "Failed to update profile";
                ProfileMessageIsError = true;
            }
        }
        finally
        {
            IsSavingProfile = false;
        }
    }

    private void ResetProfile()
    {
        Username = _originalUsername;
        DisplayName = _originalDisplayName;
        Status = _originalStatus;
        ProfileMessage = null;
        this.RaisePropertyChanged(nameof(HasProfileChanges));
    }

    private async Task ChangePasswordAsync()
    {
        PasswordMessage = null;

        if (string.IsNullOrWhiteSpace(CurrentPassword))
        {
            PasswordMessage = "Current password is required";
            PasswordMessageIsError = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(NewPassword))
        {
            PasswordMessage = "New password is required";
            PasswordMessageIsError = true;
            return;
        }

        if (NewPassword.Length < 8)
        {
            PasswordMessage = "New password must be at least 8 characters";
            PasswordMessageIsError = true;
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            PasswordMessage = "Passwords do not match";
            PasswordMessageIsError = true;
            return;
        }

        IsChangingPassword = true;

        try
        {
            var result = await _apiClient.ChangePasswordAsync(CurrentPassword, NewPassword);
            if (result.Success)
            {
                PasswordMessage = "Password changed successfully";
                PasswordMessageIsError = false;
                CurrentPassword = string.Empty;
                NewPassword = string.Empty;
                ConfirmPassword = string.Empty;
            }
            else
            {
                PasswordMessage = result.Error ?? "Failed to change password";
                PasswordMessageIsError = true;
            }
        }
        finally
        {
            IsChangingPassword = false;
        }
    }

    private async Task DeleteAccountAsync()
    {
        DeleteAccountError = null;
        IsDeletingAccount = true;

        try
        {
            var result = await _apiClient.DeleteAccountAsync();
            if (result.Success)
            {
                _onAccountDeleted();
            }
            else
            {
                DeleteAccountError = result.Error ?? "Failed to delete account";
            }
        }
        finally
        {
            IsDeletingAccount = false;
        }
    }
}
