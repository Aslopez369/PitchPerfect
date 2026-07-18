using System;
using System.Drawing;
using System.Windows.Forms;
using PitchPerfect.Services;

namespace PitchPerfect.Services;

/// <summary>
/// Wraps a WinForms <see cref="NotifyIcon"/> to provide a system tray presence
/// for PitchPerfect. Owns the icon lifetime and the context menu, and delegates
/// all behavior decisions to a <see cref="TrayController"/>.
/// </summary>
/// <remarks>
/// The WPF dispatcher loop already pumps the underlying Win32 message queue, so
/// <see cref="NotifyIcon"/> events fire correctly inside a WPF application.
/// </remarks>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayController _controller;
    private readonly Func<bool> _isWindowVisible;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayIconService"/> class.
    /// </summary>
    /// <param name="controller">The behavior controller (must not be null).</param>
    /// <param name="isWindowVisible">Returns the current main-window visibility (must not be null).</param>
    /// <param name="icon">The tray icon to display (must not be null; not owned by this service's caller — dispose separately).</param>
    /// <param name="tooltip">The initial tray tooltip text (refreshed from the controller on each open).</param>
    public TrayIconService(TrayController controller, Func<bool> isWindowVisible, Icon icon, string tooltip)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _isWindowVisible = isWindowVisible ?? throw new ArgumentNullException(nameof(isWindowVisible));
        if (icon is null) throw new ArgumentNullException(nameof(icon));

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMenu(menu);

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = tooltip,
            ContextMenuStrip = menu,
            Visible = true,
        };

        // Double-click toggles the window visibility.
        _notifyIcon.DoubleClick += (_, _) => _controller.ToggleWindowVisibility(_isWindowVisible());

        // Build the initial menu so a right-click before any Opening event still works,
        // and seed the tooltip with the current processing state.
        RebuildMenu(menu);
    }

    /// <summary>
    /// Rebuilds the context menu from the controller's current model. The enabled
    /// state of each item reflects the live window visibility and pitch state, and
    /// the tray tooltip is refreshed to mirror the current processing status.
    /// </summary>
    private void RebuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var visible = _isWindowVisible();

        // Reflect the live processing state in the tray tooltip immediately.
        _notifyIcon.Text = _controller.GetTooltip();

        foreach (var item in _controller.GetMenuItems(visible))
        {
            if (item.Action == TrayMenuAction.Separator)
            {
                menu.Items.Add(new ToolStripSeparator());
                continue;
            }

            if (item.Children is { Count: > 0 })
            {
                var parent = new ToolStripMenuItem(item.Label)
                {
                    Enabled = item.IsEnabled,
                };

                foreach (var child in item.Children)
                {
                    var childItem = new ToolStripMenuItem(child.Label)
                    {
                        Enabled = child.IsEnabled,
                        Checked = child.IsChecked ?? false,
                        Tag = child,
                    };
                    childItem.Click += OnLeafClick;
                    parent.DropDownItems.Add(childItem);
                }

                menu.Items.Add(parent);
                continue;
            }

            // Leaf item (Show / Hide / Exit / TogglePitch / SetPitch).
            var toolItem = new ToolStripMenuItem(item.Label)
            {
                Enabled = item.IsEnabled,
                Checked = item.IsChecked ?? false,
                Tag = item,
            };
            toolItem.Click += OnLeafClick;
            menu.Items.Add(toolItem);
        }
    }

    /// <summary>
    /// Routes a leaf menu click to the controller. The <see cref="TrayMenuItem"/>
    /// (carrying its action and optional pitch payload) is stored in the item's <c>Tag</c>.
    /// </summary>
    private void OnLeafClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem toolItem || toolItem.Tag is not TrayMenuItem item)
        {
            return;
        }

        // Re-evaluate visibility at click time so a stale Opening snapshot can't misroute.
        _controller.Execute(item.Action, _isWindowVisible(), item.PitchValue);
    }

    /// <summary>
    /// Updates the tray tooltip text (e.g. after a view-model state change).
    /// </summary>
    /// <param name="tooltip">The new tooltip text.</param>
    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        _notifyIcon.Text = tooltip;
    }

    /// <summary>
    /// Shows a transient balloon tooltip from the tray icon.
    /// </summary>
    /// <param name="title">The balloon title.</param>
    /// <param name="text">The balloon body text.</param>
    /// <param name="icon">The balloon icon style.</param>
    /// <param name="timeoutMs">Auto-dismiss timeout in milliseconds.</param>
    public void ShowBalloonTip(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, int timeoutMs = 3000)
    {
        if (_disposed) return;
        _notifyIcon.ShowBalloonTip(timeoutMs, title, text, icon);
    }

    /// <summary>
    /// Releases the tray icon and its native resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
