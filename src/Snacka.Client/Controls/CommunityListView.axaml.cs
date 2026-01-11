using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Snacka.Client.Services;

namespace Snacka.Client.Controls;

/// <summary>
/// Left sidebar showing community icons, DM button, and create community button.
/// </summary>
public partial class CommunityListView : UserControl
{
    public static readonly StyledProperty<IEnumerable<CommunityResponse>?> CommunitiesProperty =
        AvaloniaProperty.Register<CommunityListView, IEnumerable<CommunityResponse>?>(nameof(Communities));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<CommunityListView, bool>(nameof(IsLoading));

    public static readonly StyledProperty<ICommand?> SelectCommunityCommandProperty =
        AvaloniaProperty.Register<CommunityListView, ICommand?>(nameof(SelectCommunityCommand));

    public static readonly StyledProperty<ICommand?> CreateCommunityCommandProperty =
        AvaloniaProperty.Register<CommunityListView, ICommand?>(nameof(CreateCommunityCommand));

    public static readonly StyledProperty<ICommand?> OpenDMsCommandProperty =
        AvaloniaProperty.Register<CommunityListView, ICommand?>(nameof(OpenDMsCommand));

    public static readonly StyledProperty<ICommand?> OpenPendingInvitesCommandProperty =
        AvaloniaProperty.Register<CommunityListView, ICommand?>(nameof(OpenPendingInvitesCommand));

    public static readonly StyledProperty<int> PendingInviteCountProperty =
        AvaloniaProperty.Register<CommunityListView, int>(nameof(PendingInviteCount));

    public static readonly StyledProperty<bool> HasPendingInvitesProperty =
        AvaloniaProperty.Register<CommunityListView, bool>(nameof(HasPendingInvites));

    public CommunityListView()
    {
        InitializeComponent();
    }

    public IEnumerable<CommunityResponse>? Communities
    {
        get => GetValue(CommunitiesProperty);
        set => SetValue(CommunitiesProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public ICommand? SelectCommunityCommand
    {
        get => GetValue(SelectCommunityCommandProperty);
        set => SetValue(SelectCommunityCommandProperty, value);
    }

    public ICommand? CreateCommunityCommand
    {
        get => GetValue(CreateCommunityCommandProperty);
        set => SetValue(CreateCommunityCommandProperty, value);
    }

    public ICommand? OpenDMsCommand
    {
        get => GetValue(OpenDMsCommandProperty);
        set => SetValue(OpenDMsCommandProperty, value);
    }

    public ICommand? OpenPendingInvitesCommand
    {
        get => GetValue(OpenPendingInvitesCommandProperty);
        set => SetValue(OpenPendingInvitesCommandProperty, value);
    }

    public int PendingInviteCount
    {
        get => GetValue(PendingInviteCountProperty);
        set => SetValue(PendingInviteCountProperty, value);
    }

    public bool HasPendingInvites
    {
        get => GetValue(HasPendingInvitesProperty);
        set => SetValue(HasPendingInvitesProperty, value);
    }
}
