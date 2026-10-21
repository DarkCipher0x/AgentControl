using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.App.Shell;

/// <summary>Themed modal dialogs. In headless/self-test mode every dialog is refused (and counted) so no window ever
/// appears and nothing is approved implicitly.</summary>
public static class Dialogs
{
    public static bool Headless { get; set; }
    public static int RefusedInHeadless { get; private set; }

    private static Brush R(string key) => (Brush)System.Windows.Application.Current.Resources[key];

    private static Window Make(string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 760,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = R("Surface"),
            Foreground = R("Fg"),
            FontFamily = (FontFamily)System.Windows.Application.Current.Resources["UiFont"],
            ShowInTaskbar = false,
        };
        if (System.Windows.Application.Current.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, w)) w.Owner = owner;
        return w;
    }

    private static TextBlock Text(string s, string style = "Body") => new() { Text = s, Style = (Style)System.Windows.Application.Current.Resources[style], Margin = new Thickness(0, 0, 0, 8) };

    private static bool Refuse() { RefusedInHeadless++; return false; }

    public static void Error(string title, string message)
    {
        if (Headless) { Refuse(); return; }
        Show(title, message, null, ["Close"], "Bad");
    }

    /// <summary>A useful error: what failed, which component, why, what completed safely, what was rolled back, whether user
    /// data was affected, what to do next, and the reference id that appears in the log and audit trail.</summary>
    public static void Error(ErrorReport report)
    {
        if (Headless) { Refuse(); return; }
        if (Show(report.WhatFailed, report.Why ?? "The reason is in the log (reference below).", report.Lines(), ["Copy details", "Close"], "Bad") == 0)
            Mvvm.Ui.Copy(report.WhatFailed + Environment.NewLine + string.Join(Environment.NewLine, report.Lines()));
    }

    public static void Info(string title, string message)
    {
        if (Headless) { Refuse(); return; }
        Show(title, message, null, ["OK"], "Accent");
    }

    public static bool Confirm(string title, string message, IReadOnlyList<string>? details = null, string confirm = "Continue", bool danger = false)
    {
        if (Headless) return Refuse();
        return Show(title, message, details, [confirm, "Cancel"], danger ? "Bad" : "Accent") == 0;
    }

    /// <summary>Returns the index of the button pressed, or -1 if closed.</summary>
    public static int Show(string title, string message, IReadOnlyList<string>? details, string[] buttons, string accentKey)
    {
        if (Headless) { Refuse(); return -1; }
        var w = Make(title, 560);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new Border { Height = 2, Background = R(accentKey), Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left, Width = 40 });
        root.Children.Add(Text(title, "H2"));
        root.Children.Add(Text(message));
        if (details is { Count: > 0 })
        {
            var list = new StackPanel();
            foreach (var d in details) list.Children.Add(new TextBlock { Text = "• " + d, Style = (Style)System.Windows.Application.Current.Resources["MutedText"], Margin = new Thickness(0, 2, 0, 2) });
            root.Children.Add(new Border { Style = (Style)System.Windows.Application.Current.Resources["Notice"], Child = new ScrollViewer { MaxHeight = 320, Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        }
        var result = -1;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var idx = i;
            var b = new Button { Content = buttons[i], IsDefault = i == 0 && buttons.Length == 1, IsCancel = i == buttons.Length - 1 };
            if (i == 0 && buttons.Length > 1) b.Style = (Style)System.Windows.Application.Current.Resources[accentKey == "Bad" ? "DangerButton" : "PrimaryButton"];
            b.Click += (_, _) => { result = idx; w.Close(); };
            bar.Children.Add(b);
        }
        root.Children.Add(bar);
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    public static string? Prompt(string title, string label, string initial = "", bool secret = false, string? hint = null)
    {
        if (Headless) { Refuse(); return null; }
        var w = Make(title, 520);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(Text(title, "H2"));
        if (hint is not null) root.Children.Add(Text(hint, "MutedText"));
        root.Children.Add(Text(label, "Label"));
        Control input = secret ? new PasswordBox() : new TextBox { Text = initial };
        root.Children.Add(input);
        string? result = null;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)System.Windows.Application.Current.Resources["PrimaryButton"] };
        ok.Click += (_, _) => { result = input is PasswordBox pb ? pb.Password : ((TextBox)input).Text; w.Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => w.Close();
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        root.Children.Add(bar);
        w.Content = root;
        w.Loaded += (_, _) => input.Focus();
        w.ShowDialog();
        return result;
    }

    public static void ShowText(string title, string text)
    {
        if (Headless) { Refuse(); return; }
        var w = Make(title, 820);
        w.ResizeMode = ResizeMode.CanResize;
        var root = new DockPanel { Margin = new Thickness(18) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => Mvvm.Ui.Copy(text);
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => w.Close();
        bar.Children.Add(copy);
        bar.Children.Add(close);
        root.Children.Add(bar);
        root.Children.Add(new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, Height = 520,
            FontFamily = (FontFamily)System.Windows.Application.Current.Resources["MonoFont"],
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        w.Content = root;
        w.ShowDialog();
    }
}

/// <summary>IApprovalService backed by a modal dialog. High-risk requests use the danger style.</summary>
public sealed class WpfApprovalService : IApprovalService
{
    public Task<bool> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        Mvvm.Ui.OnUi(() => tcs.TrySetResult(Dialogs.Confirm(request.Title, request.Summary, request.Details, request.ApproveLabel,
            danger: request.Risk >= SecurityClassification.High)));
        return tcs.Task;
    }
}
