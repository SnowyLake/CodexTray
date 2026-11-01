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
