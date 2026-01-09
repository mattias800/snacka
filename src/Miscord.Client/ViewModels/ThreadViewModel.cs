using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

/// <summary>
/// ViewModel for managing a thread panel showing replies to a parent message.
/// </summary>
public class ThreadViewModel : ViewModelBase, IDisposable
{
    private readonly IApiClient _apiClient;
    private readonly Action _onClose;

    private MessageResponse? _parentMessage;
    private string _replyInput = string.Empty;
    private bool _isLoading;
    private int _currentPage = 1;
    private int _totalReplyCount;
    private const int PageSize = 50;

    public ThreadViewModel(IApiClient apiClient, MessageResponse parentMessage, Action onClose)
    {
        _apiClient = apiClient;
        _parentMessage = parentMessage;
        _onClose = onClose;

        Replies = new ObservableCollection<MessageResponse>();

        // Commands
        var canSendReply = this.WhenAnyValue(x => x.ReplyInput)
            .Select(input => !string.IsNullOrWhiteSpace(input));

        SendReplyCommand = ReactiveCommand.CreateFromTask(SendReplyAsync, canSendReply);
        CloseCommand = ReactiveCommand.Create(Close);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreAsync);
    }

    public MessageResponse? ParentMessage
    {
        get => _parentMessage;
        private set => this.RaiseAndSetIfChanged(ref _parentMessage, value);
    }

    public ObservableCollection<MessageResponse> Replies { get; }

    public string ReplyInput
    {
        get => _replyInput;
        set => this.RaiseAndSetIfChanged(ref _replyInput, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public int TotalReplyCount
    {
        get => _totalReplyCount;
        private set => this.RaiseAndSetIfChanged(ref _totalReplyCount, value);
    }

    public bool HasMoreReplies => Replies.Count < TotalReplyCount;

    public ReactiveCommand<Unit, Unit> SendReplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    /// <summary>
    /// Loads the thread data from the server.
    /// </summary>
    public async Task LoadAsync()
    {
        if (ParentMessage == null) return;

        try
        {
            IsLoading = true;
            _currentPage = 1;
            Replies.Clear();

            var result = await _apiClient.GetThreadAsync(ParentMessage.Id, _currentPage, PageSize);
            if (result.Success && result.Data != null)
            {
                ParentMessage = result.Data.ParentMessage;
                TotalReplyCount = result.Data.TotalReplyCount;

                foreach (var reply in result.Data.Replies)
                {
                    Replies.Add(reply);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load thread: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Loads more replies (pagination).
    /// </summary>
    private async Task LoadMoreAsync()
    {
        if (ParentMessage == null || !HasMoreReplies || IsLoading) return;

        try
        {
            IsLoading = true;
            _currentPage++;

            var result = await _apiClient.GetThreadAsync(ParentMessage.Id, _currentPage, PageSize);
            if (result.Success && result.Data != null)
            {
                foreach (var reply in result.Data.Replies)
                {
                    Replies.Add(reply);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load more replies: {ex.Message}");
            _currentPage--; // Revert page number on failure
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Sends a new reply to the thread.
    /// </summary>
    private async Task SendReplyAsync()
    {
        if (ParentMessage == null || string.IsNullOrWhiteSpace(ReplyInput)) return;

        try
        {
            var result = await _apiClient.CreateThreadReplyAsync(ParentMessage.Id, ReplyInput);
            if (result.Success && result.Data != null)
            {
                Replies.Add(result.Data);
                TotalReplyCount++;
                ReplyInput = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send thread reply: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds a reply received from SignalR.
    /// </summary>
    public void AddReply(MessageResponse reply)
    {
        // Avoid duplicates
        if (Replies.All(r => r.Id != reply.Id))
        {
            Replies.Add(reply);
            TotalReplyCount++;
        }
    }

    /// <summary>
    /// Updates a reply that was edited.
    /// </summary>
    public void UpdateReply(MessageResponse updatedReply)
    {
        var index = -1;
        for (var i = 0; i < Replies.Count; i++)
        {
            if (Replies[i].Id == updatedReply.Id)
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            Replies[index] = updatedReply;
        }
    }

    /// <summary>
    /// Removes a reply that was deleted.
    /// </summary>
    public void RemoveReply(Guid replyId)
    {
        var reply = Replies.FirstOrDefault(r => r.Id == replyId);
        if (reply != null)
        {
            Replies.Remove(reply);
            TotalReplyCount = Math.Max(0, TotalReplyCount - 1);
        }
    }

    /// <summary>
    /// Closes the thread panel.
    /// </summary>
    private void Close()
    {
        _onClose?.Invoke();
    }

    public void Dispose()
    {
        // Clean up if needed
    }
}
