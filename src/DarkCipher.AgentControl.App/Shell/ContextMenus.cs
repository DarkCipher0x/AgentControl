using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DarkCipher.AgentControl.App.Shell;

/// <summary>One right-click action. <see cref="Group"/> keeps related actions together; <see cref="Danger"/> actions
/// are always placed last.</summary>
public sealed record MenuAction(string Header, ICommand Command, object? Parameter = null, string Group = "", string? Glyph = null, bool Danger = false)
{
    public bool IsEnabled => Command.CanExecute(Parameter);
}

/// <summary>A page view model that offers right-click actions for the items it lists.</summary>
public interface IContextActionProvider
{
    IReadOnlyList<MenuAction> ContextActions(object item);
}

/// <summary>
/// Right-click menus for list items, built on demand from the page's <see cref="IContextActionProvider"/>.
/// Usage: <c>shell:ContextMenus.Provider="{Binding}"</c> on an ItemsControl, ListBox or DataGrid. The item under the
/// mouse (or the selected item, for the keyboard Menu key) is selected first, then the menu is built.
/// Layout: when the menu is short (up to <see cref="FlatLimit"/> actions, or only one group) every action is shown
/// with separators between groups. When it is crowded, the first group stays at the top level and every other group
/// becomes a submenu named after the group, so the menu stays scannable. Disabled actions are hidden, not greyed,
/// except in the first group (so the main verbs stay in a predictable place).
/// </summary>
public static class ContextMenus
{
    public const int FlatLimit = 9;

    public static readonly DependencyProperty ProviderProperty = DependencyProperty.RegisterAttached(
        "Provider", typeof(IContextActionProvider), typeof(ContextMenus), new PropertyMetadata(null, OnProviderChanged));

    public static IContextActionProvider? GetProvider(DependencyObject d) => (IContextActionProvider?)d.GetValue(ProviderProperty);
    public static void SetProvider(DependencyObject d, IContextActionProvider? value) => d.SetValue(ProviderProperty, value);

    private static void OnProviderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        list.ContextMenuOpening -= OnOpening;
        if (e.NewValue is null) return;
        // ContextMenuOpening is only raised when a ContextMenu exists; this one is filled on every opening.
        list.ContextMenu ??= new ContextMenu();
        list.ContextMenuOpening += OnOpening;
    }

    private static void OnOpening(object sender, ContextMenuEventArgs e)
    {
        var list = (ItemsControl)sender;
        var provider = GetProvider(list);
        var item = ItemUnder(list, e.OriginalSource as DependencyObject);
        if (provider is null || item is null) { e.Handled = true; return; }
        if (list is System.Windows.Controls.Primitives.Selector selector && !ReferenceEquals(selector.SelectedItem, item)) selector.SelectedItem = item;

        var actions = provider.ContextActions(item);
        if (actions.Count == 0) { e.Handled = true; return; }
        var menu = list.ContextMenu!;
        menu.Items.Clear();
        foreach (var entry in Build(actions)) menu.Items.Add(entry);
        menu.PlacementTarget = list;
    }

    /// <summary>The data item whose container holds the element (or the selected item when opened from the keyboard).</summary>
    private static object? ItemUnder(ItemsControl list, DependencyObject? source)
    {
        while (source is not null and not Visual and not System.Windows.Media.Media3D.Visual3D)
            source = LogicalTreeHelper.GetParent(source);
        if (source is not null && ItemsControl.ContainerFromElement(list, source) is { } container)
        {
            var item = list.ItemContainerGenerator.ItemFromContainer(container);
            if (item != DependencyProperty.UnsetValue && item is not null && item != CollectionView.NewItemPlaceholder) return item;
        }
        return list is System.Windows.Controls.Primitives.Selector s ? s.SelectedItem : null;
    }

    /// <summary>Pure layout logic (unit-tested): groups in first-appearance order, danger actions last.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<MenuAction> Actions)> Arrange(IReadOnlyList<MenuAction> actions)
    {
        var ordered = actions.Where(a => !a.Danger).Concat(actions.Where(a => a.Danger)).ToList();
        var groups = new List<(string Group, List<MenuAction> Actions)>();
        foreach (var a in ordered)
        {
            var key = a.Danger ? "\u0000danger" : a.Group;
            var g = groups.FindIndex(x => x.Group == key);
            if (g < 0) groups.Add((key, [a]));
            else groups[g].Actions.Add(a);
        }
        return groups.Select(g => (g.Group, (IReadOnlyList<MenuAction>)g.Actions)).ToList();
    }

    public static bool ShouldGroup(IReadOnlyList<MenuAction> actions) =>
        actions.Count > FlatLimit && Arrange(actions).Count(g => !g.Group.StartsWith('\u0000')) > 1;

    internal static IEnumerable<object> Build(IReadOnlyList<MenuAction> actions)
    {
        var visible = actions.Where((a, i) => a.IsEnabled || string.Equals(a.Group, actions[0].Group, StringComparison.Ordinal)).ToList();
        var groups = Arrange(visible);
        if (!ShouldGroup(visible))
        {
            // Short menu: every action, separators between groups.
            for (var g = 0; g < groups.Count; g++)
            {
                if (g > 0) yield return new Separator();
                foreach (var a in groups[g].Actions) yield return Item(a);
            }
            yield break;
        }

        // Crowded menu: the main verbs, then one block of submenus (one per group, no separators between them), then
        // the danger actions on their own.
        foreach (var a in groups[0].Actions) yield return Item(a);
        var nested = groups.Skip(1).Where(g => !g.Group.StartsWith('\u0000')).ToList();
        if (nested.Count > 0) yield return new Separator();
        foreach (var (group, items) in nested)
        {
            if (items.Count == 1 || group.Length == 0) { foreach (var a in items) yield return Item(a); continue; }
            var sub = new MenuItem { Header = group };
            System.Windows.Automation.AutomationProperties.SetName(sub, group + " submenu");
            foreach (var a in items) sub.Items.Add(Item(a));
            yield return sub;
        }
        foreach (var (_, items) in groups.Skip(1).Where(g => g.Group.StartsWith('\u0000')))
        {
            yield return new Separator();
            foreach (var a in items) yield return Item(a);
        }
    }

    private static MenuItem Item(MenuAction a)
    {
        var mi = new MenuItem { Header = a.Header, Command = a.Command, CommandParameter = a.Parameter, Icon = a.Glyph is null ? null : Glyph(a.Glyph) };
        if (a.Danger) mi.SetResourceReference(Control.ForegroundProperty, "Bad");
        System.Windows.Automation.AutomationProperties.SetName(mi, a.Header.Replace("…", "", StringComparison.Ordinal));
        return mi;
    }

    private static TextBlock Glyph(string glyph)
    {
        var t = new TextBlock { Text = glyph, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FsNavIcon");
        return t;
    }
}
