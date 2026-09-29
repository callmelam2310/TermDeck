using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TermDeck.Core;
using TermDeck.Views;

namespace TermDeck;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // English UI regardless of the Windows display language (dates, number formats, WPF bindings).
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // Every window (main window and dialogs) shows the TermDeck logo in its title bar / taskbar button.
        var icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/TermDeck.ico"));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w && w.Icon == null) w.Icon = icon; }));

        var config = ConfigStore.Load();
        ThemeManager.Apply(config.Theme);
        var window = new MainWindow(config);
        MainWindow = window;
        window.Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "error.log"), $"[{DateTime.Now:o}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "TermDeck — error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
