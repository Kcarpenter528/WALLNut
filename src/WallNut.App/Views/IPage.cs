namespace WallNut.App.Views;

/// <summary>A page in the main window. Pages start/stop their own refresh timers as they're shown and hidden.</summary>
public interface IPage
{
    string Title { get; }
    void OnShown();
    void OnHidden();
}
