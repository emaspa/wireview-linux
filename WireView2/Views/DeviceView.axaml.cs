using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WireView2.ViewModels;

namespace WireView2.Views;

public partial class DeviceView : UserControl
{
    public DeviceView()
    {
        InitializeComponent();
    }

    private async void OnSelectBackgroundClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select background image (shown as 320×170)",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" } },
                new("All files") { Patterns = new[] { "*" } },
            },
        });

        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null)
            await vm.UploadBackgroundFromFileAsync(path);
    }

    private void OnClearBackgroundClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceViewModel vm)
            vm.ClearPendingBackground();
    }

    private async void OnLoadThemeFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load WireView2 theme",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("WireView2 Theme") { Patterns = new[] { "*.wv2t" } },
                new("All files") { Patterns = new[] { "*" } },
            },
        });

        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null)
            await vm.LoadThemeFromFileAsync(path);
    }

    private async void OnSaveThemeFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save WireView2 theme",
            SuggestedFileName = "theme.wv2t",
            DefaultExtension = "wv2t",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("WireView2 Theme") { Patterns = new[] { "*.wv2t" } },
            },
        });

        string? path = file?.TryGetLocalPath();
        if (path != null)
            await vm.SaveThemeToFileAsync(path);
    }
}
