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
    private const int k_DwmwaWindowCornerPreference = 33;
    private const int k_DwmwcpRound = 2;
    private const int k_WcaAccentPolicy = 19;
    private const int k_AccentDisabled = 0;
    private const int k_AccentEnableAcrylicBlurBehind = 4;

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
                case nameof(TrayPopupViewModel.AcrylicEnabled):
                case nameof(TrayPopupViewModel.AcrylicOpacityPercent):
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
        if (DataContext is TrayPopupViewModel { IsModalOpen: true } || HasOpenComboBox(this))
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
    /// Opens the token cost item selection menu below its button.
    /// </summary>
    private void OpenTokenCostItemsMenu(object sender, RoutedEventArgs args)
    {
        if (sender is Controls.Button { ContextMenu: { } contextMenu } button)
        {
            contextMenu.PlacementTarget = button;
            contextMenu.IsOpen = true;
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
        try
        {
            nint handle = new WindowInteropHelper(this).Handle;
            int cornerPreference = k_DwmwcpRound;
            DwmSetWindowAttribute(handle, k_DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Applies or removes the acrylic blur backdrop based on the current settings.
    /// </summary>
    private void ApplyBackdrop()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        bool acrylicEnabled = DataContext is TrayPopupViewModel { AcrylicEnabled: true };
        if (acrylicEnabled)
        {
            RootBorder.Background = System.Windows.Media.Brushes.Transparent;
            SetAccentPolicy(handle, k_AccentEnableAcrylicBlurBehind, ResolveAcrylicGradientColor());
        }
        else
        {
            RootBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ApplicationBackgroundBrush");
            SetAccentPolicy(handle, k_AccentDisabled, 0);
        }
    }

    /// <summary>
    /// Builds the acrylic tint color as 0xAABBGGRR from theme and opacity settings.
    /// </summary>
    private uint ResolveAcrylicGradientColor()
    {
        int opacityPercent = DataContext is TrayPopupViewModel viewModel
            ? viewModel.AcrylicOpacityPercent
            : CodexTrayDefaults.AcrylicOpacityPercent;
        opacityPercent = Math.Clamp(
            opacityPercent,
            CodexTrayDefaults.MinimumAcrylicOpacityPercent,
            CodexTrayDefaults.MaximumAcrylicOpacityPercent);
        uint alpha = (uint)Math.Round(opacityPercent * 255.0 / 100.0);

        // Tint RGB tracks the effective theme so the blur reads correctly on light and dark.
        (uint red, uint green, uint blue) = IsEffectiveDarkTheme()
            ? (0x20u, 0x20u, 0x20u)
            : (0xF3u, 0xF3u, 0xF3u);

        return (alpha << 24) | (blue << 16) | (green << 8) | red;
    }

    /// <summary>
    /// Returns true when the effective theme resolves to dark.
    /// </summary>
    private bool IsEffectiveDarkTheme()
    {
        return ThemeMode == System.Windows.ThemeMode.Dark
            || (ThemeMode == System.Windows.ThemeMode.System && !IsSystemLightTheme());
    }

    /// <summary>
    /// Reads the system apps-use-light-theme preference.
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
    /// Sends an accent policy to the window composition attribute.
    /// </summary>
    private static void SetAccentPolicy(nint handle, int accentState, uint gradientColor)
    {
        try
        {
            AccentPolicy accent = new()
            {
                AccentState = accentState,
                GradientColor = gradientColor,
            };
            int accentSize = Marshal.SizeOf<AccentPolicy>();
            nint accentPointer = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPointer, false);
                WindowCompositionAttributeData data = new()
                {
                    Attribute = k_WcaAccentPolicy,
                    Data = accentPointer,
                    SizeOfData = accentSize,
                };
                SetWindowCompositionAttribute(handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPointer);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
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

    /// <summary>
    /// Sets a Win32 window composition attribute.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;

        public int AccentFlags;

        public uint GradientColor;

        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;

        public nint Data;

        public int SizeOfData;
    }
}
