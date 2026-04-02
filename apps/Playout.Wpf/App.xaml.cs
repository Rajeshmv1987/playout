using System.Windows;
using System;
using System.IO;

namespace Playout.Wpf;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                WriteCrashLog(args.ExceptionObject as Exception ?? new Exception("Unknown fatal error"));
            }
            catch { }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                WriteCrashLog(args.Exception);
            }
            catch { }
            try
            {
                System.Windows.MessageBox.Show($"Crash log saved.\n\n{GetCrashLogPath()}", "Playout.Wpf Error");
            }
            catch { }
            args.Handled = true;
            Shutdown(-1);
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var path = GetCrashLogPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, $"ExitCode: {e.ApplicationExitCode}{Environment.NewLine}Time: {DateTimeOffset.Now:O}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
        base.OnExit(e);
    }

    static string GetCrashLogPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playout.Wpf");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "crash.log");
    }

    static void WriteCrashLog(Exception ex)
    {
        var path = GetCrashLogPath();
        var text =
            $"Time: {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"Version: {typeof(App).Assembly.FullName}{Environment.NewLine}" +
            $"{ex}{Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(path, text);
    }
}
