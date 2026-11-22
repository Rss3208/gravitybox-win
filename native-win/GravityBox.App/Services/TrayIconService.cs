using System;
using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using GravityBox.App.Core;

namespace GravityBox.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly L10nService _l10n;
    private bool _disposed;

    public event Action? LeftClickRequested;
    public event Action? OpenMainWindowRequested;
    public event Action? OpenAntigravityRequested;
    public event Action<string>? LanguageChangeRequested;
    public event Action? ExitRequested;

    public TrayIconService(L10nService l10n, string? iconPath = null)
    {
        _l10n = l10n ?? throw new ArgumentNullException(nameof(l10n));
        Icon icon = (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            ? new Icon(iconPath)
            : SystemIcons.Application;

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "GravityBox",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                LeftClickRequested?.Invoke();
            }
        };

        _l10n.LanguageChanged += (_, _) =>
        {
            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(RebuildContextMenu);
            }
            else
            {
                RebuildContextMenu();
            }
        };
    }

    public void UpdateStatus(string mode, string tooltip)
    {
        string text = string.IsNullOrWhiteSpace(tooltip) ? "GravityBox" : $"GravityBox · {tooltip}";
        // NotifyIcon.Text limit is 63 chars on Windows
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void RebuildContextMenu()
    {
        var old = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = BuildContextMenu();
        old?.Dispose();
    }

    private Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_l10n.T("nav.overview"), null, (_, _) => OpenMainWindowRequested?.Invoke());
        menu.Items.Add(_l10n.T("sidebar.open_antigravity"), null, (_, _) => OpenAntigravityRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var langMenu = new Forms.ToolStripMenuItem(_l10n.T("menu.language"));
        langMenu.DropDownItems.Add("简体中文", null, (_, _) => LanguageChangeRequested?.Invoke("zh-Hans"));
        langMenu.DropDownItems.Add("English", null, (_, _) => LanguageChangeRequested?.Invoke("en"));
        langMenu.DropDownItems.Add(_l10n.T("language.system"), null, (_, _) => LanguageChangeRequested?.Invoke("system"));
        menu.Items.Add(langMenu);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_l10n.T("menu.quit"), null, (_, _) => ExitRequested?.Invoke());
        return menu;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
