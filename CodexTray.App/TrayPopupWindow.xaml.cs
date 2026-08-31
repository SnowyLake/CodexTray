using CodexTray.Core;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Controls = System.Windows.Controls;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Input = System.Windows.Input;

namespace CodexTray.App;

internal sealed partial class TrayPopupWindow : Window
{
    private const int k_GwlExStyle = -20;
    private const int k_WsExToolWindow = 0x00000080;
    private const int k_DwmwaUseImmersiveDarkMode = 20;
    private const int k_DwmwaWindowCornerPreference = 33;
    private const int k_DwmwaSystemBackdropType = 38;
    private const int k_DwmwaMicaEffect = 1029;
    private const int k_DwmwcpRound = 2;
    private const int k_DwmsbtNone = 1;
    private const int k_DwmsbtMainWindow = 2;

    /// <summary>
    /// Creates the WPF tray popup window.
    /// </summary>
    public TrayPopupWindow(TrayPopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ApplyThemeMode(viewModel.ThemeMode);
        viewModel.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(TrayPopupViewModel.ThemeMode):
                    ApplyThemeMode(viewModel.ThemeMode);
                    ApplyBackdrop();
                    break;
                case nameof(TrayPopupViewModel.MicaEnabled):
                    ApplyBackdrop();
                    break;
            }
        };
    }

    /// <summary>
    /// Shows the popup near a visible tray icon or at the work area corner.
    /// </summary>
    public void ShowNearTray(Drawing.Point? trayIconPosition)
    {
        WindowState = WindowState.Normal;
        new WindowInteropHelper(this).EnsureHandle();
        PositionNearTray(trayIconPosition);

        if (!IsVisible)
        {
            Show();
        }

        Activate();
        ForceForeground();
        ApplyBackdrop();
    }

    /// <summary>
    /// Forces the popup to the foreground so it can receive keyboard input.
    /// </summary>
    private void ForceForeground()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        nint foregroundWindow = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foregroundWindow, out _);
        uint currentThread = GetCurrentThreadId();
        if (foregroundThread != currentThread && foregroundThread != 0)
        {
            AttachThreadInput(currentThread, foregroundThread, true);
            SetForegroundWindow(handle);
            AttachThreadInput(currentThread, foregroundThread, false);
        }
        else
        {
            SetForegroundWindow(handle);
        }

        Activate();
        Focus();
    }

    /// <summary>
    /// Applies window interop attributes after the handle is created.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        HideFromAltTab();
        TryApplyRoundedCorners();
        ApplyBackdrop();
    }

    /// <summary>
    /// Timestamp of the last hide triggered by losing focus, used to debounce tray icon clicks.
    /// </summary>
    public DateTime LastDeactivatedHideUtc { get; private set; }

    /// <summary>
    /// Hides the popup when it loses focus.
    /// </summary>
    protected override void OnDeactivated(EventArgs args)
    {
        base.OnDeactivated(args);
        if (DataContext is TrayPopupViewModel { IsModalOpen: true } || HasOpenComboBox(this) || HasOpenContextMenu(this))
        {
            return;
        }

        LastDeactivatedHideUtc = DateTime.UtcNow;
        Hide();
    }

    /// <summary>
    /// Handles keyboard shortcuts for the popup.
    /// </summary>
    protected override void OnKeyDown(Input.KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Key == Input.Key.Escape && DataContext is TrayPopupViewModel { IsInAppDialogOpen: true } viewModel)
        {
            viewModel.DismissInAppDialog();
            args.Handled = true;
            return;
        }

        if (args.Key == Input.Key.Escape)
        {
            Hide();
            args.Handled = true;
        }
    }

    /// <summary>
    /// Opens a settings selection menu below its button.
    /// </summary>
    private void OpenSelectionMenu(object sender, RoutedEventArgs args)
    {
        if (sender is Controls.Button { ContextMenu: { } contextMenu } button)
        {
            contextMenu.PlacementTarget = button;
            contextMenu.IsOpen = true;
            args.Handled = true;
        }
    }

    /// <summary>
    /// Switches the Cursor large quota card with the mouse wheel.
    /// </summary>
    private void CursorMonthlyCard_PreviewMouseWheel(object sender, Input.MouseWheelEventArgs args)
    {
        if (DataContext is TrayPopupViewModel viewModel && args.Delta != 0)
        {
            viewModel.ShowCursorQuota(args.Delta < 0 ? viewModel.CursorGrokBotQuota : viewModel.CursorMonthlyQuota);
            args.Handled = true;
        }
    }

    /// <summary>
    /// Positions the popup near a visible tray icon or at the work area corner.
    /// </summary>
    private void PositionNearTray(Drawing.Point? trayIconPosition)
    {
        Forms.Screen screen = Forms.Screen.FromPoint(trayIconPosition ?? Forms.Control.MousePosition);
        double scaleX = 1.0;
        double scaleY = 1.0;
        HwndSource? source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source?.CompositionTarget != null)
        {
            Matrix transform = source.CompositionTarget.TransformFromDevice;
            scaleX = transform.M11;
            scaleY = transform.M22;
        }

        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        Rect workArea = new(
            screen.WorkingArea.Left * scaleX,
            screen.WorkingArea.Top * scaleY,
            screen.WorkingArea.Width * scaleX,
            screen.WorkingArea.Height * scaleY);

        Left = workArea.Right - width - 10;
        Top = workArea.Bottom - height - 10;

        if (trayIconPosition is not { } iconPosition
            || !screen.Bounds.Contains(iconPosition)
            || screen.WorkingArea.Contains(iconPosition))
        {
            return;
        }

        double minimumLeft = workArea.Left + 10;
        double maximumLeft = Math.Max(minimumLeft, workArea.Right - width - 10);
        double minimumTop = workArea.Top + 10;
        double maximumTop = Math.Max(minimumTop, workArea.Bottom - height - 10);
        double centeredLeft = Math.Clamp(iconPosition.X * scaleX - width / 2, minimumLeft, maximumLeft);
        double centeredTop = Math.Clamp(iconPosition.Y * scaleY - height / 2, minimumTop, maximumTop);

        if (iconPosition.Y < screen.WorkingArea.Top)
        {
            Left = centeredLeft;
            Top = minimumTop;
        }
        else if (iconPosition.Y >= screen.WorkingArea.Bottom)
        {
            Left = centeredLeft;
            Top = maximumTop;
        }
        else if (iconPosition.X < screen.WorkingArea.Left)
        {
            Left = minimumLeft;
            Top = centeredTop;
        }
        else
        {
            Left = maximumLeft;
            Top = centeredTop;
        }
    }

    /// <summary>
    /// Returns true when a combo box in the popup is currently open.
    /// </summary>
    private static bool HasOpenComboBox(DependencyObject parent)
    {
        if (parent is Controls.ComboBox { IsDropDownOpen: true })
        {
            return true;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            if (HasOpenComboBox(VisualTreeHelper.GetChild(parent, index)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when a context menu in the popup is currently open.
    /// </summary>
    private static bool HasOpenContextMenu(DependencyObject parent)
    {
        if (parent is FrameworkElement { ContextMenu.IsOpen: true })
        {
            return true;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            if (HasOpenContextMenu(VisualTreeHelper.GetChild(parent, index)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Hides the popup from the Alt Tab switcher.
    /// </summary>
    private void HideFromAltTab()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int extendedStyle = GetWindowLong(handle, k_GwlExStyle);
        SetWindowLong(handle, k_GwlExStyle, extendedStyle | k_WsExToolWindow);
    }

    /// <summary>
    /// Applies Windows 11 DWM rounded corners when available.
    /// </summary>
    private void TryApplyRoundedCorners()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        _ = TrySetDwmWindowAttribute(handle, k_DwmwaWindowCornerPreference, k_DwmwcpRound);
    }

    /// <summary>
    /// Applies the selected Windows backdrop or a solid fallback.
    /// </summary>
    private void ApplyBackdrop()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        bool micaSupported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        if (!micaSupported || DataContext is not TrayPopupViewModel viewModel)
        {
            RootBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ApplicationBackgroundBrush");
            return;
        }

        DisableMica(handle);
        bool applied = viewModel.MicaEnabled && TryEnableMica(handle);
        if (!applied)
        {
            RootBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ApplicationBackgroundBrush");
            return;
        }

        RootBorder.Background = System.Windows.Media.Brushes.Transparent;
    }

    /// <summary>
    /// Removes the Mica effect before applying the current setting.
    /// </summary>
    private static void DisableMica(nint handle)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            _ = TrySetDwmWindowAttribute(handle, k_DwmwaSystemBackdropType, k_DwmsbtNone);
            return;
        }

        _ = TrySetDwmWindowAttribute(handle, k_DwmwaMicaEffect, 0);
    }

    /// <summary>
    /// Enables the native Mica backdrop supported by the current Windows 11 build.
    /// </summary>
    private bool TryEnableMica(nint handle)
    {
        _ = TrySetDwmWindowAttribute(handle, k_DwmwaUseImmersiveDarkMode, IsEffectiveDarkTheme() ? 1 : 0);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return TrySetDwmWindowAttribute(handle, k_DwmwaSystemBackdropType, k_DwmsbtMainWindow);
        }

        // Windows 11 21H2 predates the public system backdrop attribute.
        return TrySetDwmWindowAttribute(handle, k_DwmwaMicaEffect, 1);
    }

    /// <summary>
    /// Returns whether the effective application theme is dark.
    /// </summary>
    private bool IsEffectiveDarkTheme()
    {
        return ThemeMode == System.Windows.ThemeMode.Dark
            || (ThemeMode == System.Windows.ThemeMode.System && !IsSystemLightTheme());
    }

    /// <summary>
    /// Reads the system application theme preference.
    /// </summary>
    private static bool IsSystemLightTheme()
    {
        try
        {
            object? value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int flag && flag != 0;
        }
        catch (Exception exception) when (exception is IOException or System.Security.SecurityException)
        {
            return true;
        }
    }

    /// <summary>
    /// Sets one DWM window attribute and reports whether the platform accepted it.
    /// </summary>
    private static bool TrySetDwmWindowAttribute(nint handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) >= 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the selected WPF Fluent theme mode.
    /// </summary>
    private void ApplyThemeMode(string themeMode)
    {
        ThemeMode = themeMode switch
        {
            AppSettings.ThemeModeLight => System.Windows.ThemeMode.Light,
            AppSettings.ThemeModeDark => System.Windows.ThemeMode.Dark,
            _ => System.Windows.ThemeMode.System,
        };
    }

    /// <summary>
    /// Reads a Win32 window style value.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    /// <summary>
    /// Writes a Win32 window style value.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// Sets a DWM window attribute.
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// Brings a window to the foreground.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>
    /// Gets the current foreground window handle.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    /// <summary>
    /// Gets the thread that owns a window.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>
    /// Attaches or detaches the input processing of two threads.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

    /// <summary>
    /// Gets the current thread identifier.
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

}
