using System;
using System.Drawing;
using System.Windows.Forms;

using SDBackup.Core;

namespace SDBackup.Windows;

/// <summary>
/// The notification-area icon (bottom right, next to Wi-Fi and the clock). Left-click opens the panel;
/// right-click shows a menu. Its balloon notifications appear as Windows 10/11 toasts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _normal;
    private readonly Icon _attention;

    public event Action? Clicked;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public TrayIcon(Icon normal, Icon attention)
    {
        _normal = normal;
        _attention = attention;
        var menu = new ContextMenuStrip();
        menu.Items.Add(Loc.T("Open Notch"), null, (_, _) => Clicked?.Invoke());
        menu.Items.Add(Loc.T("Settings…"), null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("Quit Notch"), null, (_, _) => QuitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = normal,
            Text = "Notch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Clicked?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => Clicked?.Invoke();
    }

    /// <summary>Shows a badge and tooltip when cards are waiting for a project.</summary>
    public void SetWaitingCount(int count)
    {
        _icon.Icon = count > 0 ? _attention : _normal;
        _icon.Text = count switch
        {
            0 => "Notch",
            _ => Loc.Plural(count, "Notch – {0} card waiting for a project", "Notch – {0} cards waiting for a project"),
        };
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}

/// <summary>Native Windows dialogs: task dialogs for questions, the system folder picker.</summary>
public sealed class WindowsDialogs : IDialogs
{
    public bool Confirm(string title, string message, string confirmText)
    {
        var confirm = new TaskDialogButton(confirmText);
        var page = new TaskDialogPage
        {
            Caption = "Notch",
            Heading = title,
            Text = message,
            Icon = TaskDialogIcon.Warning,
            Buttons = { confirm, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
            AllowCancel = true,
        };
        return TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) == confirm;
    }

    public void Alert(string title, string message)
    {
        var page = new TaskDialogPage
        {
            Caption = "Notch",
            Heading = title,
            Text = message,
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK },
        };
        TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
    }

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };
        if (initialFolder is not null && System.IO.Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>Answers every question with "no" and records messages. Used by the self-test.</summary>
public sealed class SilentDialogs : IDialogs
{
    public bool Confirm(string title, string message, string confirmText) => false;
    public void Alert(string title, string message) { }
    public string? PickFolder(string title, string? initialFolder) => null;
}
