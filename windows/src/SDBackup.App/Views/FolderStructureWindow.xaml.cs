using System;
using System.Windows;
using SDBackup.Windows;

namespace SDBackup.App;

/// <summary>Settings › Folder structure…: edits the structure new projects get (DataContext: <see cref="FolderStructureEditor"/>).</summary>
public partial class FolderStructureWindow : Window
{
    public FolderStructureWindow(FolderStructureEditor editor)
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
