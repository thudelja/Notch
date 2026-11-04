using System;
using System.Windows;
using SDBackup.Windows;

namespace SDBackup.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Activated += (_, _) => (DataContext as AppModel)?.RefreshYears();
    }

    private void EditFolderStructure_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AppModel model) return;
        new FolderStructureWindow(model.CreateFolderStructureEditor()) { Owner = this }.ShowDialog();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }
}
