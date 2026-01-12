# Snacka Threads Implementation Plan

## Overview
Add Slack-style message threads to Snacka, allowing users to have organized sub-conversations within channels. Thread replies are regular messages with a parent reference, reusing all existing message infrastructure.

## Core Design Principle

**A thread reply IS a message.** Instead of creating separate entities, we extend the existing `Message` entity with two optional parent references. This means:
- All existing message features (edit, delete, reactions) work automatically for thread replies
- No code duplication
- Consistent behavior across channel messages and thread replies
- Simpler maintenance

---

## Database Schema Changes

### Modified Entity: Message

Add four fields to the existing `Message` entity:

```csharp
Message
├─ ... (existing fields: Id, Content, AuthorId, ChannelId, CreatedAt, UpdatedAt, Reactions, etc.)
├─ ThreadParentMessageId (FK → Message, nullable)  // Which thread this belongs to (the thread starter)
├─ ReplyParentMessageId (FK → Message, nullable)   // Which message this is replying to (for reply preview)
├─ ReplyCount (int, default 0)                     // Cached count of thread replies (only on thread starters)
├─ LastReplyAt (DateTime?, nullable)               // Timestamp of most recent reply (only on thread starters)
```

**Two Parent References:**

| Field | Purpose | When Set |
|-------|---------|----------|
| `ThreadParentMessageId` | Identifies which thread this message belongs to | Always set for messages inside a thread |
| `ReplyParentMessageId` | Shows "replying to X" preview above message | Optional - only when user explicitly replies to a specific message |

**Examples:**

```
Channel message (not in thread):
  ThreadParentMessageId = null
  ReplyParentMessageId = null

First reply in a thread (to message A):
  ThreadParentMessageId = A
  ReplyParentMessageId = null  (or A, if we want to show reply preview)

Reply to message B inside thread started by A:
  ThreadParentMessageId = A    (still belongs to thread A)
  ReplyParentMessageId = B     (shows "replying to B" preview)

Channel message replying to another channel message C:
  ThreadParentMessageId = null (not in a thread)
  ReplyParentMessageId = C     (shows "replying to C" preview)
```

**Logic:**
- `ThreadParentMessageId == null` → Channel message (top-level)
- `ThreadParentMessageId != null` → Thread message
- `ReplyParentMessageId != null` → Show reply preview regardless of thread context

### EF Core Configuration

```csharp
// In Message entity
public Guid? ThreadParentMessageId { get; set; }
public Message? ThreadParentMessage { get; set; }
public ICollection<Message> ThreadReplies { get; set; } = new List<Message>();

public Guid? ReplyParentMessageId { get; set; }
public Message? ReplyParentMessage { get; set; }

public int ReplyCount { get; set; }
public DateTime? LastReplyAt { get; set; }

// In DbContext OnModelCreating
modelBuilder.Entity<Message>()
    .HasOne(m => m.ThreadParentMessage)
    .WithMany(m => m.ThreadReplies)
    .HasForeignKey(m => m.ThreadParentMessageId)
    .OnDelete(DeleteBehavior.Cascade);

modelBuilder.Entity<Message>()
    .HasOne(m => m.ReplyParentMessage)
    .WithMany() // No inverse navigation needed
    .HasForeignKey(m => m.ReplyParentMessageId)
    .OnDelete(DeleteBehavior.SetNull); // Don't cascade - just remove the preview
```

### Performance Indexes

```csharp
modelBuilder.Entity<Message>()
    .HasIndex(m => m.ThreadParentMessageId);

modelBuilder.Entity<Message>()
    .HasIndex(m => m.ReplyParentMessageId);
```

---

## Backend Changes

### Extend Existing Message Service

No new service needed. Extend `IMessageService` with thread-aware methods:

```csharp
public interface IMessageService
{
    // Existing methods (unchanged)
    Task<MessageDto> CreateMessageAsync(Guid channelId, Guid userId, string content);
    Task<MessageDto> UpdateMessageAsync(Guid messageId, Guid userId, string content);
    Task DeleteMessageAsync(Guid messageId, Guid userId);
    Task<List<MessageDto>> GetChannelMessagesAsync(Guid channelId, int page, int pageSize);

    // New thread methods
    Task<MessageDto> CreateThreadReplyAsync(Guid parentMessageId, Guid userId, string content);
    Task<ThreadDto> GetThreadAsync(Guid parentMessageId, int page = 1, int pageSize = 50);
}
```

**Implementation notes:**
- `CreateThreadReplyAsync` calls existing message creation logic, but sets `ParentMessageId` and updates parent's `ReplyCount`/`LastReplyAt`
- `GetThreadAsync` fetches the parent message + paginated replies
- Edit/delete use existing methods - they work automatically since replies are messages
- Reactions use existing reaction system - no changes needed

### New API Endpoints

Only two new endpoints needed:

```
GET  /api/messages/{parentMessageId}/thread
     → Returns ThreadDto (parent message + paginated replies)
     → Query params: page, pageSize

POST /api/messages/{parentMessageId}/replies
     → Creates a reply in the thread
     → Body: { content: string }
     → Returns: MessageDto (the created reply)
```

All other operations use existing endpoints:
- `PUT /api/messages/{id}` - edit any message (channel or thread reply)
- `DELETE /api/messages/{id}` - delete any message
- `POST /api/messages/{id}/reactions` - add reaction to any message
- `DELETE /api/messages/{id}/reactions/{emoji}` - remove reaction

### Modify Existing Channel Messages Endpoint

Update `GET /api/channels/{channelId}/messages` to exclude thread messages:

```csharp
// Only return top-level messages (not thread messages)
var messages = await _context.Messages
    .Where(m => m.ChannelId == channelId && m.ThreadParentMessageId == null)
    .OrderByDescending(m => m.CreatedAt)
    .ToListAsync();
```

### DTOs

**Extend MessageDto:**
```csharp
public class MessageDto
{
    // Existing fields
    public Guid Id { get; set; }
    public string Content { get; set; }
    public Guid AuthorId { get; set; }
    public UserDto? Author { get; set; }
    public Guid ChannelId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsEdited => UpdatedAt > CreatedAt;
    public List<ReactionDto> Reactions { get; set; }

    // Thread fields
    public Guid? ThreadParentMessageId { get; set; }  // Which thread this belongs to
    public int ReplyCount { get; set; }                // Number of thread replies (for thread starters)
    public DateTime? LastReplyAt { get; set; }         // Latest reply time (for thread starters)

    // Reply preview fields
    public Guid? ReplyParentMessageId { get; set; }   // Which message this replies to (for preview)
    public MessagePreviewDto? ReplyParentMessage { get; set; }  // Preview of replied message
}

// Lightweight DTO for reply previews (avoid circular references)
public class MessagePreviewDto
{
    public Guid Id { get; set; }
    public string Content { get; set; }  // Truncated if long
    public Guid AuthorId { get; set; }
    public string AuthorName { get; set; }
}
```

**New ThreadDto:**
```csharp
public class ThreadDto
{
    public MessageDto ParentMessage { get; set; }
    public List<MessageDto> Replies { get; set; }
    public int TotalReplyCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
```

---

## SignalR Changes

### Extend Existing Events

Most events work as-is. The client just needs to check `ThreadParentMessageId` to know if a message belongs to a thread.

**Existing events that work automatically:**
- `ReceiveMessage` - client checks if `ThreadParentMessageId` is set
- `MessageEdited` - works for any message
- `MessageDeleted` - works for any message
- `ReactionAdded` / `ReactionRemoved` - works for any message

### New SignalR Features

**Thread group management:**
```csharp
// When user opens a thread
await Groups.AddToGroupAsync(connectionId, $"thread_{parentMessageId}");

// When user closes a thread
await Groups.RemoveFromGroupAsync(connectionId, $"thread_{parentMessageId}");
```

**New event for parent message updates:**
```csharp
// Broadcast when ReplyCount or LastReplyAt changes on a parent message
ThreadMetadataUpdated(parentMessageId, replyCount, lastReplyAt)
```

**Targeted broadcasts:**
- Thread replies broadcast to `thread_{parentMessageId}` group (users viewing that thread)
- Also update parent message metadata for all channel users

---

## UI Implementation

### ViewModels

**Extend MessageViewModel:**
```csharp
public class MessageViewModel
{
    // Existing properties...

    // Thread properties
    public Guid? ThreadParentMessageId { get; set; }
    public int ReplyCount { get; set; }
    public DateTime? LastReplyAt { get; set; }
    public bool HasReplies => ReplyCount > 0;
    public bool IsInThread => ThreadParentMessageId != null;

    // Reply preview properties
    public Guid? ReplyParentMessageId { get; set; }
    public MessagePreviewViewModel? ReplyParentMessage { get; set; }
    public bool HasReplyPreview => ReplyParentMessage != null;

    // Commands
    public ICommand OpenThreadCommand { get; }
    public ICommand ReplyToMessageCommand { get; }  // Sets ReplyParentMessageId for next message
}
```

**New ThreadViewModel:**
```csharp
public class ThreadViewModel : ViewModelBase, IDisposable
{
    public MessageViewModel ParentMessage { get; }
    public ObservableCollection<MessageViewModel> Replies { get; }
    public string ReplyInput { get; set; }
    public bool IsLoading { get; set; }

    public ICommand SendReplyCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand LoadMoreCommand { get; }

    public async Task LoadAsync();
    public async Task SendReplyAsync();
    public void Dispose(); // Leave SignalR group
}
```

**Extend MainViewModel:**
```csharp
public class MainViewModel
{
    // Existing...

    public ThreadViewModel? CurrentThread { get; set; }
    public bool IsThreadOpen => CurrentThread != null;

    public void OpenThread(MessageViewModel parentMessage);
    public void CloseThread();
}
```

### UI Components

**ThreadPanel.axaml** (right sidebar, replaces UserList when open):
```
┌─────────────────────────────┐
│ Thread                   [X]│  ← Header with close button
├─────────────────────────────┤
│ ┌─────────────────────────┐ │
│ │ [Avatar] Author         │ │  ← Parent message preview
│ │ Original message text   │ │
│ │ 5 replies · Last 2m ago │ │
│ └─────────────────────────┘ │
├─────────────────────────────┤
│                             │
│ [Reply 1]                   │  ← Replies list (scrollable)
│ [Reply 2]                   │
│ [Reply 3]                   │
│ ...                         │
│                             │
├─────────────────────────────┤
│ [Reply input...]    [Send]  │  ← Reply input
└─────────────────────────────┘
```

**Modify MessageItem.axaml:**
Add thread indicator when `ReplyCount > 0`:
```
┌────────────────────────────────────────┐
│ [Avatar] Author · 2m ago               │
│ Message content here...                │
│                                        │
│ 💬 5 replies · Last reply 2m ago  →    │  ← Clickable thread indicator
└────────────────────────────────────────┘
```

### Interaction Flow

1. **User clicks thread indicator on message**
   - `MainViewModel.OpenThread(message)` called
   - Creates `ThreadViewModel`, calls `LoadAsync()`
   - Joins SignalR group `thread_{parentMessageId}`
   - `ThreadPanel` becomes visible, `UserListPanel` hides

2. **User sends reply in thread**
   - `ThreadViewModel.SendReplyAsync()` called
   - POST to `/api/messages/{parentId}/replies`
   - Server creates message with `ParentMessageId` set
   - Server updates parent's `ReplyCount` and `LastReplyAt`
   - Server broadcasts via SignalR to thread group
   - UI updates automatically

3. **User closes thread**
   - `MainViewModel.CloseThread()` called
   - `ThreadViewModel.Dispose()` leaves SignalR group
   - `ThreadPanel` hides, `UserListPanel` shows

---

## Implementation Checklist

### Phase 1: Database Schema ✅

- [x] Add `ThreadParentMessageId` (nullable FK) to Message entity
- [x] Add `ReplyParentMessageId` (nullable FK) to Message entity
- [x] Add `ReplyCount` (int, default 0) to Message entity
- [x] Add `LastReplyAt` (DateTime?, nullable) to Message entity
- [x] Add `ThreadParentMessage` navigation property
- [x] Add `ReplyParentMessage` navigation property
- [x] Add `ThreadReplies` collection navigation property
- [x] Configure thread self-referential relationship (cascade delete)
- [x] Configure reply self-referential relationship (set null on delete)
- [x] Add index on `ThreadParentMessageId`
- [x] Add index on `ReplyParentMessageId`
- [x] Create EF Core migration
- [x] Apply migration

### Phase 2: Backend Service ✅

- [x] Add `ThreadParentMessageId`, `ReplyCount`, `LastReplyAt` to MessageDto
- [x] Add `ReplyParentMessageId`, `ReplyParentMessage` to MessageDto
- [x] Create `MessagePreviewDto` class (for reply previews)
- [x] Create `ThreadDto` class
- [x] Add `CreateThreadReplyAsync` method to IMessageService
- [x] Add `GetThreadAsync` method to IMessageService
- [x] Update `GetChannelMessagesAsync` to exclude thread messages (where ThreadParentMessageId == null)
- [x] Update delete logic to decrement parent's ReplyCount when thread message is deleted
- [x] Add validation: thread parent message must exist and be in same channel
- [x] Include reply preview data when fetching messages

### Phase 3: API Endpoints ✅

- [x] Add `GET /api/messages/{parentMessageId}/thread` endpoint
- [x] Add `POST /api/messages/{parentMessageId}/replies` endpoint
- [x] Test that existing message endpoints work for thread replies

### Phase 4: SignalR ✅

- [x] Add method to join thread group when opening thread
- [x] Add method to leave thread group when closing thread
- [x] Add `ThreadMetadataUpdated` event for parent message updates
- [x] Broadcast new replies to thread group
- [x] Broadcast parent metadata updates to channel

### Phase 5: UI - ViewModels ✅

- [x] Add `ThreadParentMessageId`, `ReplyCount`, `LastReplyAt`, `HasReplies`, `IsInThread` to MessageViewModel
- [x] Add `ReplyParentMessageId`, `ReplyParentMessage`, `HasReplyPreview` to MessageViewModel
- [x] Create `MessagePreviewViewModel` class
- [x] Add `OpenThreadCommand` to MessageViewModel
- [x] Add `ReplyToMessageCommand` to MessageViewModel
- [x] Create `ThreadViewModel` with Replies collection
- [x] Add `SendReplyCommand` to ThreadViewModel
- [x] Add `CloseCommand` to ThreadViewModel
- [x] Add `LoadAsync` method to ThreadViewModel
- [x] Add `CurrentThread` property to MainViewModel
- [x] Add `IsThreadOpen` computed property to MainViewModel
- [x] Add `OpenThread` method to MainViewModel
- [x] Add `CloseThread` method to MainViewModel
- [x] Add `ReplyingToMessage` property for tracking reply-in-progress

### Phase 6: UI - Components ✅

- [x] Create `ThreadPanel.axaml` layout
- [x] Add parent message preview section to ThreadPanel
- [x] Add replies list (reuse MessageItem or create ThreadReplyItem)
- [x] Add reply input field and send button
- [x] Add close button to ThreadPanel header
- [x] Add thread indicator to MessageItem (reply count badge)
- [x] Make thread indicator clickable
- [x] Style "N replies · Last reply Xm ago" text
- [x] Add reply preview component above MessageItem (when replying to a message)
- [x] Add "Reply to" button/context menu on messages
- [x] Add cancel reply button in input area
- [x] Integrate ThreadPanel into MainWindow right sidebar
- [x] Add visibility toggle between ThreadPanel and UserListPanel

### Phase 7: Real-time Updates ✅

- [x] Subscribe to SignalR events in ThreadViewModel
- [x] Handle new reply received (add to Replies collection)
- [x] Handle reply edited (update in Replies collection)
- [x] Handle reply deleted (remove from Replies collection)
- [x] Handle `ThreadMetadataUpdated` in channel view (update parent message badge)
- [x] Add loading state while fetching thread

### Phase 8: Polish (Partial)

- [x] Add scroll to bottom when thread opens
- [x] Add scroll to bottom when new reply received
- [ ] Add "Load more" for pagination
- [x] Handle edge case: parent message deleted while thread is open
- [x] Add empty state when thread has no replies yet

### Phase 9: Testing

- [ ] Unit test: CreateThreadReplyAsync creates message with ThreadParentMessageId
- [ ] Unit test: CreateThreadReplyAsync increments parent ReplyCount
- [ ] Unit test: DeleteMessageAsync decrements parent ReplyCount (for thread messages)
- [ ] Unit test: GetChannelMessagesAsync excludes thread messages
- [ ] Unit test: GetThreadAsync returns parent + replies
- [ ] Unit test: Message with ReplyParentMessageId includes preview data
- [ ] Unit test: Deleting replied-to message sets ReplyParentMessageId to null
- [ ] Integration test: POST /messages/{id}/replies endpoint
- [ ] Integration test: GET /messages/{id}/thread endpoint
- [ ] Integration test: existing PUT/DELETE work for thread messages
- [ ] Integration test: reply preview loads correctly
- [ ] Test SignalR group join/leave
- [ ] Test real-time reply broadcast

---

## What We're NOT Building (Reusing Instead)

| Feature | Reused From |
|---------|-------------|
| Reply editing | Existing `PUT /api/messages/{id}` |
| Reply deletion | Existing `DELETE /api/messages/{id}` |
| Reply reactions | Existing reaction endpoints |
| Reply entity | Existing `Message` entity |
| Reply DTO | Existing `MessageDto` (extended) |
| Reply SignalR events | Existing message events |

---

## Migration Notes

**Breaking change:** None. Existing messages get `ThreadParentMessageId = null`, `ReplyParentMessageId = null`, `ReplyCount = 0`, `LastReplyAt = null`.

**Migration commands:**
```bash
dotnet ef migrations add AddThreadSupport --project src/Snacka.Server
dotnet ef database update --project src/Snacka.Server
```

---

## Future Enhancements (Out of Scope)

- Unread thread tracking per user
- Thread notifications
- Thread search
- "Also send to channel" option when replying in thread
- Thread following/muting
- Thread previews on hover
- Jump to replied message when clicking reply preview

---

**Last Updated:** 2026-01-12
**Status:** ✅ IMPLEMENTED - Phases 1-7 complete, Phase 8 partial (load more pending), Phase 9 (tests) pending
