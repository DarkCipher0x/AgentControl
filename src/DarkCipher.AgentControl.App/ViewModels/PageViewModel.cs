using System.Windows.Media;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.Domain.Common;

namespace DarkCipher.AgentControl.App.ViewModels;

/// <summary>Base for every screen: busy state, a status line and a guarded activation (errors are reported, not lost).</summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _busy;
    private string _message = "";

    public abstract string Title { get; }
    public virtual string Subtitle => "";

    public bool Busy { get => _busy; protected set => Set(ref _busy, value); }
    public string Message { get => _message; set => Set(ref _message, value); }

    public async Task ActivateSafeAsync()
    {
        try
        {
            Busy = true;
            await OnActivateAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = ex.Message;
            ErrorReporter.Report($"{Title} could not load", ex);
        }
        finally { Busy = false; }
    }

    protected virtual Task OnActivateAsync() => Task.CompletedTask;

    /// <summary>Runs an action with busy state and reports failures into <see cref="Message"/> and the log.</summary>
    protected async Task Guard(Func<Task> action, string context)
    {
        try
        {
            Busy = true;
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = $"{context}: {ex.Message}";
            ErrorReporter.Report(context, ex);
        }
        finally { Busy = false; }
    }
}

/// <summary>Maps health/status words to theme brushes (status is always also shown as text, never colour alone).</summary>
public static class StatusBrushes
{
    private static Brush Get(string key) => (Brush)System.Windows.Application.Current.Resources[key];

    public static Brush For(ComponentHealth h) => h switch
    {
        ComponentHealth.Healthy => Get("Good"),
        ComponentHealth.Degraded or ComponentHealth.Misconfigured => Get("Warn"),
        ComponentHealth.Unknown => Get("Muted"),
        _ => Get("Bad"),
    };

    public static Brush Good => Get("Good");
    public static Brush Warn => Get("Warn");
    public static Brush Bad => Get("Bad");
    public static Brush Muted => Get("Muted");
    public static Brush Info => Get("Info");
    public static Brush Experimental => Get("Experimental");
    public static Brush Accent => Get("Accent");
}
