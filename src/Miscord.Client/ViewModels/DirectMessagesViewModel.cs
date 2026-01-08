using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

public class DirectMessagesViewModel : ViewModelBase, IDisposable
{
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;
    private readonly AuthResponse _auth;
    private readonly Action _onBack;
    private readonly Guid? _initialUserId;
    private readonly string? _initialUsername;

    private ConversationSummary? _selectedConversation;
    private string _messageInput = string.Empty;
    private bool _isLoading;
    private string? _errorMessage;
    private DirectMessageResponse? _editingMessage;
    private string _editingMessageContent = string.Empty;
    private int _firstUnreadIndex = -1;

    public DirectMessagesViewModel(
        IApiClient apiClient,
        ISignalRService signalR,
        AuthResponse auth,
        Action onBack,
        Guid? initialUserId = null,
        string? initialUsername = null)
    {
        _apiClient = apiClient;
        _signalR = signalR;
        _auth = auth;
        _onBack = onBack;
        _initialUserId = initialUserId;
        _initialUsername = initialUsername;

        Conversations = new ObservableCollection<ConversationSummary>();
        Messages = new ObservableCollection<DirectMessageResponse>();

        // Commands
        BackCommand = ReactiveCommand.Create(_onBack);
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadConversationsAsync);
        SelectConversationCommand = ReactiveCommand.Create<ConversationSummary>(conv => SelectedConversation = conv);

        // Message commands
        StartEditMessageCommand = ReactiveCommand.Create<DirectMessageResponse>(StartEditMessage);
        SaveMessageEditCommand = ReactiveCommand.CreateFromTask(SaveMessageEditAsync);
        CancelEditMessageCommand = ReactiveCommand.Create(CancelEditMessage);
        DeleteMessageCommand = ReactiveCommand.CreateFromTask<DirectMessageResponse>(DeleteMessageAsync);

        var canSendMessage = this.WhenAnyValue(
            x => x.MessageInput,
            x => x.SelectedConversation,
            x => x.IsLoading,
            (message, conv, isLoading) =>
                !string.IsNullOrWhiteSpace(message) &&
                conv is not null &&
                !isLoading);

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSendMessage);

        // React to conversation selection changes
        this.WhenAnyValue(x => x.SelectedConversation)
            .Where(c => c is not null)
            .SelectMany(_ => Observable.FromAsync(OnConversationSelectedAsync))
            .Subscribe();

        // Set up SignalR event handlers
        SetupSignalRHandlers();

        // Load conversations on initialization
        Observable.FromAsync(InitializeAsync).Subscribe();
    }

    private async Task InitializeAsync()
    {
        await LoadConversationsAsync();

        // If we have an initial user to message, select or create that conversation
        if (_initialUserId.HasValue && !string.IsNullOrEmpty(_initialUsername))
        {
            var existing = Conversations.FirstOrDefault(c => c.UserId == _initialUserId.Value);
            if (existing is not null)
            {
                SelectedConversation = existing;
            }
            else
            {
                // Create a new conversation entry for this user
                var newConv = new ConversationSummary(
                    _initialUserId.Value,
                    _initialUsername,
                    _initialUsername, // EffectiveDisplayName defaults to username
                    null,
                    true, // Assume online for now
                    null,
                    0
                );
                Conversations.Insert(0, newConv);
                SelectedConversation = newConv;
            }
        }
    }

    private void SetupSignalRHandlers()
    {
        _signalR.DirectMessageReceived += message => Dispatcher.UIThread.Post(() =>
        {
            // If this message is from/to the current conversation partner
            if (SelectedConversation is not null &&
                (message.SenderId == SelectedConversation.UserId || message.RecipientId == SelectedConversation.UserId))
            {
                if (!Messages.Any(m => m.Id == message.Id))
                    Messages.Add(message);
            }

            // Update conversation list
            UpdateConversationWithMessage(message);
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
    }

    private void UpdateConversationWithMessage(DirectMessageResponse message)
    {
        var otherUserId = message.SenderId == _auth.UserId ? message.RecipientId : message.SenderId;
        var existing = Conversations.FirstOrDefault(c => c.UserId == otherUserId);

        if (existing is not null)
        {
            var index = Conversations.IndexOf(existing);
            var updated = existing with
            {
                LastMessage = message,
                UnreadCount = message.SenderId != _auth.UserId && !message.IsRead
                    ? existing.UnreadCount + 1
                    : existing.UnreadCount
            };
            Conversations.RemoveAt(index);
            Conversations.Insert(0, updated); // Move to top
        }
        else
        {
            // New conversation
            var otherUsername = message.SenderId == _auth.UserId ? message.RecipientUsername : message.SenderUsername;
            var otherEffectiveDisplayName = message.SenderId == _auth.UserId
                ? message.RecipientEffectiveDisplayName
                : message.SenderEffectiveDisplayName;
            var newConv = new ConversationSummary(
                otherUserId,
                otherUsername,
                otherEffectiveDisplayName,
                null,
                true,
                message,
                message.SenderId != _auth.UserId ? 1 : 0
            );
            Conversations.Insert(0, newConv);
        }
    }

    private async Task OnConversationSelectedAsync()
    {
        if (SelectedConversation is null) return;

        // Remember the unread count before clearing
        var unreadCount = SelectedConversation.UnreadCount;

        await LoadMessagesAsync(unreadCount);
        await _apiClient.MarkConversationAsReadAsync(SelectedConversation.UserId);

        // Clear the unread count in the conversation list
        var index = Conversations.IndexOf(SelectedConversation);
        if (index >= 0 && SelectedConversation.UnreadCount > 0)
        {
            Conversations[index] = SelectedConversation with { UnreadCount = 0 };
            SelectedConversation = Conversations[index];
        }
    }

    public string Username => _auth.Username;
    public Guid UserId => _auth.UserId;

    public ObservableCollection<ConversationSummary> Conversations { get; }
    public ObservableCollection<DirectMessageResponse> Messages { get; }

    public ConversationSummary? SelectedConversation
    {
        get => _selectedConversation;
        set => this.RaiseAndSetIfChanged(ref _selectedConversation, value);
    }

    public string MessageInput
    {
        get => _messageInput;
        set => this.RaiseAndSetIfChanged(ref _messageInput, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public DirectMessageResponse? EditingMessage
    {
        get => _editingMessage;
        set => this.RaiseAndSetIfChanged(ref _editingMessage, value);
    }

    public string EditingMessageContent
    {
        get => _editingMessageContent;
        set => this.RaiseAndSetIfChanged(ref _editingMessageContent, value);
    }

    public int FirstUnreadIndex
    {
        get => _firstUnreadIndex;
        set => this.RaiseAndSetIfChanged(ref _firstUnreadIndex, value);
    }

    /// <summary>
    /// Returns true if the message at the given index is the first unread message.
    /// Used by the view to show the "Unread" separator.
    /// </summary>
    public bool IsFirstUnreadMessage(DirectMessageResponse message)
    {
        if (FirstUnreadIndex < 0 || FirstUnreadIndex >= Messages.Count)
            return false;
        return Messages.IndexOf(message) == FirstUnreadIndex;
    }

    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<ConversationSummary, Unit> SelectConversationCommand { get; }
    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<DirectMessageResponse, Unit> StartEditMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveMessageEditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditMessageCommand { get; }
    public ReactiveCommand<DirectMessageResponse, Unit> DeleteMessageCommand { get; }

    private async Task LoadConversationsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var result = await _apiClient.GetConversationsAsync();
            if (result.Success && result.Data is not null)
            {
                Conversations.Clear();
                foreach (var conv in result.Data)
                    Conversations.Add(conv);
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadMessagesAsync(int unreadCount = 0)
    {
        if (SelectedConversation is null) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.GetDirectMessagesAsync(SelectedConversation.UserId);
            if (result.Success && result.Data is not null)
            {
                Messages.Clear();

                // Calculate the index where unread messages start
                // Messages are in chronological order, unread ones are at the end
                var totalCount = result.Data.Count;
                FirstUnreadIndex = unreadCount > 0 ? totalCount - unreadCount : -1;

                foreach (var message in result.Data)
                    Messages.Add(message);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SendMessageAsync()
    {
        if (SelectedConversation is null || string.IsNullOrWhiteSpace(MessageInput)) return;

        var content = MessageInput;
        MessageInput = string.Empty;

        var result = await _apiClient.SendDirectMessageAsync(SelectedConversation.UserId, content);
        if (result.Success && result.Data is not null)
        {
            Messages.Add(result.Data);
            UpdateConversationWithMessage(result.Data);
        }
        else
        {
            ErrorMessage = result.Error;
            MessageInput = content; // Restore message on failure
        }
    }

    private void StartEditMessage(DirectMessageResponse message)
    {
        if (message.SenderId != UserId) return;

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
                ErrorMessage = result.Error;
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
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        // Unsubscribe from events if needed
    }
}
