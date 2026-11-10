using System;
using System.Windows;
using SDBackup.Windows;

namespace SDBackup.App;

/// <summary>
/// Settings › Folder structure…: edits the structures new projects get, photos and videos in their own tabs
/// (DataContext: <see cref="FolderStructuresEditor"/>).
/// </summary>
public partial class FolderStructureWindow : Window
{
    public FolderStructureWindow(FolderStructuresEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
        editor.Saved += Close;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }
}
