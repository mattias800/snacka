using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Miscord.Client.Services;

namespace Miscord.Client.Controls;

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
}
