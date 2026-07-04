using CodexMonitor.Core;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Input = System.Windows.Input;

namespace CodexMonitor.App;

internal sealed partial class TrayPopupWindow : Window
{
    private const int k_GwlExStyle = -20;
    private const int k_WsExToolWindow = 0x00000080;
    private const int k_DwmwaWindowCornerPreference = 33;
    private const int k_DwmwaSystemBackdropType = 38;
    private const int k_DwmwcpRound = 2;
    private const int k_DwmsbtTransientWindow = 3;

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
            if (args.PropertyName == nameof(TrayPopupViewModel.ThemeMode))
            {
                ApplyThemeMode(viewModel.ThemeMode);
            }
        };
    }

    /// <summary>
    /// Shows the popup near the notification area.
    /// </summary>
    public void ShowNearTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        PositionNearTray();
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
        TryApplyDwmWindowAttributes();
    }

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

        Hide();
    }

    /// <summary>
    /// Handles keyboard shortcuts for the popup.
    /// </summary>
    protected override void OnKeyDown(Input.KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Key == Input.Key.Escape)
        {
            Hide();
            args.Handled = true;
        }
    }

    /// <summary>
    /// Positions the popup near the current screen work area.
    /// </summary>
    private void PositionNearTray()
    {
        Forms.Screen screen = Forms.Screen.FromPoint(Forms.Control.MousePosition);
        double scaleX = 1.0;
        double scaleY = 1.0;
        PresentationSource? source = PresentationSource.FromVisual(this);
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
    /// Applies Windows 11 DWM rounded corners and transient backdrop when available.
    /// </summary>
    private void TryApplyDwmWindowAttributes()
    {
        try
        {
            nint handle = new WindowInteropHelper(this).Handle;
            int cornerPreference = k_DwmwcpRound;
            DwmSetWindowAttribute(handle, k_DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

            int backdropType = k_DwmsbtTransientWindow;
            DwmSetWindowAttribute(handle, k_DwmwaSystemBackdropType, ref backdropType, sizeof(int));
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
}
