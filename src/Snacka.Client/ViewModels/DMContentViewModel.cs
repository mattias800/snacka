using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Snacka.Client.Services;
using ReactiveUI;
using ConversationParticipantInfo = Snacka.Client.Services.ParticipantInfo;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for inline DM content displayed within the main app.
/// Supports both 1:1 and group conversations.
/// </summary>
public class DMContentViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;
    private readonly Guid _currentUserId;
    private readonly Action<string?> _onError;

    private bool _isLoading;
    private Guid? _conversationId;
    private string? _conversationDisplayName;
    private bool _isGroup;
    private string _messageInput = string.Empty;
    private ConversationMessageResponse? _editingMessage;
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

        Messages = new ObservableCollection<ConversationMessageResponse>();
        Participants = new ObservableCollection<ConversationParticipantInfo>();

        // Commands
        var canSendMessage = this.WhenAnyValue(
            x => x.MessageInput,
            x => x.ConversationId,
            x => x.IsLoading,
            (message, conversationId, isLoading) =>
                !string.IsNullOrWhiteSpace(message) &&
                conversationId.HasValue &&
                !isLoading);

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSendMessage);
        CloseCommand = ReactiveCommand.Create(Close);
        StartEditMessageCommand = ReactiveCommand.Create<ConversationMessageResponse>(StartEditMessage);
        SaveMessageEditCommand = ReactiveCommand.CreateFromTask(SaveMessageEditAsync);
        CancelEditMessageCommand = ReactiveCommand.Create(CancelEditMessage);
        DeleteMessageCommand = ReactiveCommand.CreateFromTask<ConversationMessageResponse>(DeleteMessageAsync);
        AddParticipantCommand = ReactiveCommand.CreateFromTask<Guid>(AddParticipantAsync);
        RemoveParticipantCommand = ReactiveCommand.CreateFromTask<Guid>(RemoveParticipantAsync);
        LeaveConversationCommand = ReactiveCommand.CreateFromTask(LeaveConversationAsync);

        // Setup SignalR handlers
        SetupSignalRHandlers();
    }

    private void SetupSignalRHandlers()
    {
        // New conversation message events
        _signalR.ConversationMessageReceived += message => Dispatcher.UIThread.Post(() =>
        {
            // Clear typing indicator for this user since they sent a message
            var typingUser = _typingUsers.FirstOrDefault(t => t.UserId == message.SenderId);
            if (typingUser != null)
            {
                _typingUsers.Remove(typingUser);
                this.RaisePropertyChanged(nameof(TypingIndicatorText));
                this.RaisePropertyChanged(nameof(IsTyping));
            }

            // If this message is for the current conversation
            if (ConversationId.HasValue && message.ConversationId == ConversationId.Value)
            {
                if (!Messages.Any(m => m.Id == message.Id))
                    Messages.Add(message);
            }
        });

        _signalR.ConversationMessageUpdated += message => Dispatcher.UIThread.Post(() =>
        {
            if (ConversationId.HasValue && message.ConversationId == ConversationId.Value)
            {
                var index = Messages.ToList().FindIndex(m => m.Id == message.Id);
                if (index >= 0)
                    Messages[index] = message;
            }
        });

        _signalR.ConversationMessageDeleted += e => Dispatcher.UIThread.Post(() =>
        {
            if (ConversationId.HasValue && e.ConversationId == ConversationId.Value)
            {
                var message = Messages.FirstOrDefault(m => m.Id == e.MessageId);
                if (message is not null)
                    Messages.Remove(message);
            }
        });

        _signalR.ConversationUserTyping += e => Dispatcher.UIThread.Post(() =>
        {
            // Only show typing for the current conversation
            if (ConversationId.HasValue && e.ConversationId == ConversationId.Value && e.UserId != _currentUserId)
            {
                var existing = _typingUsers.FirstOrDefault(t => t.UserId == e.UserId);
                if (existing != null)
                    _typingUsers.Remove(existing);
                _typingUsers.Add(new TypingUser(e.UserId, e.Username, DateTime.UtcNow));
                this.RaisePropertyChanged(nameof(TypingIndicatorText));
                this.RaisePropertyChanged(nameof(IsTyping));
            }
        });

        _signalR.ConversationParticipantAdded += e => Dispatcher.UIThread.Post(() =>
        {
            if (ConversationId.HasValue && e.ConversationId == ConversationId.Value)
            {
                if (!Participants.Any(p => p.UserId == e.Participant.UserId))
                {
                    Participants.Add(e.Participant);
                    UpdateConversationDisplayName();
                }
            }
        });

        _signalR.ConversationParticipantRemoved += e => Dispatcher.UIThread.Post(() =>
        {
            if (ConversationId.HasValue && e.ConversationId == ConversationId.Value)
            {
                var participant = Participants.FirstOrDefault(p => p.UserId == e.UserId);
                if (participant != null)
                {
                    Participants.Remove(participant);
                    UpdateConversationDisplayName();
                }

                // If the current user was removed, close the conversation
                if (e.UserId == _currentUserId)
                {
                    Close();
                }
            }
        });

        _signalR.ConversationUpdated += conversation => Dispatcher.UIThread.Post(() =>
        {
            if (ConversationId.HasValue && conversation.Id == ConversationId.Value)
            {
                ConversationDisplayName = GetDisplayNameForConversation(conversation);
                IsGroup = conversation.IsGroup;
            }
        });

        _signalR.AddedToConversation += conversationId => Dispatcher.UIThread.Post(async () =>
        {
            // Join the SignalR group for this conversation
            await _signalR.JoinConversationGroupAsync(conversationId);
        });

        _signalR.RemovedFromConversation += conversationId => Dispatcher.UIThread.Post(async () =>
        {
            // Leave the SignalR group for this conversation
            await _signalR.LeaveConversationGroupAsync(conversationId);

            // If this is the current conversation, close it
            if (ConversationId.HasValue && ConversationId.Value == conversationId)
            {
                Close();
            }
        });
    }

    /// <summary>
    /// Opens a conversation by ID.
    /// </summary>
    public async Task OpenConversationByIdAsync(Guid conversationId)
    {
        ConversationId = conversationId;
        MessageInput = string.Empty;
        EditingMessage = null;
        EditingMessageContent = string.Empty;

        await LoadConversationAsync();
    }

    /// <summary>
    /// Opens a conversation by ID (synchronous wrapper).
    /// </summary>
    public void OpenConversationById(Guid conversationId, string displayName)
    {
        ConversationDisplayName = displayName;
        _ = OpenConversationByIdAsync(conversationId);
    }

    /// <summary>
    /// Opens or creates a 1:1 DM conversation with the specified user.
    /// </summary>
    public async Task OpenDirectConversationAsync(Guid userId, string username)
    {
        // Don't DM yourself
        if (userId == _currentUserId) return;

        MessageInput = string.Empty;
        EditingMessage = null;
        EditingMessageContent = string.Empty;

        // Get or create the conversation
        var result = await _apiClient.GetOrCreateDirectConversationAsync(userId);
        if (result.Success && result.Data is not null)
        {
            ConversationId = result.Data.Id;
            ConversationDisplayName = GetDisplayNameForConversation(result.Data);
            IsGroup = result.Data.IsGroup;

            Participants.Clear();
            foreach (var participant in result.Data.Participants)
            {
                Participants.Add(participant);
            }

            await LoadMessagesAsync();
        }
        else
        {
            _onError(result.Error);
        }
    }

    /// <summary>
    /// Opens a DM conversation with the specified user (legacy compatibility).
    /// </summary>
    public void OpenConversation(Guid userId, string username)
    {
        _ = OpenDirectConversationAsync(userId, username);
    }

    /// <summary>
    /// Creates a new group conversation with the specified users.
    /// </summary>
    public async Task<bool> CreateGroupConversationAsync(List<Guid> participantIds, string? name)
    {
        var result = await _apiClient.CreateConversationAsync(participantIds, name);
        if (result.Success && result.Data is not null)
        {
            // Join the SignalR group
            await _signalR.JoinConversationGroupAsync(result.Data.Id);

            // Open the new conversation
            await OpenConversationByIdAsync(result.Data.Id);
            return true;
        }
        else
        {
            _onError(result.Error);
            return false;
        }
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
    public ObservableCollection<ConversationMessageResponse> Messages { get; }
    public ObservableCollection<ConversationParticipantInfo> Participants { get; }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public Guid? ConversationId
    {
        get => _conversationId;
        private set
        {
            this.RaiseAndSetIfChanged(ref _conversationId, value);
            this.RaisePropertyChanged(nameof(IsOpen));
        }
    }

    public string? ConversationDisplayName
    {
        get => _conversationDisplayName;
        private set => this.RaiseAndSetIfChanged(ref _conversationDisplayName, value);
    }

    public bool IsGroup
    {
        get => _isGroup;
        private set => this.RaiseAndSetIfChanged(ref _isGroup, value);
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

    public ConversationMessageResponse? EditingMessage
    {
        get => _editingMessage;
        private set => this.RaiseAndSetIfChanged(ref _editingMessage, value);
    }

    public string EditingMessageContent
    {
        get => _editingMessageContent;
        set => this.RaiseAndSetIfChanged(ref _editingMessageContent, value);
    }

    public bool IsOpen => ConversationId.HasValue;

    public bool IsTyping => _typingUsers.Count > 0;

    public string TypingIndicatorText
    {
        get
        {
            if (_typingUsers.Count == 0) return string.Empty;
            if (_typingUsers.Count == 1) return $"{_typingUsers[0].Username} is typing...";
            if (_typingUsers.Count == 2) return $"{_typingUsers[0].Username} and {_typingUsers[1].Username} are typing...";
            return $"{_typingUsers[0].Username} and {_typingUsers.Count - 1} others are typing...";
        }
    }

    public Guid CurrentUserId => _currentUserId;

    // Commands
    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<ConversationMessageResponse, Unit> StartEditMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveMessageEditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditMessageCommand { get; }
    public ReactiveCommand<ConversationMessageResponse, Unit> DeleteMessageCommand { get; }
    public ReactiveCommand<Guid, Unit> AddParticipantCommand { get; }
    public ReactiveCommand<Guid, Unit> RemoveParticipantCommand { get; }
    public ReactiveCommand<Unit, Unit> LeaveConversationCommand { get; }

    // Private methods
    private async Task LoadConversationAsync()
    {
        if (!ConversationId.HasValue) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.GetConversationAsync(ConversationId.Value);
            if (result.Success && result.Data is not null)
            {
                ConversationDisplayName = GetDisplayNameForConversation(result.Data);
                IsGroup = result.Data.IsGroup;

                Participants.Clear();
                foreach (var participant in result.Data.Participants)
                {
                    Participants.Add(participant);
                }

                await LoadMessagesAsync();
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

    private async Task LoadMessagesAsync()
    {
        if (!ConversationId.HasValue) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.GetConversationMessagesAsync(ConversationId.Value);
            if (result.Success && result.Data is not null)
            {
                Messages.Clear();
                foreach (var message in result.Data)
                    Messages.Add(message);
            }

            // Mark conversation as read
            await _apiClient.MarkConversationReadByIdAsync(ConversationId.Value);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageInput) || !ConversationId.HasValue) return;

        var content = MessageInput;
        MessageInput = string.Empty;

        var result = await _apiClient.SendConversationMessageAsync(ConversationId.Value, content);
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
        // Leave the conversation SignalR group if we were in one
        if (ConversationId.HasValue)
        {
            var convId = ConversationId.Value;
            _ = _signalR.LeaveConversationGroupAsync(convId);
        }

        ConversationId = null;
        ConversationDisplayName = null;
        IsGroup = false;
        MessageInput = string.Empty;
        Messages.Clear();
        Participants.Clear();
        EditingMessage = null;
        EditingMessageContent = string.Empty;
        _typingUsers.Clear();
    }

    private void StartEditMessage(ConversationMessageResponse message)
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

        if (!ConversationId.HasValue) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.UpdateConversationMessageAsync(
                ConversationId.Value, EditingMessage.Id, EditingMessageContent.Trim());

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

    private async Task DeleteMessageAsync(ConversationMessageResponse message)
    {
        if (!ConversationId.HasValue) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.DeleteConversationMessageAsync(ConversationId.Value, message.Id);

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

    private async Task AddParticipantAsync(Guid userId)
    {
        if (!ConversationId.HasValue || !IsGroup) return;

        var result = await _apiClient.AddConversationParticipantAsync(ConversationId.Value, userId);
        if (!result.Success)
        {
            _onError(result.Error);
        }
        // Participant will be added via SignalR event
    }

    private async Task RemoveParticipantAsync(Guid userId)
    {
        if (!ConversationId.HasValue || !IsGroup) return;

        var result = await _apiClient.RemoveConversationParticipantAsync(ConversationId.Value, userId);
        if (!result.Success)
        {
            _onError(result.Error);
        }
        // Participant will be removed via SignalR event
    }

    private async Task LeaveConversationAsync()
    {
        if (!ConversationId.HasValue || !IsGroup) return;

        var result = await _apiClient.RemoveConversationParticipantAsync(ConversationId.Value, _currentUserId);
        if (result.Success)
        {
            Close();
        }
        else
        {
            _onError(result.Error);
        }
    }

    private void SendTypingIndicatorThrottled()
    {
        if (string.IsNullOrEmpty(MessageInput) || !ConversationId.HasValue) return;

        var now = DateTime.UtcNow;
        if ((now - _lastTypingSent).TotalMilliseconds < TypingThrottleMs) return;

        _lastTypingSent = now;
        _ = _signalR.SendConversationTypingAsync(ConversationId.Value);
    }

    private string GetDisplayNameForConversation(ConversationResponse conversation)
    {
        if (!string.IsNullOrEmpty(conversation.Name))
            return conversation.Name;

        // For 1:1 conversations, show the other user's name
        var otherParticipants = conversation.Participants
            .Where(p => p.UserId != _currentUserId)
            .ToList();

        if (otherParticipants.Count == 0)
            return "Empty Conversation";
        if (otherParticipants.Count == 1)
            return otherParticipants[0].EffectiveDisplayName;
        if (otherParticipants.Count == 2)
            return $"{otherParticipants[0].EffectiveDisplayName}, {otherParticipants[1].EffectiveDisplayName}";

        return $"{otherParticipants[0].EffectiveDisplayName} and {otherParticipants.Count - 1} others";
    }

    private void UpdateConversationDisplayName()
    {
        var otherParticipants = Participants
            .Where(p => p.UserId != _currentUserId)
            .ToList();

        if (otherParticipants.Count == 0)
            ConversationDisplayName = "Empty Conversation";
        else if (otherParticipants.Count == 1)
            ConversationDisplayName = otherParticipants[0].EffectiveDisplayName;
        else if (otherParticipants.Count == 2)
            ConversationDisplayName = $"{otherParticipants[0].EffectiveDisplayName}, {otherParticipants[1].EffectiveDisplayName}";
        else
            ConversationDisplayName = $"{otherParticipants[0].EffectiveDisplayName} and {otherParticipants.Count - 1} others";
    }
}
