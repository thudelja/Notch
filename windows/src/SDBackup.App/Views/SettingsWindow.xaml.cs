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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }
}
