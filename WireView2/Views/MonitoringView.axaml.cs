using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WireView2.ViewModels;

namespace WireView2.Views;

public partial class MonitoringView : UserControl
{
    public MonitoringView()
    {
        InitializeComponent();
    }

    private async void OnExportCsvClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitoringViewModel vm) return;

        if (vm.IsExportingCsv)
        {
            vm.StopCsvExport();
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export live monitoring to CSV",
            SuggestedFileName = $"wireview-monitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("CSV") { Patterns = new[] { "*.csv" } },
            },
        });

        string? path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            vm.StartCsvExport(path);
    }
}
