using System.Reactive;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for the thread panel that displays message replies.
/// Handles opening, closing, and updating thread state.
/// Subscribes to SignalR events for real-time thread updates.
/// </summary>
public class ThreadPanelViewModel : ReactiveObject, IDisposable
{
    private readonly IApiClient _apiClient;
    private readonly IMessageStore _messageStore;
    private readonly ISignalRService _signalR;
    private readonly Guid _currentUserId;
    private ThreadViewModel? _currentThread;
    private double _panelWidth = 400;

    public ThreadPanelViewModel(
        IApiClient apiClient,
        IMessageStore messageStore,
        ISignalRService signalR,
        Guid currentUserId)
    {
        _apiClient = apiClient;
        _messageStore = messageStore;
        _signalR = signalR;
        _currentUserId = currentUserId;

        OpenCommand = ReactiveCommand.CreateFromTask<MessageResponse>(OpenAsync);
        CloseCommand = ReactiveCommand.Create(Close);

        // Subscribe to SignalR events for thread updates
        SetupSignalRHandlers();
    }

    private void SetupSignalRHandlers()
    {
        // Message edited - update in thread replies
        _signalR.MessageEdited += message => Dispatcher.UIThread.Post(() =>
        {
            UpdateReply(message);
        });

        // Message deleted - remove from thread
        _signalR.MessageDeleted += e => Dispatcher.UIThread.Post(() =>
        {
            RemoveReply(e.MessageId);
        });

        // Thread reply received - add to thread and update metadata
        _signalR.ThreadReplyReceived += e => Dispatcher.UIThread.Post(() =>
        {
            AddReplyIfMatches(e.ParentMessageId, e.Reply);

            // Update the parent message's reply count in the store
            var existingMessage = _messageStore.GetMessage(e.ParentMessageId);
            if (existingMessage is not null)
            {
                UpdateThreadMetadata(
                    e.ParentMessageId,
                    existingMessage.ReplyCount + 1,
                    e.Reply.CreatedAt);
            }
        });

        // Reaction updated - update in thread replies
        _signalR.ReactionUpdated += e => Dispatcher.UIThread.Post(() =>
        {
            UpdateReplyReaction(
                e.MessageId, e.Emoji, e.Count, e.Added,
                e.UserId, e.Username, e.EffectiveDisplayName, _currentUserId);
        });
    }

    #region Properties

    /// <summary>
    /// The currently open thread view model, or null if no thread is open.
    /// </summary>
    public ThreadViewModel? CurrentThread
    {
        get => _currentThread;
        private set => this.RaiseAndSetIfChanged(ref _currentThread, value);
    }

    /// <summary>
    /// Whether a thread is currently open.
    /// </summary>
    public bool IsOpen => CurrentThread != null;

    /// <summary>
    /// Width of the thread panel.
    /// </summary>
    public double PanelWidth
    {
        get => _panelWidth;
        set => this.RaiseAndSetIfChanged(ref _panelWidth, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Command to open a thread for a parent message.
    /// </summary>
    public ReactiveCommand<MessageResponse, Unit> OpenCommand { get; }

    /// <summary>
    /// Command to close the current thread.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Opens a thread for the given parent message.
    /// </summary>
    public async Task OpenAsync(MessageResponse parentMessage)
    {
        // Close any existing thread
        Close();

        // Create new thread view model
        CurrentThread = new ThreadViewModel(_apiClient, parentMessage, Close);
        await CurrentThread.LoadAsync();

        // Notify that IsOpen changed
        this.RaisePropertyChanged(nameof(IsOpen));
    }

    /// <summary>
    /// Closes the current thread.
    /// </summary>
    public void Close()
    {
        if (CurrentThread != null)
        {
            CurrentThread.Dispose();
            CurrentThread = null;
            this.RaisePropertyChanged(nameof(IsOpen));
        }
    }

    /// <summary>
    /// Updates a reply in the current thread.
    /// Called from SignalR event handler.
    /// </summary>
    public void UpdateReply(MessageResponse message)
    {
        CurrentThread?.UpdateReply(message);
    }

    /// <summary>
    /// Removes a reply from the current thread.
    /// Called from SignalR event handler.
    /// </summary>
    public void RemoveReply(Guid messageId)
    {
        CurrentThread?.RemoveReply(messageId);
    }

    /// <summary>
    /// Adds a reply to the current thread if it matches the parent message.
    /// Called from SignalR event handler.
    /// </summary>
    public void AddReplyIfMatches(Guid parentMessageId, MessageResponse reply)
    {
        if (CurrentThread?.ParentMessage?.Id == parentMessageId)
        {
            CurrentThread.AddReply(reply);
        }
    }

    /// <summary>
    /// Updates reaction on a reply in the current thread.
    /// Called from SignalR event handler.
    /// </summary>
    public void UpdateReplyReaction(
        Guid messageId,
        string emoji,
        int count,
        bool added,
        Guid userId,
        string username,
        string effectiveDisplayName,
        Guid currentUserId)
    {
        CurrentThread?.UpdateReplyReaction(
            messageId, emoji, count, added,
            userId, username, effectiveDisplayName, currentUserId);
    }

    /// <summary>
    /// Updates thread metadata on a parent message when a new reply is added.
    /// Updates the message store.
    /// </summary>
    public void UpdateThreadMetadata(Guid parentMessageId, int replyCount, DateTime? lastReplyAt)
    {
        _messageStore.UpdateThreadMetadata(parentMessageId, replyCount, lastReplyAt);
    }

    #endregion

    public void Dispose()
    {
        CurrentThread?.Dispose();
        OpenCommand.Dispose();
        CloseCommand.Dispose();
    }
}
