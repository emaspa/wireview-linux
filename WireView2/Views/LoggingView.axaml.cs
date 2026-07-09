using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WireView2.ViewModels;

namespace WireView2.Views;

public partial class LoggingView : UserControl
{
    public LoggingView()
    {
        InitializeComponent();
    }

    private async void OnExportCsvClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggingViewModel vm) return;
        string? path = await PickSavePathAsync("Export log to CSV",
            $"wireview-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv", "csv", "CSV");
        if (path != null)
            await vm.ExportCsvAsync(path);
    }

    private async void OnSaveLogClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggingViewModel vm) return;
        string? path = await PickSavePathAsync("Save raw device log",
            $"wireview-log-{DateTime.Now:yyyyMMdd-HHmmss}.bin", "bin", "Raw log");
        if (path != null)
            await vm.SaveToFileAsync(path);
    }

    private async void OnLoadLogClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggingViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load raw device log",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Raw log") { Patterns = new[] { "*.bin" } },
                new("All files") { Patterns = new[] { "*" } },
            },
        });

        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null)
            await vm.LoadFromFileAsync(path);
    }

    private async System.Threading.Tasks.Task<string?> PickSavePathAsync(
        string title, string suggestedName, string extension, string typeName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = new List<FilePickerFileType>
            {
                new(typeName) { Patterns = new[] { "*." + extension } },
            },
        });
        return file?.TryGetLocalPath();
    }
}
