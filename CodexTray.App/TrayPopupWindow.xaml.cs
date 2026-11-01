using CodexTray.Core;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Wpf.Ui.Animations;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Binding = System.Windows.Data.Binding;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
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
    private readonly Dictionary<FrameworkElement, Storyboard> m_Transitions = [];
    private ContentDialog? m_ContentDialog;
    private int m_CurrentPageIndex = -1;
    private bool m_IsClosed;

    /// <summary>
    /// Creates the WPF tray popup window.
    /// </summary>
    public TrayPopupWindow(TrayPopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => m_CurrentPageIndex = PageViewport.Children.IndexOf(PageViewport.Children.OfType<UIElement>().FirstOrDefault(page => page.IsVisible));
        ApplyThemeMode();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>
    /// Applies theme and backdrop changes from the retained view model while this window is open.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is TrayPopupViewModel viewModel)
        {
            switch (args.PropertyName)
            {
                case nameof(TrayPopupViewModel.ThemeMode):
                    ApplyThemeMode();
                    ApplyBackdrop();
                    break;
                case nameof(TrayPopupViewModel.MicaEnabled):
                    ApplyBackdrop();
                    break;
                case nameof(TrayPopupViewModel.IsInAppDialogOpen):
                    if (viewModel.IsInAppDialogOpen)
                    {
                        ShowInAppDialog(viewModel);
                    }
                    else
                    {
                        CloseInAppDialog();
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Releases window subscriptions and dismisses transient dialogs before the view model is reused.
    /// </summary>
    protected override void OnClosed(EventArgs args)
    {
        m_IsClosed = true;
        foreach (FrameworkElement element in m_Transitions.Keys.ToArray())
        {
            ApplyPageTransition(element, Transition.None, 200);
        }
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        CloseInAppDialog();
        if (DataContext is TrayPopupViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.DismissInAppDialog();
        }

        base.OnClosed(args);
    }

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        ApplyPageTransition(sender, Transition.FadeIn, 160);
    }

    private void SessionRegion_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is UIElement { IsVisible: false })
        {
            ApplyPageTransition(sender, Transition.None, 200);
        }
    }

    private void SessionRegion_TargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs args)
    {
        if (args.Property != VisibilityProperty || !ReferenceEquals(args.TargetObject, sender))
        {
            return;
        }
        double offset = sender is Controls.ContentControl { Content: SessionLogListViewModel } ? 30 : -30;
        bool pageIsMoving = sender is DependencyObject region && PageViewport.Children.OfType<FrameworkElement>().Any(page => m_Transitions.ContainsKey(page) && page.IsAncestorOf(region));
        ApplyPageTransition(sender, IsLoaded && !pageIsMoving ? Transition.FadeIn : Transition.None, 200, offset);
    }

    private void MainPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!IsLoaded)
        {
            ApplyPageTransition(sender, Transition.None, 200);
            return;
        }
        if (sender is UIElement element)
        {
            int pageIndex = PageViewport.Children.IndexOf(element);
            Transition transition = pageIndex == m_CurrentPageIndex ? Transition.None : pageIndex > m_CurrentPageIndex ? Transition.SlideRight : Transition.SlideLeft;
            if (element.IsVisible)
            {
                m_CurrentPageIndex = pageIndex;
            }
            ApplyPageTransition(element, transition, 200);
        }
    }

    private void ApplyPageTransition(object sender, Transition transition, int duration, double verticalOffset = 0)
    {
        if (sender is FrameworkElement element)
        {
            if (m_Transitions.Remove(element, out Storyboard? previous))
            {
                previous.Remove(element);
            }
            element.BeginAnimation(OpacityProperty, null);
            if (element.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.BeginAnimation(TranslateTransform.YProperty, null);
            }
            if (transition != Transition.None && element.IsVisible && SystemParameters.ClientAreaAnimation
                && Wpf.Ui.Hardware.HardwareAcceleration.IsSupported(Wpf.Ui.Hardware.RenderingTier.PartialAcceleration))
            {
                Storyboard animation = new() { FillBehavior = FillBehavior.Stop };
                DoubleAnimation fade = new(0, 1, TimeSpan.FromMilliseconds(duration)) { DecelerationRatio = 0.7 };
                Storyboard.SetTarget(fade, element);
                Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
                animation.Children.Add(fade);
                double horizontalOffset = transition == Transition.SlideRight ? 50 : transition == Transition.SlideLeft ? -50 : 0;
                if (horizontalOffset != 0 || verticalOffset != 0)
                {
                    TranslateTransform slide = new();
                    element.SetCurrentValue(RenderTransformProperty, slide);
                    DoubleAnimation move = new(horizontalOffset != 0 ? horizontalOffset : verticalOffset, 0, TimeSpan.FromMilliseconds(duration)) { DecelerationRatio = 0.7 };
                    Storyboard.SetTarget(move, element);
                    Storyboard.SetTargetProperty(move, new PropertyPath("(0).(1)", RenderTransformProperty, horizontalOffset != 0 ? TranslateTransform.XProperty : TranslateTransform.YProperty));
                    animation.Children.Add(move);
                }
                m_Transitions[element] = animation;
                animation.Completed += (_, _) =>
                {
                    // A queued completion must not remove a replacement transition.
                    if (m_Transitions.TryGetValue(element, out Storyboard? current) && ReferenceEquals(current, animation))
                    {
                        m_Transitions.Remove(element);
                        animation.Remove(element);
                    }
                };
                animation.Begin(element, HandoffBehavior.SnapshotAndReplace, true);
            }
        }
    }

    private async void ShowInAppDialog(TrayPopupViewModel viewModel)
    {
        ContentDialog dialog = new(DialogHost)
        {
            DataContext = viewModel,
            DialogMaxWidth = 300,
            DialogMaxHeight = 460,
            DialogMargin = new Thickness(24),
            Padding = new Thickness(20),
            Style = (Style)FindResource("InAppDialogStyle"),
        };
        dialog.SetBinding(ContentDialog.TitleProperty, new Binding(nameof(TrayPopupViewModel.InAppDialogTitle)));
        Controls.TextBlock message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        message.SetBinding(Controls.TextBlock.TextProperty, new Binding(nameof(TrayPopupViewModel.InAppDialogMessage)));
        dialog.Content = message;
        dialog.Loaded += (_, _) =>
        {
            if (dialog.Template.FindName("DialogRootGrid", dialog) is Controls.Grid root)
            {
                // The library shadow rasterizes the entire dialog, including its text.
                foreach (Controls.Border surface in root.Children.OfType<Controls.Border>())
                {
                    surface.Effect = null;
                }
            }
            Page_IsVisibleChanged(dialog, default);
        };
        // Resolve the action on the click, before an asynchronous close can race a replacement request.
        dialog.ButtonClicked += (_, args) =>
        {
            if (!ReferenceEquals(m_ContentDialog, dialog) || !viewModel.IsInAppDialogOpen)
            {
                return;
            }

            CloseInAppDialog();
            if (args.Button == ContentDialogButton.Primary || !viewModel.HasInAppDialogSecondaryButton)
            {
                viewModel.ConfirmInAppDialogCommand.Execute(null);
            }
            else
            {
                viewModel.DismissInAppDialog();
            }
        };
        m_ContentDialog = dialog;
        await dialog.ShowAsync();
        if (ReferenceEquals(m_ContentDialog, dialog))
        {
            m_ContentDialog = null;
            viewModel.DismissInAppDialog();
        }
    }

    private void CloseInAppDialog()
    {
        ContentDialog? dialog = m_ContentDialog;
        m_ContentDialog = null;
        if (dialog != null)
        {
            ApplyPageTransition(dialog, Transition.None, 160);
        }
        dialog?.Hide();
        DialogHost.Content = null;
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs args)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!m_IsClosed && DataContext is TrayPopupViewModel viewModel)
            {
                ApplyThemeMode();
                ApplyBackdrop();
            }
        });
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
        if (DataContext is TrayPopupViewModel { IsPanelPinned: true } or TrayPopupViewModel { IsModalOpen: true } || HasOpenComboBox(this) || HasOpenContextMenu(this))
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

    protected override void OnPreviewKeyDown(Input.KeyEventArgs args)
    {
        base.OnPreviewKeyDown(args);
        // The library's single Close button also serves as OK; Esc must always cancel pending actions.
        if (args.Key == Input.Key.Escape && DataContext is TrayPopupViewModel { IsInAppDialogOpen: true } viewModel)
        {
            viewModel.DismissInAppDialog();
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
    /// Keeps the external session-log bar in sync without taking width away from the columns.
    /// </summary>
    private void SessionLogList_ScrollChanged(object sender, Controls.ScrollChangedEventArgs args)
    {
        if (sender is not Controls.ScrollViewer viewer || FindSessionLogBar(viewer) is not Primitives.ScrollBar bar)
        {
            return;
        }

        bar.Maximum = Math.Max(0, viewer.ScrollableHeight);
        bar.ViewportSize = Math.Max(0, viewer.ViewportHeight);
        bar.LargeChange = Math.Max(1, viewer.ViewportHeight);
        bar.Visibility = viewer.ScrollableHeight > 0 ? Visibility.Visible : Visibility.Hidden;
        if (Math.Abs(bar.Value - viewer.VerticalOffset) > 0.5)
        {
            bar.Value = viewer.VerticalOffset;
        }
    }

    /// <summary>
    /// Scrolls the session-log list from its external bar.
    /// </summary>
    private void SessionLogBar_Scroll(object sender, Primitives.ScrollEventArgs args)
    {
        if (sender is not DependencyObject source || FindSessionLogScroller(source) is not Controls.ScrollViewer viewer)
        {
            return;
        }

        if (Math.Abs(viewer.VerticalOffset - args.NewValue) > 0.5)
        {
            viewer.ScrollToVerticalOffset(args.NewValue);
        }
    }

    /// <summary>
    /// At the list edge, the wheel moves between session-log pages.
    /// </summary>
    private void SessionLogList_PreviewMouseWheel(object sender, Input.MouseWheelEventArgs args)
    {
        if (sender is not Controls.ScrollViewer viewer || viewer.DataContext is not SessionLogListViewModel logs || args.Delta == 0)
        {
            return;
        }

        bool atBottom = viewer.ScrollableHeight <= 1 || viewer.VerticalOffset >= viewer.ScrollableHeight - 1;
        bool atTop = viewer.VerticalOffset <= 1;
        if (args.Delta < 0 && atBottom && logs.CanGoNext)
        {
            logs.NextPage();
            viewer.ScrollToVerticalOffset(0);
            args.Handled = true;
            return;
        }

        if (args.Delta > 0 && atTop && logs.CanGoPrevious)
        {
            logs.PreviousPage();
            viewer.ScrollToVerticalOffset(0);
            args.Handled = true;
        }
    }

    /// <summary>
    /// Returns the session-log list to the top after a pager click.
    /// </summary>
    private void SessionLogPager_Click(object sender, RoutedEventArgs args)
    {
        if (sender is DependencyObject source && FindSessionLogScroller(source) is Controls.ScrollViewer viewer)
        {
            viewer.ScrollToVerticalOffset(0);
        }
    }

    /// <summary>
    /// Finds the external bar that shares a parent with the session-log scroller.
    /// </summary>
    private static Primitives.ScrollBar? FindSessionLogBar(Controls.ScrollViewer viewer)
    {
        if (VisualTreeHelper.GetParent(viewer) is not DependencyObject parent)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            if (VisualTreeHelper.GetChild(parent, index) is Primitives.ScrollBar bar)
            {
                return bar;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the session-log scroller under the nearest parent that contains one.
    /// </summary>
    private static Controls.ScrollViewer? FindSessionLogScroller(DependencyObject source)
    {
        DependencyObject? current = VisualTreeHelper.GetParent(source);
        while (current != null)
        {
            Controls.ScrollViewer? viewer = FindDescendantScrollViewer(current);
            if (viewer != null)
            {
                return viewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// Returns the first scroll viewer under a session-log root.
    /// </summary>
    private static Controls.ScrollViewer? FindDescendantScrollViewer(DependencyObject source)
    {
        int count = VisualTreeHelper.GetChildrenCount(source);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(source, index);
            if (child is Controls.ScrollViewer viewer)
            {
                return viewer;
            }

            Controls.ScrollViewer? nested = FindDescendantScrollViewer(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Switches a two-page quota card with the mouse wheel.
    /// </summary>
    private void QuotaCard_PreviewMouseWheel(object sender, Input.MouseWheelEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: TrayPopupViewModel.QuotaPagerViewModel pager } && pager.HasMultiplePages && args.Delta != 0)
        {
            pager.SelectQuota(args.Delta < 0 ? pager.SecondQuota : pager.FirstQuota);
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
        if (micaSupported)
        {
            DisableMica(handle);
        }

        bool applied = micaSupported && !SystemParameters.HighContrast && DataContext is TrayPopupViewModel { MicaEnabled: true } && TryEnableMica(handle);
        if (!applied)
        {
            WindowChrome.SetWindowChrome(this, null);
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
            RootBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ApplicationBackgroundBrush");
            return;
        }

        // Without the former WPF Fluent theme, we must expose the DWM backdrop through the composition surface.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false,
        });
        WindowBackdrop.RemoveBackground(this);
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
        string? themeMode = (DataContext as TrayPopupViewModel)?.ThemeMode;
        return themeMode == AppSettings.ThemeModeDark
            || (themeMode == AppSettings.ThemeModeSystem && !IsSystemLightTheme());
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
    /// Applies the selected WPF UI theme without enabling the separate WPF Fluent theme manager.
    /// </summary>
    private void ApplyThemeMode()
    {
        ThemesDictionary theme = Resources.MergedDictionaries.OfType<ThemesDictionary>().Single();
        ApplicationTheme effectiveTheme = IsEffectiveDarkTheme() ? ApplicationTheme.Dark : ApplicationTheme.Light;
        if (SystemParameters.HighContrast)
        {
            SystemThemeManager.UpdateSystemThemeCache();
            string themeName = ApplicationThemeManager.GetSystemTheme() switch
            {
                SystemTheme.HC1 => "HC1",
                SystemTheme.HC2 => "HC2",
                SystemTheme.HCBlack => "HCBlack",
                _ => "HCWhite",
            };
            theme.Source = new Uri($"pack://application:,,,/Wpf.Ui;component/Resources/Theme/{themeName}.xaml", UriKind.Absolute);
        }
        else
        {
            theme.Theme = effectiveTheme;
        }

        if (System.Windows.Application.Current != null)
        {
            ApplicationAccentColorManager.Apply(ApplicationAccentColorManager.GetColorizationColor(), effectiveTheme, systemAccentColor: true);
            // Window-scoped dictionaries would otherwise shadow the library's application accent resources.
            foreach (System.Collections.DictionaryEntry resource in Wpf.Ui.UiApplication.Current.Resources)
            {
                Resources[resource.Key] = resource.Value;
            }
        }
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
