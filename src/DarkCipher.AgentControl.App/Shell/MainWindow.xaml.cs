using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DarkCipher.AgentControl.App.Appearance;
using DarkCipher.AgentControl.App.ViewModels;

namespace DarkCipher.AgentControl.App.Shell;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ThemeService? _theme;

    public MainWindow(MainViewModel vm, ThemeService? theme = null)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        DataContext = vm;
        NavList.Loaded += (_, _) => SyncNav();
        vm.Nav.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Navigator.Selected)) SyncNav(); };
        // The sidebar is rebuilt when navigation settings change; re-check the current page once containers exist.
        vm.Nav.ItemsRebuilt += (_, _) => Dispatcher.BeginInvoke(SyncNav, DispatcherPriority.Loaded);
        SourceInitialized += (_, _) => _theme?.ApplyTitleBar(this);
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: NavItem item } && item.Key != _vm.Nav.Selected?.Key) _vm.Nav.Go(item.Key);
    }

    /// <summary>Keeps the checked navigation item in step with programmatic navigation (e.g. "Go to Models").</summary>
    private void SyncNav()
    {
        for (var i = 0; i < NavList.Items.Count; i++)
        {
            if (NavList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            var item = (NavItem)NavList.Items[i];
            var rb = FindChild<RadioButton>(cp);
            if (rb is not null) rb.IsChecked = item.Key == _vm.Nav.Selected?.Key;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            var deeper = FindChild<T>(c);
            if (deeper is not null) return deeper;
        }
        return null;
    }
}
