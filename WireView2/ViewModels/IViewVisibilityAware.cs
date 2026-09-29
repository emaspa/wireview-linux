namespace WireView2.ViewModels;

/// <summary>A page view model that pauses UI work while its page is not shown.
/// <see cref="Views.ViewVisibilityTracker"/> sets <see cref="IsViewVisible"/> when
/// the page's view is attached to or detached from the window.</summary>
public interface IViewVisibilityAware
{
    bool IsViewVisible { get; set; }
}
