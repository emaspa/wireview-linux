using System;
using Avalonia;
using Avalonia.Controls;
using WireView2.ViewModels;

namespace WireView2.Views;

/// <summary>Tells a page's view model whether the page is on screen: attached to
/// the visual tree with that view model as its DataContext. The main window's
/// visibility (tray) is tracked by the view models themselves through
/// App.MainWindowVisibilityChanged.</summary>
internal static class ViewVisibilityTracker
{
    public static void Track(UserControl view)
    {
        IViewVisibilityAware? current = null;
        bool attached = false;

        void Sync()
        {
            var vm = attached ? view.DataContext as IViewVisibilityAware : null;
            if (!ReferenceEquals(vm, current) && current != null)
                current.IsViewVisible = false;
            current = vm;
            if (current != null)
                current.IsViewVisible = true;
        }

        view.AttachedToVisualTree += (_, _) => { attached = true; Sync(); };
        view.DetachedFromVisualTree += (_, _) => { attached = false; Sync(); };
        view.DataContextChanged += (_, _) => Sync();
    }
}
