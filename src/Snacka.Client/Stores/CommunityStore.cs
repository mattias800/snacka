using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using Snacka.Client.Services;
using Snacka.Shared.Models;

namespace Snacka.Client.Stores;

/// <summary>
/// Immutable state representing a community.
/// </summary>
public record CommunityState(
    Guid Id,
    string Name,
    string? Description,
    string? Icon,
    Guid OwnerId,
    string OwnerUsername,
    string OwnerEffectiveDisplayName,
    DateTime CreatedAt,
    int MemberCount
);

/// <summary>
/// Immutable state representing a community member.
/// </summary>
public record CommunityMemberState(
    Guid UserId,
    Guid CommunityId,
    string Username,
    string? DisplayName,
    string? DisplayNameOverride,
    string EffectiveDisplayName,
    string? Avatar,
    bool IsOnline,
    UserRole Role,
    DateTime JoinedAt
);

/// <summary>
/// Store managing community and member state.
/// </summary>
public interface ICommunityStore : IStore<CommunityState, Guid>
{
    /// <summary>
    /// Currently selected community ID.
    /// </summary>
    IObservable<Guid?> SelectedCommunityId { get; }

    /// <summary>
    /// Currently selected community state.
    /// </summary>
    IObservable<CommunityState?> SelectedCommunity { get; }

    /// <summary>
    /// Members of the currently selected community.
    /// </summary>
    IObservable<IReadOnlyCollection<CommunityMemberState>> CurrentCommunityMembers { get; }

    /// <summary>
    /// Online members of the currently selected community.
    /// </summary>
    IObservable<IReadOnlyCollection<CommunityMemberState>> OnlineMembers { get; }

    /// <summary>
    /// Offline members of the currently selected community.
    /// </summary>
    IObservable<IReadOnlyCollection<CommunityMemberState>> OfflineMembers { get; }

    /// <summary>
    /// Observable stream of member changes.
    /// </summary>
    IObservable<IChangeSet<CommunityMemberState, Guid>> ConnectMembers();

    /// <summary>
    /// Gets a community by ID synchronously.
    /// </summary>
    CommunityState? GetCommunity(Guid communityId);

    /// <summary>
    /// Gets a member by user ID for the current community.
    /// </summary>
    CommunityMemberState? GetMember(Guid userId);

    /// <summary>
    /// Gets the current user's role in the selected community.
    /// </summary>
    UserRole? GetCurrentUserRole(Guid currentUserId);

    /// <summary>
    /// Gets the currently selected community ID synchronously.
    /// </summary>
    Guid? GetSelectedCommunityId();

    /// <summary>
    /// Gets the currently selected community state synchronously.
    /// </summary>
    CommunityState? GetSelectedCommunity();

    /// <summary>
    /// Gets all current community members synchronously.
    /// </summary>
    IReadOnlyList<CommunityMemberState> GetCurrentMembers();

    // Actions
    void SetCommunities(IEnumerable<CommunityResponse> communities);
    void SelectCommunity(Guid? communityId);
    void AddCommunity(CommunityResponse community);
    void UpdateCommunity(CommunityResponse community);
    void RemoveCommunity(Guid communityId);
    void SetMembers(Guid communityId, IEnumerable<CommunityMemberResponse> members);
    void AddMember(Guid communityId, CommunityMemberResponse member);
    void RemoveMember(Guid communityId, Guid userId);
    void UpdateMemberOnlineStatus(Guid userId, bool isOnline);
    void UpdateMemberRole(Guid communityId, Guid userId, UserRole role);
    void UpdateMemberNickname(Guid communityId, Guid userId, string? nickname);
    void Clear();
}

public sealed class CommunityStore : ICommunityStore, IDisposable
{
    private readonly SourceCache<CommunityState, Guid> _communityCache;
    private readonly SourceCache<CommunityMemberState, Guid> _memberCache;
    private readonly BehaviorSubject<Guid?> _selectedCommunityId;
    private readonly IDisposable _cleanUp;

    public CommunityStore()
    {
        _communityCache = new SourceCache<CommunityState, Guid>(c => c.Id);
        _memberCache = new SourceCache<CommunityMemberState, Guid>(m => m.UserId);
        _selectedCommunityId = new BehaviorSubject<Guid?>(null);

        _cleanUp = _communityCache.Connect().Subscribe();
    }

    public IObservable<IChangeSet<CommunityState, Guid>> Connect() => _communityCache.Connect();

    public IObservable<IChangeSet<CommunityMemberState, Guid>> ConnectMembers() => _memberCache.Connect();

    public IObservable<IReadOnlyCollection<CommunityState>> Items =>
        _communityCache.Connect()
            .QueryWhenChanged(cache => cache.Items.ToList().AsReadOnly() as IReadOnlyCollection<CommunityState>)
            .StartWith(_communityCache.Items.ToList().AsReadOnly() as IReadOnlyCollection<CommunityState>);

    public IObservable<Guid?> SelectedCommunityId => _selectedCommunityId.AsObservable();

    public IObservable<CommunityState?> SelectedCommunity =>
        _selectedCommunityId
            .Select(selectedId =>
            {
                if (selectedId is null) return null;
                var lookup = _communityCache.Lookup(selectedId.Value);
                return lookup.HasValue ? lookup.Value : null;
            })
            .Merge(
                _communityCache.Connect()
                    .QueryWhenChanged()
                    .Select(_ =>
                    {
                        var selectedId = _selectedCommunityId.Value;
                        if (selectedId is null) return null;
                        var lookup = _communityCache.Lookup(selectedId.Value);
                        return lookup.HasValue ? lookup.Value : null;
                    }))
            .DistinctUntilChanged();

    public IObservable<IReadOnlyCollection<CommunityMemberState>> CurrentCommunityMembers =>
        _selectedCommunityId
            .Select(_ => Unit.Default)
            .Merge(_memberCache.Connect().Select(_ => Unit.Default))
            .Select(_ => GetCurrentCommunityMembersInternal());

    private IReadOnlyCollection<CommunityMemberState> GetCurrentCommunityMembersInternal()
    {
        var communityId = _selectedCommunityId.Value;
        if (communityId is null)
            return Array.Empty<CommunityMemberState>();

        return _memberCache.Items
            .Where(m => m.CommunityId == communityId.Value)
            .OrderBy(m => m.Role)
            .ThenBy(m => m.EffectiveDisplayName)
            .ToList()
            .AsReadOnly();
    }

    public IObservable<IReadOnlyCollection<CommunityMemberState>> OnlineMembers =>
        _selectedCommunityId
            .Select(_ => Unit.Default)
            .Merge(_memberCache.Connect().Select(_ => Unit.Default))
            .Select(_ => GetOnlineMembersInternal());

    private IReadOnlyCollection<CommunityMemberState> GetOnlineMembersInternal()
    {
        var communityId = _selectedCommunityId.Value;
        if (communityId is null)
            return Array.Empty<CommunityMemberState>();

        return _memberCache.Items
            .Where(m => m.CommunityId == communityId.Value && m.IsOnline)
            .OrderBy(m => m.Role)
            .ThenBy(m => m.EffectiveDisplayName)
            .ToList()
            .AsReadOnly();
    }

    public IObservable<IReadOnlyCollection<CommunityMemberState>> OfflineMembers =>
        _selectedCommunityId
            .Select(_ => Unit.Default)
            .Merge(_memberCache.Connect().Select(_ => Unit.Default))
            .Select(_ => GetOfflineMembersInternal());

    private IReadOnlyCollection<CommunityMemberState> GetOfflineMembersInternal()
    {
        var communityId = _selectedCommunityId.Value;
        if (communityId is null)
            return Array.Empty<CommunityMemberState>();

        return _memberCache.Items
            .Where(m => m.CommunityId == communityId.Value && !m.IsOnline)
            .OrderBy(m => m.Role)
            .ThenBy(m => m.EffectiveDisplayName)
            .ToList()
            .AsReadOnly();
    }

    public CommunityState? GetCommunity(Guid communityId)
    {
        var lookup = _communityCache.Lookup(communityId);
        return lookup.HasValue ? lookup.Value : null;
    }

    public CommunityMemberState? GetMember(Guid userId)
    {
        var lookup = _memberCache.Lookup(userId);
        return lookup.HasValue ? lookup.Value : null;
    }

    public UserRole? GetCurrentUserRole(Guid currentUserId)
    {
        var communityId = _selectedCommunityId.Value;
        if (communityId is null) return null;

        var member = _memberCache.Items.FirstOrDefault(m => m.CommunityId == communityId.Value && m.UserId == currentUserId);
        return member?.Role;
    }

    public Guid? GetSelectedCommunityId() => _selectedCommunityId.Value;

    public CommunityState? GetSelectedCommunity()
    {
        var selectedId = _selectedCommunityId.Value;
        if (selectedId is null) return null;
        var lookup = _communityCache.Lookup(selectedId.Value);
        return lookup.HasValue ? lookup.Value : null;
    }

    public IReadOnlyList<CommunityMemberState> GetCurrentMembers() =>
        GetCurrentCommunityMembersInternal().ToList().AsReadOnly();

    public void SetCommunities(IEnumerable<CommunityResponse> communities)
    {
        _communityCache.Edit(cache =>
        {
            cache.Clear();
            foreach (var community in communities)
            {
                cache.AddOrUpdate(MapToState(community));
            }
        });
    }

    public void SelectCommunity(Guid? communityId)
    {
        _selectedCommunityId.OnNext(communityId);
    }

    public void AddCommunity(CommunityResponse community)
    {
        _communityCache.AddOrUpdate(MapToState(community));
    }

    public void UpdateCommunity(CommunityResponse community)
    {
        _communityCache.AddOrUpdate(MapToState(community));
    }

    public void RemoveCommunity(Guid communityId)
    {
        _communityCache.Remove(communityId);

        // Clear members for this community
        var toRemove = _memberCache.Items.Where(m => m.CommunityId == communityId).Select(m => m.UserId).ToList();
        _memberCache.Edit(cache =>
        {
            foreach (var id in toRemove)
            {
                cache.Remove(id);
            }
        });

        // Clear selection if removed community was selected
        if (_selectedCommunityId.Value == communityId)
        {
            _selectedCommunityId.OnNext(null);
        }
    }

    public void SetMembers(Guid communityId, IEnumerable<CommunityMemberResponse> members)
    {
        _memberCache.Edit(cache =>
        {
            // Remove existing members for this community
            var toRemove = cache.Items.Where(m => m.CommunityId == communityId).Select(m => m.UserId).ToList();
            foreach (var id in toRemove)
            {
                cache.Remove(id);
            }

            // Add new members
            foreach (var member in members)
            {
                cache.AddOrUpdate(MapMemberToState(communityId, member));
            }
        });
    }

    public void AddMember(Guid communityId, CommunityMemberResponse member)
    {
        _memberCache.AddOrUpdate(MapMemberToState(communityId, member));
    }

    public void RemoveMember(Guid communityId, Guid userId)
    {
        var existing = _memberCache.Items.FirstOrDefault(m => m.CommunityId == communityId && m.UserId == userId);
        if (existing is not null)
        {
            _memberCache.Remove(existing.UserId);
        }
    }

    public void UpdateMemberOnlineStatus(Guid userId, bool isOnline)
    {
        var existing = _memberCache.Lookup(userId);
        if (existing.HasValue)
        {
            _memberCache.AddOrUpdate(existing.Value with { IsOnline = isOnline });
        }
    }

    public void UpdateMemberRole(Guid communityId, Guid userId, UserRole role)
    {
        var existing = _memberCache.Items.FirstOrDefault(m => m.CommunityId == communityId && m.UserId == userId);
        if (existing is not null)
        {
            _memberCache.AddOrUpdate(existing with { Role = role });
        }
    }

    public void UpdateMemberNickname(Guid communityId, Guid userId, string? nickname)
    {
        var existing = _memberCache.Items.FirstOrDefault(m => m.CommunityId == communityId && m.UserId == userId);
        if (existing is not null)
        {
            var effectiveDisplayName = nickname ?? existing.DisplayName ?? existing.Username;
            _memberCache.AddOrUpdate(existing with
            {
                DisplayNameOverride = nickname,
                EffectiveDisplayName = effectiveDisplayName
            });
        }
    }

    public void Clear()
    {
        _communityCache.Clear();
        _memberCache.Clear();
        _selectedCommunityId.OnNext(null);
    }

    private static CommunityState MapToState(CommunityResponse response) =>
        new CommunityState(
            Id: response.Id,
            Name: response.Name,
            Description: response.Description,
            Icon: response.Icon,
            OwnerId: response.OwnerId,
            OwnerUsername: response.OwnerUsername,
            OwnerEffectiveDisplayName: response.OwnerEffectiveDisplayName,
            CreatedAt: response.CreatedAt,
            MemberCount: response.MemberCount
        );

    private static CommunityMemberState MapMemberToState(Guid communityId, CommunityMemberResponse response) =>
        new CommunityMemberState(
            UserId: response.UserId,
            CommunityId: communityId,
            Username: response.Username,
            DisplayName: response.DisplayName,
            DisplayNameOverride: response.DisplayNameOverride,
            EffectiveDisplayName: response.EffectiveDisplayName,
            Avatar: response.Avatar,
            IsOnline: response.IsOnline,
            Role: response.Role,
            JoinedAt: response.JoinedAt
        );

    public void Dispose()
    {
        _cleanUp.Dispose();
        _communityCache.Dispose();
        _memberCache.Dispose();
        _selectedCommunityId.Dispose();
    }
}
