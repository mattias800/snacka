using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

/// <summary>
/// ViewModel for inline DM content displayed within the main app.
/// Encapsulates all DM-related state and logic.
/// </summary>
public class DMContentViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;
    private readonly Guid _currentUserId;
    private readonly Action<string?> _onError;

    private bool _isLoading;
    private Guid? _recipientId;
    private string? _recipientName;
    private string _messageInput = string.Empty;
    private DirectMessageResponse? _editingMessage;
    private string _editingMessageContent = string.Empty;

    // Typing indicator state
    private ObservableCollection<TypingUser> _typingUsers = new();
    private DateTime _lastTypingSent = DateTime.MinValue;
    private const int TypingThrottleMs = 3000;
    private const int TypingTimeoutMs = 5000;

    public DMContentViewModel(
        IApiClient apiClient,
        ISignalRService signalR,
        Guid currentUserId,
        Action<string?> onError)
    {
        _apiClient = apiClient;
        _signalR = signalR;
        _currentUserId = currentUserId;
        _onError = onError;

        Messages = new ObservableCollection<DirectMessageResponse>();

        // Commands
        var canSendMessage = this.WhenAnyValue(
            x => x.MessageInput,
            x => x.RecipientId,
            x => x.IsLoading,
            (message, recipientId, isLoading) =>
                !string.IsNullOrWhiteSpace(message) &&
                recipientId.HasValue &&
                !isLoading);

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSendMessage);
        CloseCommand = ReactiveCommand.Create(Close);
        StartEditMessageCommand = ReactiveCommand.Create<DirectMessageResponse>(StartEditMessage);
        SaveMessageEditCommand = ReactiveCommand.CreateFromTask(SaveMessageEditAsync);
        CancelEditMessageCommand = ReactiveCommand.Create(CancelEditMessage);
        DeleteMessageCommand = ReactiveCommand.CreateFromTask<DirectMessageResponse>(DeleteMessageAsync);

        // Setup SignalR handlers
        SetupSignalRHandlers();
    }

    private void SetupSignalRHandlers()
    {
        _signalR.DirectMessageReceived += message => Dispatcher.UIThread.Post(() =>
        {
            // Clear typing indicator for this user since they sent a message
            var typingUser = _typingUsers.FirstOrDefault(t => t.UserId == message.SenderId);
            if (typingUser != null)
            {
                _typingUsers.Remove(typingUser);
                this.RaisePropertyChanged(nameof(TypingIndicatorText));
                this.RaisePropertyChanged(nameof(IsTyping));
            }

            // If this message is from/to the current DM recipient
            if (RecipientId.HasValue &&
                (message.SenderId == RecipientId.Value || message.RecipientId == RecipientId.Value))
            {
                if (!Messages.Any(m => m.Id == message.Id))
                    Messages.Add(message);
            }
        });

        _signalR.DirectMessageEdited += message => Dispatcher.UIThread.Post(() =>
        {
            var index = Messages.ToList().FindIndex(m => m.Id == message.Id);
            if (index >= 0)
                Messages[index] = message;
        });

        _signalR.DirectMessageDeleted += e => Dispatcher.UIThread.Post(() =>
        {
            var message = Messages.FirstOrDefault(m => m.Id == e.MessageId);
            if (message is not null)
                Messages.Remove(message);
        });

        _signalR.DMUserTyping += e => Dispatcher.UIThread.Post(() =>
        {
            // Only show typing for the current DM recipient
            if (RecipientId.HasValue && e.UserId == RecipientId.Value)
            {
                var existing = _typingUsers.FirstOrDefault(t => t.UserId == e.UserId);
                if (existing != null)
                    _typingUsers.Remove(existing);
                _typingUsers.Add(new TypingUser(e.UserId, e.Username, DateTime.UtcNow));
                this.RaisePropertyChanged(nameof(TypingIndicatorText));
                this.RaisePropertyChanged(nameof(IsTyping));
            }
        });
    }

    /// <summary>
    /// Opens a DM conversation with the specified user.
    /// </summary>
    public void OpenConversation(Guid userId, string username)
    {
        // Don't DM yourself
        if (userId == _currentUserId) return;

        RecipientId = userId;
        RecipientName = username;
        MessageInput = string.Empty;
        EditingMessage = null;
        EditingMessageContent = string.Empty;

        // Load messages asynchronously
        _ = LoadMessagesAsync();
    }

    /// <summary>
    /// Cleans up expired typing indicators. Call this periodically.
    /// </summary>
    public void CleanupExpiredTypingIndicators()
    {
        var now = DateTime.UtcNow;
        var expired = _typingUsers.Where(t => (now - t.LastTypingAt).TotalMilliseconds > TypingTimeoutMs).ToList();

        foreach (var user in expired)
            _typingUsers.Remove(user);

        if (expired.Count > 0)
        {
            this.RaisePropertyChanged(nameof(TypingIndicatorText));
            this.RaisePropertyChanged(nameof(IsTyping));
        }
    }

    // Properties
    public ObservableCollection<DirectMessageResponse> Messages { get; }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public Guid? RecipientId
    {
        get => _recipientId;
        private set
        {
            this.RaiseAndSetIfChanged(ref _recipientId, value);
            this.RaisePropertyChanged(nameof(IsOpen));
        }
    }

    public string? RecipientName
    {
        get => _recipientName;
        private set => this.RaiseAndSetIfChanged(ref _recipientName, value);
    }

    public string MessageInput
    {
        get => _messageInput;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageInput, value);
            // Send typing indicator (throttled)
            SendTypingIndicatorThrottled();
        }
    }

    public DirectMessageResponse? EditingMessage
    {
        get => _editingMessage;
        private set => this.RaiseAndSetIfChanged(ref _editingMessage, value);
    }

    public string EditingMessageContent
    {
        get => _editingMessageContent;
        set => this.RaiseAndSetIfChanged(ref _editingMessageContent, value);
    }

    public bool IsOpen => RecipientId.HasValue;

    public bool IsTyping => _typingUsers.Count > 0;

    public string TypingIndicatorText
    {
        get
        {
            if (_typingUsers.Count == 0) return string.Empty;
            return $"{_typingUsers[0].Username} is typing...";
        }
    }

    // Commands
    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<DirectMessageResponse, Unit> StartEditMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveMessageEditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditMessageCommand { get; }
    public ReactiveCommand<DirectMessageResponse, Unit> DeleteMessageCommand { get; }

    // Private methods
    private async Task LoadMessagesAsync()
    {
        if (!RecipientId.HasValue) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.GetDirectMessagesAsync(RecipientId.Value);
            if (result.Success && result.Data is not null)
            {
                Messages.Clear();
                foreach (var message in result.Data)
                    Messages.Add(message);
            }

            // Mark conversation as read
            await _apiClient.MarkConversationAsReadAsync(RecipientId.Value);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SendMessageAsync()
    {
        if (!RecipientId.HasValue || string.IsNullOrWhiteSpace(MessageInput)) return;

        var content = MessageInput;
        MessageInput = string.Empty;

        var result = await _apiClient.SendDirectMessageAsync(RecipientId.Value, content);
        if (result.Success && result.Data is not null)
        {
            Messages.Add(result.Data);
        }
        else
        {
            _onError(result.Error);
            MessageInput = content; // Restore message on failure
        }
    }

    /// <summary>
    /// Closes the DM conversation and clears all state.
    /// </summary>
    public void Close()
    {
        RecipientId = null;
        RecipientName = null;
        MessageInput = string.Empty;
        Messages.Clear();
        EditingMessage = null;
        EditingMessageContent = string.Empty;
        _typingUsers.Clear();
    }

    private void StartEditMessage(DirectMessageResponse message)
    {
        if (message.SenderId != _currentUserId) return;

        EditingMessage = message;
        EditingMessageContent = message.Content;
    }

    private void CancelEditMessage()
    {
        EditingMessage = null;
        EditingMessageContent = string.Empty;
    }

    private async Task SaveMessageEditAsync()
    {
        if (EditingMessage is null || string.IsNullOrWhiteSpace(EditingMessageContent))
            return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.UpdateDirectMessageAsync(EditingMessage.Id, EditingMessageContent.Trim());

            if (result.Success && result.Data is not null)
            {
                var index = Messages.ToList().FindIndex(m => m.Id == EditingMessage.Id);
                if (index >= 0)
                    Messages[index] = result.Data;

                EditingMessage = null;
                EditingMessageContent = string.Empty;
            }
            else
            {
                _onError(result.Error);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DeleteMessageAsync(DirectMessageResponse message)
    {
        IsLoading = true;
        try
        {
            var result = await _apiClient.DeleteDirectMessageAsync(message.Id);

            if (result.Success)
            {
                Messages.Remove(message);
            }
            else
            {
                _onError(result.Error);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SendTypingIndicatorThrottled()
    {
        if (!RecipientId.HasValue) return;
        if (string.IsNullOrEmpty(MessageInput)) return;

        var now = DateTime.UtcNow;
        if ((now - _lastTypingSent).TotalMilliseconds < TypingThrottleMs) return;

        _lastTypingSent = now;
        _ = _signalR.SendDMTypingAsync(RecipientId.Value);
    }
}
