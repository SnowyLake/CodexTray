using CodexMonitor.Core;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Input = System.Windows.Input;

namespace CodexMonitor.App;

internal sealed partial class TrayPopupWindow : Window
{
    private const int k_GwlExStyle = -20;
    private const int k_WsExToolWindow = 0x00000080;
    private const int k_WcaAccentPolicy = 19;
    private const int k_DwmwaWindowCornerPreference = 33;
    private const int k_DwmwaSystemBackdropType = 38;
    private const int k_AccentEnableAcrylicBlurBehind = 4;
    private const int k_DwmwcpRound = 2;
    private const int k_DwmsbtTransientWindow = 3;
    private const uint k_AcrylicGradientColor = 0xA8142020;

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
    }

    /// <summary>
    /// Applies window interop attributes after the handle is created.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        HideFromAltTab();
        TryApplyDwmWindowAttributes();
        TryApplyAcrylicBackdrop();
    }

    /// <summary>
    /// Hides the popup when it loses focus.
    /// </summary>
    protected override void OnDeactivated(EventArgs args)
    {
        base.OnDeactivated(args);
        if (DataContext is TrayPopupViewModel { IsModalOpen: true })
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
    /// Hides the popup from the Alt Tab switcher.
    /// </summary>
    private void HideFromAltTab()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int extendedStyle = GetWindowLong(handle, k_GwlExStyle);
        SetWindowLong(handle, k_GwlExStyle, extendedStyle | k_WsExToolWindow);
    }

    /// <summary>
    /// Applies a best-effort Acrylic blur backdrop on supported Windows versions.
    /// </summary>
    private void TryApplyAcrylicBackdrop()
    {
        try
        {
            nint handle = new WindowInteropHelper(this).Handle;
            AccentPolicy accent = new()
            {
                AccentState = k_AccentEnableAcrylicBlurBehind,
                GradientColor = k_AcrylicGradientColor,
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
    /// Sets a Win32 window composition attribute.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Sets a DWM window attribute.
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

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
