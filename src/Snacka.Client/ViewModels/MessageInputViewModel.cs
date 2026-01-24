using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Snacka.Client.Models;
using Snacka.Client.Services;
using Snacka.Client.Services.Autocomplete;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for message input, editing, reply, and autocomplete functionality.
/// Encapsulates all message composition state and commands.
/// Reads current channel from ChannelStore (Redux-style).
/// </summary>
public class MessageInputViewModel : ReactiveObject, IDisposable
{
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;
    private readonly IMessageStore _messageStore;
    private readonly IChannelStore _channelStore;
    private readonly AutocompleteManager _autocomplete = new();
    private readonly Guid _userId;

    private string _messageInput = string.Empty;
    private MessageResponse? _editingMessage;
    private string _editingMessageContent = string.Empty;
    private MessageResponse? _replyingToMessage;
    private bool _isSelectingAutocomplete;
    private DateTime _lastTypingSent = DateTime.MinValue;
    private const int TypingThrottleMs = 3000;
    private bool _isLoading;

    private readonly ObservableCollection<PendingAttachment> _pendingAttachments = new();

    /// <summary>
    /// Raised when an error occurs during message operations.
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Raised when a GIF preview is requested (e.g., /gif command).
    /// Parameter is the search query.
    /// </summary>
    public event Func<string, Task>? GifPreviewRequested;

    /// <summary>
    /// Creates a new MessageInputViewModel.
    /// Reads current channel from ChannelStore (Redux-style).
    /// </summary>
    public MessageInputViewModel(
        IApiClient apiClient,
        ISignalRService signalR,
        IMessageStore messageStore,
        IChannelStore channelStore,
        Guid userId,
        Func<IEnumerable<CommunityMemberResponse>> getMembers,
        bool gifsEnabled = false)
    {
        _apiClient = apiClient;
        _signalR = signalR;
        _messageStore = messageStore;
        _channelStore = channelStore;
        _userId = userId;

        // Register autocomplete sources
        _autocomplete.RegisterSource(new MentionAutocompleteSource(getMembers, userId));
        _autocomplete.RegisterSource(new SlashCommandAutocompleteSource(gifsEnabled: gifsEnabled));
        _autocomplete.RegisterSource(new EmojiAutocompleteSource());

        // Forward autocomplete property changes
        _autocomplete.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AutocompleteManager.IsPopupOpen))
                this.RaisePropertyChanged(nameof(IsAutocompletePopupOpen));
            else if (e.PropertyName == nameof(AutocompleteManager.SelectedIndex))
                this.RaisePropertyChanged(nameof(SelectedAutocompleteIndex));
        };

        // Create commands
        var canSendMessage = this.WhenAnyValue(
            x => x.MessageInput,
            x => x.HasPendingAttachments,
            (input, hasAttachments) => !string.IsNullOrWhiteSpace(input) || hasAttachments);

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSendMessage);
        StartEditMessageCommand = ReactiveCommand.Create<MessageResponse>(StartEditMessage);
        SaveMessageEditCommand = ReactiveCommand.CreateFromTask(SaveMessageEditAsync);
        CancelEditMessageCommand = ReactiveCommand.Create(CancelEditMessage);
        DeleteMessageCommand = ReactiveCommand.CreateFromTask<MessageResponse>(DeleteMessageAsync);
        ReplyToMessageCommand = ReactiveCommand.Create<MessageResponse>(StartReplyToMessage);
        CancelReplyCommand = ReactiveCommand.Create(CancelReply);
    }

    #region Properties

    /// <summary>
    /// The current message input text.
    /// </summary>
    public string MessageInput
    {
        get => _messageInput;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageInput, value);

            // Handle unified autocomplete (@ mentions and / commands)
            // Skip during autocomplete selection to avoid interfering with caret positioning
            if (!_isSelectingAutocomplete)
            {
                _autocomplete.HandleTextChange(value);
            }

            // Send typing indicator (throttled)
            var channelId = _channelStore.GetSelectedChannelId();
            if (!string.IsNullOrEmpty(value) && channelId is not null)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastTypingSent).TotalMilliseconds > TypingThrottleMs)
                {
                    _lastTypingSent = now;
                    _ = _signalR.SendTypingAsync(channelId.Value);
                }
            }
        }
    }

    /// <summary>
    /// The message currently being edited (null if not editing).
    /// </summary>
    public MessageResponse? EditingMessage
    {
        get => _editingMessage;
        private set => this.RaiseAndSetIfChanged(ref _editingMessage, value);
    }

    /// <summary>
    /// The content of the message being edited.
    /// </summary>
    public string EditingMessageContent
    {
        get => _editingMessageContent;
        set => this.RaiseAndSetIfChanged(ref _editingMessageContent, value);
    }

    /// <summary>
    /// The message being replied to (null if not replying).
    /// </summary>
    public MessageResponse? ReplyingToMessage
    {
        get => _replyingToMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _replyingToMessage, value);
            this.RaisePropertyChanged(nameof(IsReplying));
        }
    }

    /// <summary>
    /// Whether currently replying to a message.
    /// </summary>
    public bool IsReplying => ReplyingToMessage is not null;

    /// <summary>
    /// Whether a message operation is in progress.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>
    /// Pending file attachments.
    /// </summary>
    public ObservableCollection<PendingAttachment> PendingAttachments => _pendingAttachments;

    /// <summary>
    /// Whether there are pending attachments.
    /// </summary>
    public bool HasPendingAttachments => _pendingAttachments.Count > 0;

    /// <summary>
    /// Whether the autocomplete popup is open.
    /// </summary>
    public bool IsAutocompletePopupOpen => _autocomplete.IsPopupOpen;

    /// <summary>
    /// Autocomplete suggestions.
    /// </summary>
    public ObservableCollection<IAutocompleteSuggestion> AutocompleteSuggestions => _autocomplete.Suggestions;

    /// <summary>
    /// Currently selected autocomplete suggestion index.
    /// </summary>
    public int SelectedAutocompleteIndex
    {
        get => _autocomplete.SelectedIndex;
        set => _autocomplete.SelectedIndex = value;
    }

    #endregion

    #region Commands

    /// <summary>
    /// Command to send the current message.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }

    /// <summary>
    /// Command to start editing a message.
    /// </summary>
    public ReactiveCommand<MessageResponse, Unit> StartEditMessageCommand { get; }

    /// <summary>
    /// Command to save the message edit.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveMessageEditCommand { get; }

    /// <summary>
    /// Command to cancel message editing.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelEditMessageCommand { get; }

    /// <summary>
    /// Command to delete a message.
    /// </summary>
    public ReactiveCommand<MessageResponse, Unit> DeleteMessageCommand { get; }

    /// <summary>
    /// Command to start replying to a message.
    /// </summary>
    public ReactiveCommand<MessageResponse, Unit> ReplyToMessageCommand { get; }

    /// <summary>
    /// Command to cancel replying.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelReplyCommand { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Sends the current message.
    /// </summary>
    public async Task SendMessageAsync()
    {
        var channelId = _channelStore.GetSelectedChannelId();
        if (channelId is null) return;

        // Allow empty content if there are attachments
        if (string.IsNullOrWhiteSpace(MessageInput) && !HasPendingAttachments) return;

        var content = MessageInput.Trim();

        // Check for /gif or /giphy command
        if (content.StartsWith("/gif ", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("/giphy ", StringComparison.OrdinalIgnoreCase))
        {
            // Extract search query (everything after the command)
            var spaceIndex = content.IndexOf(' ');
            var query = content.Substring(spaceIndex + 1).Trim();

            if (!string.IsNullOrWhiteSpace(query))
            {
                MessageInput = string.Empty;
                if (GifPreviewRequested != null)
                {
                    await GifPreviewRequested(query);
                }
                return;
            }
        }

        // Process other slash commands
        content = SlashCommandRegistry.ProcessContent(content);

        // If content became empty after processing (e.g., just "/shrug" with no other text), that's fine
        // But if there's nothing to send, return
        if (string.IsNullOrWhiteSpace(content) && !HasPendingAttachments) return;

        var replyToId = ReplyingToMessage?.Id;
        MessageInput = string.Empty;
        ReplyingToMessage = null;

        ApiResult<MessageResponse> result;

        if (HasPendingAttachments)
        {
            // Send with attachments
            var files = _pendingAttachments.Select(a => new FileAttachment
            {
                FileName = a.FileName,
                Stream = a.Stream,
                ContentType = a.ContentType
            }).ToList();

            result = await _apiClient.SendMessageWithAttachmentsAsync(channelId.Value, content, replyToId, files);

            // Clear pending attachments (streams are now consumed)
            _pendingAttachments.Clear();
            this.RaisePropertyChanged(nameof(HasPendingAttachments));
        }
        else
        {
            // Send text-only message
            result = await _apiClient.SendMessageAsync(channelId.Value, content, replyToId);
        }

        if (result.Success && result.Data is not null)
        {
            _messageStore.AddMessage(result.Data);
        }
        else
        {
            ErrorOccurred?.Invoke(result.Error ?? "Failed to send message");
            MessageInput = content; // Restore message on failure
        }
    }

    /// <summary>
    /// Starts editing a message.
    /// </summary>
    public void StartEditMessage(MessageResponse message)
    {
        // Only allow editing own messages
        if (message.AuthorId != _userId) return;

        EditingMessage = message;
        EditingMessageContent = message.Content;
    }

    /// <summary>
    /// Cancels message editing.
    /// </summary>
    public void CancelEditMessage()
    {
        EditingMessage = null;
        EditingMessageContent = string.Empty;
    }

    /// <summary>
    /// Saves the current message edit.
    /// </summary>
    public async Task SaveMessageEditAsync()
    {
        var channelId = _channelStore.GetSelectedChannelId();
        if (EditingMessage is null || channelId is null || string.IsNullOrWhiteSpace(EditingMessageContent))
            return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.UpdateMessageAsync(channelId.Value, EditingMessage.Id, EditingMessageContent.Trim());

            if (result.Success && result.Data is not null)
            {
                _messageStore.UpdateMessage(result.Data);
                EditingMessage = null;
                EditingMessageContent = string.Empty;
            }
            else
            {
                ErrorOccurred?.Invoke(result.Error ?? "Failed to update message");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Deletes a message.
    /// </summary>
    public async Task DeleteMessageAsync(MessageResponse message)
    {
        var channelId = _channelStore.GetSelectedChannelId();
        if (channelId is null) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.DeleteMessageAsync(channelId.Value, message.Id);

            if (result.Success)
            {
                _messageStore.DeleteMessage(message.Id);
            }
            else
            {
                ErrorOccurred?.Invoke(result.Error ?? "Failed to delete message");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Starts replying to a message.
    /// </summary>
    public void StartReplyToMessage(MessageResponse message)
    {
        ReplyingToMessage = message;
    }

    /// <summary>
    /// Cancels the current reply.
    /// </summary>
    public void CancelReply()
    {
        ReplyingToMessage = null;
    }

    /// <summary>
    /// Adds a pending attachment.
    /// </summary>
    public void AddPendingAttachment(string fileName, Stream stream, long size, string contentType)
    {
        _pendingAttachments.Add(new PendingAttachment
        {
            FileName = fileName,
            Stream = stream,
            Size = size,
            ContentType = contentType
        });
        this.RaisePropertyChanged(nameof(HasPendingAttachments));
    }

    /// <summary>
    /// Removes a pending attachment.
    /// </summary>
    public void RemovePendingAttachment(PendingAttachment attachment)
    {
        attachment.Stream.Dispose();
        _pendingAttachments.Remove(attachment);
        this.RaisePropertyChanged(nameof(HasPendingAttachments));
    }

    /// <summary>
    /// Clears all pending attachments.
    /// </summary>
    public void ClearPendingAttachments()
    {
        foreach (var attachment in _pendingAttachments)
        {
            attachment.Stream.Dispose();
        }
        _pendingAttachments.Clear();
        this.RaisePropertyChanged(nameof(HasPendingAttachments));
    }

    #endregion

    #region Autocomplete Methods

    /// <summary>
    /// Closes the autocomplete popup.
    /// </summary>
    public void CloseAutocompletePopup()
    {
        _autocomplete.Close();
    }

    /// <summary>
    /// Selects a suggestion and returns the cursor position where the caret should be placed.
    /// Returns -1 if no suggestion was inserted (e.g., command was executed).
    /// </summary>
    public int SelectAutocompleteSuggestion(IAutocompleteSuggestion suggestion)
    {
        var result = _autocomplete.Select(suggestion, MessageInput);
        if (result.HasValue)
        {
            _isSelectingAutocomplete = true;
            try
            {
                MessageInput = result.Value.newText;
                return result.Value.cursorPosition;
            }
            finally
            {
                _isSelectingAutocomplete = false;
            }
        }
        return -1;
    }

    /// <summary>
    /// Selects a suggestion and returns both the new text and cursor position.
    /// The caller is responsible for updating the UI directly.
    /// </summary>
    public (string newText, int cursorPosition)? SelectAutocompleteSuggestionWithText(IAutocompleteSuggestion suggestion)
    {
        return _autocomplete.Select(suggestion, MessageInput);
    }

    /// <summary>
    /// Selects the currently highlighted suggestion and returns the cursor position.
    /// Returns -1 if no suggestion was selected.
    /// </summary>
    public int SelectCurrentAutocompleteSuggestion()
    {
        var result = _autocomplete.SelectCurrent(MessageInput);
        if (result.HasValue)
        {
            _isSelectingAutocomplete = true;
            try
            {
                MessageInput = result.Value.newText;
                return result.Value.cursorPosition;
            }
            finally
            {
                _isSelectingAutocomplete = false;
            }
        }
        return -1;
    }

    /// <summary>
    /// Selects the currently highlighted suggestion and returns both the new text and cursor position.
    /// The caller is responsible for updating the UI directly (setting TextBox.Text).
    /// The TwoWay binding will push the value back to MessageInput.
    /// </summary>
    public (string newText, int cursorPosition)? SelectCurrentAutocompleteSuggestionWithText()
    {
        return _autocomplete.SelectCurrent(MessageInput);
    }

    /// <summary>
    /// Navigates to the previous autocomplete suggestion.
    /// </summary>
    public void NavigateAutocompleteUp()
    {
        _autocomplete.NavigateUp();
    }

    /// <summary>
    /// Navigates to the next autocomplete suggestion.
    /// </summary>
    public void NavigateAutocompleteDown()
    {
        _autocomplete.NavigateDown();
    }

    #endregion

    public void Dispose()
    {
        ClearPendingAttachments();
        SendMessageCommand.Dispose();
        StartEditMessageCommand.Dispose();
        SaveMessageEditCommand.Dispose();
        CancelEditMessageCommand.Dispose();
        DeleteMessageCommand.Dispose();
        ReplyToMessageCommand.Dispose();
        CancelReplyCommand.Dispose();
    }
}
