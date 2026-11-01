using CodexTray.App;
using CodexTray.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexTray.Tests;

internal static class PageTransitionTests
{
    internal static Task RunAsync()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            Application? application = null;
            TrayPopupWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                TrayPopupViewModel viewModel = new(new AppSettings { MicaEnabled = false }, () => Task.CompletedTask);
                viewModel.TogglePanelPinnedCommand.Execute(null);
                window = new TrayPopupWindow(viewModel);
                window.Show();
                Pump();
                bool animationsEnabled = SystemParameters.ClientAreaAnimation && Wpf.Ui.Hardware.HardwareAcceleration.IsSupported(Wpf.Ui.Hardware.RenderingTier.PartialAcceleration);
                CheckChartAnimations(window, viewModel, animationsEnabled);
                foreach (string source in new[] { "Codex", "Grok" })
                {
                    SessionLogListViewModel logs = source == "Codex" ? viewModel.CodexSessionLogs : viewModel.GrokSessionLogs;
                    Grid page = (Grid)window.FindName(source + "PageGrid");
                    UIElement[] regions = [
                        (UIElement)window.FindName(source + "SessionLogContent"),
                        (UIElement)window.FindName(source + "TokenCostDashboard"),
                        (UIElement)window.FindName(source + "QuotaScrollViewer"),
                    ];
                    for (int iteration = 0; iteration < 2; iteration++)
                    {
                        if (logs.IsOpen != (iteration == 1))
                        {
                            logs.Toggle();
                            Pump(250);
                        }
                        if (source == "Codex")
                        {
                            viewModel.ShowGrok();
                            Pump();
                            viewModel.ShowCodex();
                        }
                        else
                        {
                            viewModel.ShowCodex();
                            Pump();
                            viewModel.ShowGrok();
                        }
                        Pump();
                        Check(regions.All(IsIdle), "switching main pages must not start nested slides or fades");
                        logs.Toggle();
                        Pump();
                        Check(regions.All(IsIdle), "a session toggle during a main-page slide must not add a nested animation");
                        Pump(250);
                        Check(IsIdle(page) && regions.All(IsIdle), "completed main-page transitions must release every clock");
                        logs.Toggle();
                        Pump();
                        if (animationsEnabled)
                        {
                            Check(HasSlide(regions[logs.IsOpen ? 0 : 1]), "session toggles after the main transition must still animate");
                        }
                        Check(IsIdle(page), "session transitions must not animate the page");
                        window.Hide();
                        Pump();
                        Check(IsIdle(page) && regions.All(IsIdle), "hiding mid-transition must clear slide and opacity clocks");
                        window.ShowNearTray(null);
                        Pump();
                        Check(IsIdle(page) && regions.All(IsIdle), "showing the same page must not restart page or session animations");
                        logs.Toggle();
                        Pump();
                        logs.Toggle();
                        Pump();
                        logs.Toggle();
                        Pump(250);
                        Check(regions.All(IsIdle), "rapid session reversals must settle and release every clock");
                        window.Hide();
                        logs.Toggle();
                        Pump();
                        window.ShowNearTray(null);
                        Pump();
                        Check(IsIdle(page) && regions.All(IsIdle), "session changes while hidden must not animate on reopening");
                    }
                }
                for (int iteration = 0; iteration < 4; iteration++)
                {
                    viewModel.ShowCodex();
                    viewModel.ShowGrok();
                    viewModel.ShowApi();
                    viewModel.ShowSettings();
                    viewModel.ShowAbout();
                    viewModel.ShowCodex();
                    Pump();
                }
                Grid viewport = (Grid)window.FindName("PageViewport");
                Pump(250);
                Check(viewport.Children.OfType<UIElement>().All(IsIdle), "rapid main-page replacements must settle without old callbacks affecting the current page");
                viewModel.ShowGrok();
                Pump(130);
                viewModel.ShowCodex();
                viewModel.ShowGrok();
                Pump(90);
                if (animationsEnabled)
                {
                    Check(HasSlide((UIElement)window.FindName("GrokPageGrid")), "the previous transition deadline must not terminate its replacement on the same page");
                }
                Pump(250);
                viewModel.GrokSessionLogs.Toggle();
                Pump();
                window.Close();
                Pump(250);
                Check(viewport.Children.OfType<UIElement>().All(IsIdle), "closing must clear main-page animations");
                Check(new[] { "Codex", "Grok" }.SelectMany(source => new[] { "SessionLogContent", "TokenCostDashboard", "QuotaScrollViewer" }
                    .Select(suffix => (UIElement)window.FindName(source + suffix))).All(IsIdle), "closing must clear session slides and fades");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
                application?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw new InvalidOperationException($"Page transition regression failed: {failure}", failure);
        }
        return Task.CompletedTask;
    }

    private static bool HasSlide(UIElement element) => element.RenderTransform is TranslateTransform { HasAnimatedProperties: true };

    private static void CheckChartAnimations(TrayPopupWindow window, TrayPopupViewModel viewModel, bool animationsEnabled)
    {
        foreach (bool isCodex in new[] { true, false })
        {
            if (isCodex)
            {
                viewModel.ShowCodex();
            }
            else
            {
                viewModel.ShowGrok();
            }
            Pump(250);
            TrayPopupViewModel.TokenCostDashboardViewModel dashboard = isCodex ? viewModel.CodexTokenCost : viewModel.GrokTokenCost;
            ContentControl region = (ContentControl)window.FindName(isCodex ? "CodexTokenCostDashboard" : "GrokTokenCostDashboard");
            ItemsControl donut = Descendants(region).OfType<ItemsControl>().Single(item => item.Name == "TokenCostDonutItemsControl");
            ItemsControl bars = Descendants(region).OfType<ItemsControl>().Single(item => item.Name == "TokenCostChartItemsControl");
            System.Windows.Shapes.Ellipse placeholder = Descendants(region).OfType<System.Windows.Shapes.Ellipse>().Single(item => item.Name == "TokenCostDonutPlaceholder");
            Check(placeholder.IsVisible, "an empty donut must show its placeholder ring");
            dashboard.Update(ChartStatistics(100));
            Pump();
            Check(!placeholder.IsVisible, "a populated donut must hide the placeholder to avoid an inner ghost when enlarged");
            if (animationsEnabled)
            {
                Check(IsFading(donut) && IsFading(bars), "the first visible data must animate even if it arrives after Loaded");
                Check(((ScaleTransform)bars.RenderTransform).ScaleY < 1, "bars must enter upward from their baseline");
                Check(donut.Clip != null && donut.Clip.FillContains(new Point(68, 10)) && !donut.Clip.FillContains(new Point(76, 10)),
                    "the left segment must reveal counterclockwise from its own end boundary");
                Check(donut.Clip != null && donut.Clip.FillContains(new Point(76, 134)) && !donut.Clip.FillContains(new Point(68, 134)),
                    "the opposite segment must fill at the same time instead of waiting for a whole-ring sweep");
            }
            Pump(260);
            CheckChartIdle(donut);
            CheckChartIdle(bars);

            object oldSegment = donut.Items[0];
            dashboard.Update(ChartStatistics(200));
            Pump();
            Check(!ReferenceEquals(oldSegment, donut.Items[0]), "refresh must actually replace chart data");
            Check(ReferenceEquals(bars, Descendants(region).OfType<ItemsControl>().Single(item => item.Name == bars.Name)), "refresh must retain the entrance container");
            CheckChartIdle(donut);
            CheckChartIdle(bars);

            dashboard.SelectPeriodCommand.Execute(dashboard.Rows[1]);
            Pump();
            if (animationsEnabled)
            {
                Check(IsFading(donut) && donut.Clip != null, "selecting another donut period must replay the independent segment fill");
            }
            CheckChartIdle(bars);
            Pump(260);
            dashboard.SelectPeriodCommand.Execute(dashboard.Rows[1]);
            Pump();
            CheckChartIdle(donut);
            dashboard.ToggleChartPeriodCommand.Execute(null);
            Pump();
            if (animationsEnabled)
            {
                Check(IsFading(bars) && ((ScaleTransform)bars.RenderTransform).ScaleY < 1, "changing the trend period must replay the bar growth");
                Check(IsFading(donut) && donut.Clip != null, "switching the selected donut period from rolling to calendar must replay its fill too");
            }
            Pump(260);
            dashboard.Update(ChartStatistics(250));
            Pump();
            CheckChartIdle(donut);
            CheckChartIdle(bars);
            dashboard.SelectPeriodCommand.Execute(dashboard.Rows[0]);
            Pump(130);
            dashboard.SelectPeriodCommand.Execute(dashboard.Rows[1]);
            Pump(110);
            if (animationsEnabled)
            {
                Check(IsFading(donut) && donut.Clip != null, "a prior period animation deadline must not remove its replacement");
            }
            Pump(160);
            CheckChartIdle(donut);
            dashboard.ToggleChartPeriodCommand.Execute(null);
            Pump(260);

            Grid[] donutItems = Descendants(donut).OfType<Grid>().Where(grid => grid.Children.Count == 2 && grid.Children[0] is System.Windows.Shapes.Path).ToArray();
            Grid donutItem = donutItems[0];
            Grid barItem = Descendants(bars).OfType<Grid>().First(grid => grid.Children.Count == 1 && grid.Children[0] is Border { Height: > 0 });
            FrameworkElement donutVisual = (FrameworkElement)donutItem.Children[0];
            FrameworkElement barVisual = (FrameworkElement)barItem.Children[0];
            Check(!donutVisual.IsHitTestVisible && !barVisual.IsHitTestVisible, "animated visuals must not change the static hover targets");
            Check(donutItem.ToolTip != null && barItem.ToolTip != null, "tooltips must stay on the static hover targets");
            Point ringPoint = new(134, 72);
            IInputElement? hitBefore = donutItem.InputHitTest(ringPoint);
            Check(ReferenceEquals(hitBefore, donutItem.Children[1]), "the ring must hit the unscaled geometry");
            Check(ReferenceEquals(donut.InputHitTest(ringPoint), donutItem.Children[1]), "the parent chart must hit the first segment");
            Check(ReferenceEquals(donut.InputHitTest(new Point(10, 72)), donutItems[1].Children[1]), "the parent chart must hit the adjacent segment");
            ToolTip toolTip = (ToolTip)window.Resources["ChartToolTip"];
            SetHover(donutItem, true);
            Check(toolTip.IsOpen && Equals(toolTip.Content, donutItem.ToolTip), "tooltip content must open with the highlight without a separate delay");
            Pump(160);
            Check(((ScaleTransform)donutVisual.RenderTransform).ScaleX == 1.04, "hover must enlarge the ring segment slightly");
            Check(ReferenceEquals(hitBefore, donutItem.InputHitTest(ringPoint)), "enlarging a segment must not move its hit region");
            Check(((ScaleTransform)donutVisual.RenderTransform).CenterX == 72 && ((ScaleTransform)donutVisual.RenderTransform).CenterY == 72, "segments must scale around the common ring center");
            foreach (string theme in new[] { AppSettings.ThemeModeLight, AppSettings.ThemeModeDark })
            {
                viewModel.ThemeMode = theme;
                Pump();
                Check(ReferenceEquals(toolTip.Background, window.FindResource("ToolTipBackground")), "the shared tooltip must follow the active theme");
            }
            Panel.SetZIndex((UIElement)donut.ItemContainerGenerator.ContainerFromIndex(0), 1);
            Check(ReferenceEquals(donut.InputHitTest(new Point(10, 72)), donutItems[1].Children[1]), "the highlighted container must not hide the adjacent segment's target");
            SetHover(donutItems[1], true);
            SetHover(donutItem, false);
            Check(toolTip.IsOpen && ReferenceEquals(toolTip.PlacementTarget, donutItems[1]) && Equals(toolTip.Content, donutItems[1].ToolTip),
                "crossing segments must replace the tip immediately, even if the old leave event arrives later");
            Pump(160);
            CheckChartIdle(donutVisual);
            SetHover(donutItems[1], false);
            Check(!toolTip.IsOpen, "leaving the segment must close its tip immediately");
            SetHover(barItem, true);
            Check(toolTip.IsOpen && Equals(toolTip.Content, barItem.ToolTip), "bar hover must immediately replace the shared tooltip content");
            Pump(160);
            Check(((ScaleTransform)barVisual.RenderTransform).ScaleX == 1.12 && ((ScaleTransform)barVisual.RenderTransform).ScaleY == 1.04, "hover must enlarge the bar around its bottom edge");
            foreach (Grid item in new[] { donutItem, barItem })
            {
                SetHover(item, false);
                SetHover(item, true);
                SetHover(item, false);
            }
            Pump(160);
            CheckChartIdle(donutVisual);
            CheckChartIdle(barVisual);
            SetHover(donutItem, true);
            window.Hide();
            dashboard.SelectPeriodCommand.Execute(dashboard.Rows[0]);
            Pump();
            Check(!toolTip.IsOpen && toolTip.PlacementTarget == null, "hiding the chart must close and release its tooltip");
            CheckChartIdle(donutVisual);
            CheckChartIdle(donut);
            CheckChartIdle(bars);
            window.ShowNearTray(null);
            Pump();
            if (animationsEnabled)
            {
                Check(IsFading(donut) && IsFading(bars), "showing the chart region again must replay its entrance");
            }
            window.Hide();
            Pump();
            CheckChartIdle(donut);
            CheckChartIdle(bars);
            window.ShowNearTray(null);
            Pump();
            donutItem = Descendants(donut).OfType<Grid>().First(grid => grid.Children.Count == 2 && grid.Children[0] is System.Windows.Shapes.Path);
            SetHover(donutItem, true);
            dashboard.Update(ChartStatistics(300));
            Pump(260);
            Check(!toolTip.IsOpen && toolTip.PlacementTarget == null, "replacing a hovered item must release the old tooltip owner");
            CheckChartIdle(donut);
            CheckChartIdle(bars);
            dashboard.Update(null);
            Pump();
            Check(placeholder.IsVisible, "unavailable data must restore the placeholder ring");
        }
    }

    private static TokenCostStatistics ChartStatistics(long tokens)
    {
        TokenCostSummary summary = new() { TotalTokens = tokens, CostUsd = tokens / 100m };
        return new TokenCostStatistics
        {
            LastTwentyFourHours = summary,
            LastSevenDays = summary,
            Today = summary,
            CurrentWeek = summary,
            LastThirtyDaysDaily = [new TokenCostDailySummary { Date = DateTime.Today, Summary = summary }],
            CurrentMonthDaily = [new TokenCostDailySummary { Date = DateTime.Today, Summary = summary }],
            Models =
            [
                new TokenCostModelStatistics { Model = "fixture-a", LastTwentyFourHours = summary, LastSevenDays = summary, Today = summary, CurrentWeek = summary },
                new TokenCostModelStatistics { Model = "fixture-b", LastTwentyFourHours = summary, LastSevenDays = summary, Today = summary, CurrentWeek = summary },
            ],
        };
    }

    private static void SetHover(Grid item, bool hovered)
    {
        // Exercise the handlers without claiming to simulate the system pointer or IsMouseOver.
        item.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = hovered ? UIElement.MouseEnterEvent : UIElement.MouseLeaveEvent });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsFading(UIElement element) => DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated;

    private static void CheckChartIdle(FrameworkElement element)
    {
        Check(!IsFading(element) && element.Opacity == 1 && element.Clip == null && element.RenderTransform is ScaleTransform { HasAnimatedProperties: false, ScaleX: 1, ScaleY: 1 },
            "idle and hidden chart visuals must release their clocks and restore normal size");
    }

    private static bool IsIdle(UIElement element) => !HasSlide(element)
        && !DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated
        && element.Opacity == 1 && (element.RenderTransform is not TranslateTransform offset || offset.X == 0 && offset.Y == 0);

    private static void Pump(int milliseconds = 20)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
