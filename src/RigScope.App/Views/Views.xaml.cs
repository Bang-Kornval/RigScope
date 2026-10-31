using System.Windows.Controls;
using System.Windows.Input;
using RigScope.App.ViewModels;
using RigScope.Core.Storage;

namespace RigScope.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();
}

public partial class CheckView : UserControl
{
    public CheckView() => InitializeComponent();
}

public partial class StorageView : UserControl
{
    public StorageView() => InitializeComponent();

    private StorageViewModel? Vm => DataContext as StorageViewModel;

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        Vm?.OpenRowCommand.Execute(FolderList.SelectedItem as FolderRow);

    private void FolderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm?.OpenRowCommand.Execute(FolderList.SelectedItem as FolderRow); e.Handled = true; }
        else if (e.Key == Key.Back) { Vm?.UpCommand.Execute(null); e.Handled = true; }
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is LargeFile f) Vm?.ShowInExplorerCommand.Execute(f.Path);
    }
}

public partial class MeasureView : UserControl
{
    public MeasureView() => InitializeComponent();
}

public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
