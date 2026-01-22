using System.Reactive;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Snacka.Client.Models;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;
using ReactiveUI;

namespace Snacka.Client.Controls;

/// <summary>
/// A reusable members list component that displays community members with online status.
/// </summary>
public partial class MembersListView : UserControl
{
    public static readonly StyledProperty<MembersListViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<MembersListView, MembersListViewModel?>(nameof(ViewModel));

    public MembersListView()
    {
        InitializeComponent();
    }

    public MembersListViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Event for when a member is clicked
    public event EventHandler<CommunityMemberResponse>? MemberClicked;

    private void Member_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only trigger MemberClicked on left-click, not right-click (context menu)
        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed &&
            sender is Border border && border.Tag is CommunityMemberResponse member)
        {
            MemberClicked?.Invoke(this, member);
        }
    }

    // Context menu click handlers
    private void OnChangeMyNicknameClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ChangeMyNicknameCommand?.Execute().Subscribe();
    }

    private void OnMemberContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        // Show/hide admin items based on CanManageMembers
        if (sender is ContextMenu menu)
        {
            var canManage = ViewModel?.CanManageMembers ?? false;
            foreach (var item in menu.Items)
            {
                if (item is Separator sep && (sep.Name == "AdminSeparator1" || sep.Name == "AdminSeparator2"))
                {
                    sep.IsVisible = canManage;
                }
                else if (item is MenuItem menuItem)
                {
                    var name = menuItem.Name;
                    if (name == "ChangeNicknameItem" || name == "PromoteItem" ||
                        name == "DemoteItem" || name == "TransferItem")
                    {
                        menuItem.IsVisible = canManage;
                    }
                }
            }
        }
    }

    private void OnStartDMClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is CommunityMemberResponse member)
        {
            ViewModel?.StartDMCommand?.Execute(member);
        }
    }

    private void OnChangeMemberNicknameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is CommunityMemberResponse member)
        {
            ViewModel?.ChangeMemberNicknameCommand?.Execute(member);
        }
    }

    private void OnPromoteToAdminClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is CommunityMemberResponse member)
        {
            ViewModel?.PromoteToAdminCommand?.Execute(member);
        }
    }

    private void OnDemoteToMemberClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is CommunityMemberResponse member)
        {
            ViewModel?.DemoteToMemberCommand?.Execute(member);
        }
    }

    private void OnTransferOwnershipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is CommunityMemberResponse member)
        {
            ViewModel?.TransferOwnershipCommand?.Execute(member);
        }
    }

    // Gaming station context menu handlers
    private void OnGamingStationContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            // Find the station from the context menu's DataContext
            var station = menu.DataContext as MyGamingStationInfo;
            if (station is null) return;

            // Find the "Add to channel" menu item and update its enabled state
            foreach (var item in menu.Items)
            {
                if (item is MenuItem menuItem && menuItem.Name == "AddToChannelItem")
                {
                    // Disable if: station is current machine, station is offline, or already in channel
                    menuItem.IsEnabled = !station.IsCurrentMachine && station.IsAvailable && !station.IsInVoiceChannel;
                }
            }
        }
    }

    private void OnAddStationToChannelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is MyGamingStationInfo station)
        {
            ViewModel?.AddStationToChannelCommand?.Execute(station);
        }
    }
}
