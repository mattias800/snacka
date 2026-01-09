using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Miscord.Client.Services;

namespace Miscord.Client.Controls;

/// <summary>
/// A reusable members list component that displays community members with online status.
/// </summary>
public partial class MembersListView : UserControl
{
    // The current user's ID (for showing "(You)" label)
    public static readonly StyledProperty<Guid> CurrentUserIdProperty =
        AvaloniaProperty.Register<MembersListView, Guid>(nameof(CurrentUserId));

    // The members collection
    public static readonly StyledProperty<IEnumerable<CommunityMemberResponse>?> MembersProperty =
        AvaloniaProperty.Register<MembersListView, IEnumerable<CommunityMemberResponse>?>(nameof(Members));

    // Whether the current user can manage members (admin/owner)
    public static readonly StyledProperty<bool> CanManageMembersProperty =
        AvaloniaProperty.Register<MembersListView, bool>(nameof(CanManageMembers), false);

    // Commands
    public static readonly StyledProperty<ICommand?> ChangeMyNicknameCommandProperty =
        AvaloniaProperty.Register<MembersListView, ICommand?>(nameof(ChangeMyNicknameCommand));

    public static readonly StyledProperty<ICommand?> StartDMCommandProperty =
        AvaloniaProperty.Register<MembersListView, ICommand?>(nameof(StartDMCommand));

    public static readonly StyledProperty<ICommand?> ChangeMemberNicknameCommandProperty =
        AvaloniaProperty.Register<MembersListView, ICommand?>(nameof(ChangeMemberNicknameCommand));

    public static readonly StyledProperty<ICommand?> PromoteToAdminCommandProperty =
        AvaloniaProperty.Register<MembersListView, ICommand?>(nameof(PromoteToAdminCommand));

    public static readonly StyledProperty<ICommand?> DemoteToMemberCommandProperty =
        AvaloniaProperty.Register<MembersListView, ICommand?>(nameof(DemoteToMemberCommand));

    public static readonly StyledProperty<ICommand?> TransferOwnershipCommandProperty =
        AvaloniaProperty.Register<MembersListView, ICommand?>(nameof(TransferOwnershipCommand));

    public MembersListView()
    {
        InitializeComponent();
    }

    public Guid CurrentUserId
    {
        get => GetValue(CurrentUserIdProperty);
        set => SetValue(CurrentUserIdProperty, value);
    }

    public IEnumerable<CommunityMemberResponse>? Members
    {
        get => GetValue(MembersProperty);
        set => SetValue(MembersProperty, value);
    }

    public bool CanManageMembers
    {
        get => GetValue(CanManageMembersProperty);
        set => SetValue(CanManageMembersProperty, value);
    }

    public ICommand? ChangeMyNicknameCommand
    {
        get => GetValue(ChangeMyNicknameCommandProperty);
        set => SetValue(ChangeMyNicknameCommandProperty, value);
    }

    public ICommand? StartDMCommand
    {
        get => GetValue(StartDMCommandProperty);
        set => SetValue(StartDMCommandProperty, value);
    }

    public ICommand? ChangeMemberNicknameCommand
    {
        get => GetValue(ChangeMemberNicknameCommandProperty);
        set => SetValue(ChangeMemberNicknameCommandProperty, value);
    }

    public ICommand? PromoteToAdminCommand
    {
        get => GetValue(PromoteToAdminCommandProperty);
        set => SetValue(PromoteToAdminCommandProperty, value);
    }

    public ICommand? DemoteToMemberCommand
    {
        get => GetValue(DemoteToMemberCommandProperty);
        set => SetValue(DemoteToMemberCommandProperty, value);
    }

    public ICommand? TransferOwnershipCommand
    {
        get => GetValue(TransferOwnershipCommandProperty);
        set => SetValue(TransferOwnershipCommandProperty, value);
    }

    // Event for when a member is clicked
    public event EventHandler<CommunityMemberResponse>? MemberClicked;

    private void Member_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is CommunityMemberResponse member)
        {
            MemberClicked?.Invoke(this, member);
        }
    }
}
