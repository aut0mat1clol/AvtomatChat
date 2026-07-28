using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AvtomatChat;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Глобальный перехват ошибок: вместо тихого падения показываем
        // текст ошибки и пишем его в crash.log рядом с exe.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash(args.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportCrash(args.Exception, "Task");
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Известный внутренний баг WPF: гонка в загрузчике картинок
        // (LateBoundBitmapDecoder / Freezable.FireChanged). На работу не влияет —
        // пишем в лог, но не беспокоим пользователя окном.
        if (e.Exception is ArgumentOutOfRangeException &&
            e.Exception.StackTrace?.Contains("LateBoundBitmapDecoder") == true)
        {
            LogCrash(e.Exception, "WPF-imaging (подавлено)");
            e.Handled = true;
            return;
        }

        ReportCrash(e.Exception, "UI");
        e.Handled = true; // не даём приложению закрыться молча

        // Если главное окно так и не открылось (краш при запуске) —
        // завершаем процесс, иначе он останется висеть в памяти без окна.
        if (MainWindow == null || !MainWindow.IsLoaded)
            Shutdown(1);
    }

    private static void LogCrash(Exception? ex, string source)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source})\r\n{ex}\r\n\r\n";
        try
        {
            File.AppendAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                text);
        }
        catch { }
    }

    private static void ReportCrash(Exception? ex, string source)
    {
        LogCrash(ex, source);

        System.Windows.MessageBox.Show(
            "Произошла ошибка:\r\n\r\n" + ex?.Message +
            "\r\n\r\nПодробности в crash.log рядом с программой.",
            "AvtomatChat — ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
