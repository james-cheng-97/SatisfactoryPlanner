using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SatisfactoryPlanner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // WPF changes a closed ComboBox's selection on mouse wheel when it has focus, so scrolling the panel after
        // picking a product silently swapped it (e.g. Heavy Modular Frame → Alien Protein). Closed combos now pass the
        // wheel to their parent instead; an open drop-down still scrolls its list.
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(ComboBoxWheel));

        // last line of defence: report unexpected errors instead of closing the app (and losing unsaved plan tabs)
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var log = System.IO.Path.Combine(GameData.AppDir, "error.log");
                System.IO.Directory.CreateDirectory(GameData.AppDir);
                System.IO.File.AppendAllText(log, $"[{DateTime.Now:u}] {args.Exception}{Environment.NewLine}{Environment.NewLine}");
                MessageBox.Show(Loc.T("crash", args.Exception.Message, log), "Satisfactory Planner", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception) { }
            args.Handled = true;
        };
    }

    static void ComboBoxWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen || e.Handled) return;
        e.Handled = true;
        if (VisualTreeHelper.GetParent(combo) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = combo });
    }
}
