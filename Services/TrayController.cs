using System;
using System.Collections.Generic;
using PitchPerfect.Models;
using PitchPerfect.ViewModels;

namespace PitchPerfect.Services;

/// <summary>
/// UI-agnostic controller for the system tray behavior of PitchPerfect.
/// It decouples the window-visibility policy and context-menu model from the
/// concrete <see cref="System.Windows.Forms.NotifyIcon"/> plumbing so the
/// decision logic can be unit-tested headlessly (no Windows session required).
/// </summary>
/// <remarks>
/// The controller depends only on the <see cref="MainViewModel"/> (a plain
/// command/state holder) and on the menu model below — never on WinForms or
/// WPF UI types — so it stays testable and free of tray plumbing concerns.
/// </remarks>
public sealed class TrayController
{
    private readonly Action _showWindow;
    private readonly Action _hideWindow;
    private readonly Action _exitApplication;
    private readonly MainViewModel _viewModel;
    private readonly Action<string, string>? _showBalloon;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayController"/> class.
    /// </summary>
    /// <param name="showWindow">Callback that makes the main window visible.</param>
    /// <param name="hideWindow">Callback that hides the main window.</param>
    /// <param name="exitApplication">Callback that fully shuts the application down.</param>
    /// <param name="viewModel">The main view model (source of pitch state and commands).</param>
    /// <param name="showBalloon">Optional callback used to surface a balloon tip (e.g. missing VB-Cable).</param>
    public TrayController(
        Action showWindow,
        Action hideWindow,
        Action exitApplication,
        MainViewModel viewModel,
        Action<string, string>? showBalloon = null)
    {
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _hideWindow = hideWindow ?? throw new ArgumentNullException(nameof(hideWindow));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _showBalloon = showBalloon;
    }

    /// <summary>
    /// Toggles the window visibility. Returns the resulting visibility state
    /// (<c>true</c> when the window ends up visible).
    /// </summary>
    /// <param name="currentlyVisible">The current visibility state of the window.</param>
    /// <returns>The new visibility state after the toggle.</returns>
    public bool ToggleWindowVisibility(bool currentlyVisible)
    {
        if (currentlyVisible)
        {
            _hideWindow();
            return false;
        }

        _showWindow();
        return true;
    }

    /// <summary>Shows the main window (idempotent with respect to visibility).</summary>
    public void Show() => _showWindow();

    /// <summary>Hides the main window (idempotent with respect to visibility).</summary>
    public void Hide() => _hideWindow();

    /// <summary>Requests a full application shutdown.</summary>
    public void Exit() => _exitApplication();

    /// <summary>
    /// Builds the context-menu model based on the current window visibility and
    /// the live pitch-processing state from the view model.
    /// </summary>
    /// <param name="windowVisible">Whether the main window is currently visible.</param>
    /// <returns>An ordered, read-only list of tray menu items.</returns>
    public IReadOnlyList<TrayMenuItem> GetMenuItems(bool windowVisible)
    {
        var items = new List<TrayMenuItem>();

        // --- Pitch on/off toggle (top dynamic item) ---
        // Kept enabled even without VB-Cable so a click can surface the
        // "install VB-Cable" balloon; the actual start is gated by the command.
        items.Add(_viewModel.IsGlobalProcessing
            ? new TrayMenuItem("⏸ 停止变调", TrayMenuAction.TogglePitch, true)
            : new TrayMenuItem("▶ 开始变调", TrayMenuAction.TogglePitch, true));

        // --- Pitch level sub-menu (-12..+12 semitones, step 1) ---
        var currentPitch = Math.Round(_viewModel.GlobalPitchSemiTones);
        var pitchChildren = new List<TrayMenuItem>(25);
        for (var p = -12; p <= 12; p++)
        {
            pitchChildren.Add(new TrayMenuItem(
                label: $"{p:+0;-0;0} key",
                action: TrayMenuAction.SetPitch,
                isEnabled: true,
                isChecked: p == currentPitch,
                pitchValue: p));
        }

        items.Add(new TrayMenuItem(
            label: $"音调 (Pitch) — {_viewModel.GlobalPitchDisplay}",
            children: pitchChildren));

        // --- Vocal removal (karaoke) toggle ---
        items.Add(new TrayMenuItem(
            label: "🎤 消除人声（仅伴奏）",
            action: TrayMenuAction.ToggleVocalRemoval,
            isEnabled: _viewModel.VocalRemovalApplicable,
            isChecked: _viewModel.VocalRemovalEnabled));

        // --- Visual separator before window/exit items ---
        items.Add(TrayMenuItem.Separator);

        // --- Window visibility + exit (bottom) ---
        items.Add(new TrayMenuItem("显示窗口", TrayMenuAction.Show, !windowVisible));
        items.Add(new TrayMenuItem("隐藏窗口", TrayMenuAction.Hide, windowVisible));
        items.Add(new TrayMenuItem("退出 PitchPerfect", TrayMenuAction.Exit, true));

        return items;
    }

    /// <summary>
    /// Routes a tray menu action to the corresponding behavior.
    /// No-ops for actions that are not valid in the current visibility state
    /// (e.g. Show while already visible) to avoid redundant work.
    /// </summary>
    /// <param name="action">The selected menu action.</param>
    /// <param name="windowVisible">The current visibility state of the window.</param>
    /// <param name="pitchValue">
    /// Optional payload for <see cref="TrayMenuAction.SetPitch"/> (semitone offset).
    /// </param>
    public void Execute(TrayMenuAction action, bool windowVisible, float? pitchValue = null)
    {
        switch (action)
        {
            case TrayMenuAction.Show:
                if (!windowVisible) _showWindow();
                break;
            case TrayMenuAction.Hide:
                if (windowVisible) _hideWindow();
                break;
            case TrayMenuAction.Exit:
                _exitApplication();
                break;
            case TrayMenuAction.TogglePitch:
                TogglePitch();
                break;
            case TrayMenuAction.SetPitch:
                if (pitchValue.HasValue) SetPitch(pitchValue.Value);
                break;
            case TrayMenuAction.ToggleVocalRemoval:
                ToggleVocalRemoval();
                break;
            case TrayMenuAction.Separator:
            case TrayMenuAction.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown tray menu action.");
        }
    }

    /// <summary>
    /// Computes the tray tooltip reflecting the live processing state.
    /// </summary>
    /// <returns>The tooltip text for the notification-area icon.</returns>
    public string GetTooltip()
    {
        var baseText = _viewModel.IsGlobalProcessing
            ? $"PitchPerfect — 运行中 · {_viewModel.GlobalPitchDisplay}"
            : "PitchPerfect — 全局变调: 关";

        if (_viewModel.VocalRemovalEnabled)
        {
            baseText += " · 消除人声";
        }

        return baseText;
    }

    /// <summary>
    /// Toggles global pitch processing on or off through the view model.
    /// When turning on without VB-Cable installed, a balloon tip is shown and
    /// nothing else happens (the start command's CanExecute would block it anyway).
    /// </summary>
    private void TogglePitch()
    {
        if (_viewModel.IsGlobalProcessing)
        {
            _viewModel.StopGlobalCommand.Execute(null);
            return;
        }

        if (!_viewModel.IsVBcableInstalled)
        {
            _showBalloon?.Invoke("需要 VB-Cable", "请先安装 VB-Cable 才能变调");
            return;
        }

        // Switch to Global mode first (this stops any per-app processing via
        // the view model's OnCurrentModeChanged), then start global processing.
        _viewModel.CurrentMode = ProcessingMode.Global;
        _viewModel.StartGlobalCommand.Execute(null);
    }

    /// <summary>
    /// Sets the global pitch to the supplied value (clamped to the -12..+12 range).
    /// If processing is active, the view model propagates the change in real time.
    /// </summary>
    /// <param name="value">The desired pitch offset in semitones.</param>
    private void SetPitch(float value)
    {
        var clamped = Math.Clamp(value, -12f, 12f);
        _viewModel.GlobalPitchSemiTones = clamped;
    }

    /// <summary>
    /// Toggles the vocal-removal (instrumental-only) feature through the view model.
    /// No-ops when the current capture source is not applicable (mono).
    /// </summary>
    private void ToggleVocalRemoval()
    {
        if (!_viewModel.VocalRemovalApplicable) return;
        _viewModel.VocalRemovalEnabled = !_viewModel.VocalRemovalEnabled;
    }
}

/// <summary>
/// A single, UI-agnostic tray context-menu entry produced by <see cref="TrayController"/>.
/// </summary>
public readonly struct TrayMenuItem
{
    /// <summary>Gets the display label (empty for separators).</summary>
    public string Label { get; }

    /// <summary>Gets the logical action this item represents.</summary>
    public TrayMenuAction Action { get; }

    /// <summary>Gets a value indicating whether the item is enabled (ignored for separators).</summary>
    public bool IsEnabled { get; }

    /// <summary>Gets the optional sub-menu items. When present, the item acts as a parent.</summary>
    public IReadOnlyList<TrayMenuItem>? Children { get; }

    /// <summary>Gets the optional checked state for check/radio style items. Null means no check.</summary>
    public bool? IsChecked { get; }

    /// <summary>Gets the optional payload (e.g. pitch value in semitones) carried for leaf actions.</summary>
    public float? PitchValue { get; }

    /// <summary>
    /// Initializes a leaf tray menu item (backward-compatible signature).
    /// </summary>
    public TrayMenuItem(string label, TrayMenuAction action, bool isEnabled)
        : this(label, action, isEnabled, null, null, null)
    {
    }

    /// <summary>
    /// Initializes a leaf tray menu item with an optional checked state and payload.
    /// </summary>
    public TrayMenuItem(string label, TrayMenuAction action, bool isEnabled, bool? isChecked, float? pitchValue = null)
        : this(label, action, isEnabled, null, isChecked, pitchValue)
    {
    }

    /// <summary>
    /// Initializes a parent tray menu item that owns a sub-menu.
    /// </summary>
    public TrayMenuItem(string label, IReadOnlyList<TrayMenuItem> children)
        : this(label, TrayMenuAction.None, true, children, null, null)
    {
    }

    private TrayMenuItem(
        string label,
        TrayMenuAction action,
        bool isEnabled,
        IReadOnlyList<TrayMenuItem>? children,
        bool? isChecked,
        float? pitchValue)
    {
        Label = label;
        Action = action;
        IsEnabled = isEnabled;
        Children = children;
        IsChecked = isChecked;
        PitchValue = pitchValue;
    }

    /// <summary>Gets a visual separator item (has no action or behavior).</summary>
    public static TrayMenuItem Separator => new TrayMenuItem(string.Empty, TrayMenuAction.Separator, false);
}

/// <summary>
/// Logical actions exposed through the system tray context menu.
/// </summary>
public enum TrayMenuAction
{
    /// <summary>Placeholder / no-op action (used by parent menu items).</summary>
    None,

    /// <summary>Show the main window.</summary>
    Show,

    /// <summary>Hide the main window (send to tray).</summary>
    Hide,

    /// <summary>Exit the application completely.</summary>
    Exit,

    /// <summary>Toggle global pitch processing on or off.</summary>
    TogglePitch,

    /// <summary>Set the global pitch to the value carried by the item.</summary>
    SetPitch,

    /// <summary>Toggle vocal removal (instrumental-only / karaoke mode).</summary>
    ToggleVocalRemoval,

    /// <summary>Visual separator (no behavior).</summary>
    Separator,
}
