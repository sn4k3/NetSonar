using Microsoft.Extensions.Logging;
using NetSonar.Avalonia.Models;
using System.Diagnostics;
using System;
using System.IO;
using Updatum;
using ZLogger;
using ZLogger.Providers;

namespace NetSonar.Avalonia;

public partial class App
{
    public static ILogger Logger { get; private set; } = null!;

    private static void SetupLogger()
    {
        var factory = LoggerFactory.Create(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddZLoggerRollingFile(options =>
            {
                // File name determined by parameters to be rotated
                options.FilePathSelector = (timestamp, sequenceNumber) =>
                    Path.Combine(LogsPath, $"{timestamp.ToLocalTime():yyyy-MM}_{sequenceNumber:000}.log");

                // The period of time for which you want to rotate files at time intervals.
                options.RollingInterval = RollingInterval.Month;

                // Limit of size if you want to rotate by file size. (KB)
                options.RollingSizeKB = 1024 * 2;

                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter($"(*){0}|{1}|", (in template, in info) => template.Format(info.Timestamp, info.LogLevel));
                    //formatter.SetSuffixFormatter($" ({0})", (in MessageTemplate template, in LogInfo info) => template.Format(info.Category));
                    //formatter.SetExceptionFormatter((writer, ex) => Utf8String.Format(writer, $"{ex.Message}"));
                });
            });

#if DEBUG
            // Add to output of simple rendered strings into memory. You can subscribe to this and use it.
            logging.AddZLoggerInMemory(processor =>
            {
                processor.MessageReceived += WriteLine;
            });
#endif
            // Output Structured Logging, setup options
            // logging.AddZLoggerConsole(options => options.UseJsonFormatter());
        });

        Logger = factory.CreateLogger(Software);
    }

    /// <summary>
    /// Handles and log the unhandled exception.
    /// </summary>
    /// <param name="category"></param>
    /// <param name="ex"></param>
    public static void HandleUnhandledException(Exception ex, string category)
    {
        try
        {
            WriteLine(ex);
            Logger.ZLogCritical(ex, $"{category}");

            if (category == "Task")
            {
                if (ex.Message.Contains("org.freedesktop.DBus.Error.ServiceUnknown")
                    || ex.Message.Contains("org.freedesktop.DBus.Error.UnknownMethod")) return;
            }

            if (!IsCrashReport)
            {
                var report = new CrashReport(ex, category);
                CrashReports.Add(report);
                EntryApplication.LaunchNewInstance($"--crash-report \"{report.Id}\"");
            }
        }
        catch (Exception e)
        {
            WriteLine(e);
        }

        PanicSaveSettings();
        Environment.Exit(-1);
    }

    /// <summary>
    /// Handles exceptions that are safe to continue.
    /// </summary>
    /// <param name="ex"></param>
    /// <param name="category"></param>
    /// <param name="logLevel"></param>
    public static void HandleSafeException(Exception ex, string category, LogLevel logLevel = LogLevel.Error)
    {
        try
        {
            WriteLine(ex);
            Logger.ZLog(logLevel, ex, $"{category}");
        }
        catch (Exception e)
        {
            WriteLine(e);
        }
    }


    /// <summary>
    /// Writes the specified string to the console and debug output.
    /// </summary>
    /// <param name="str"></param>
    public static void WriteLine(object? str)
    {
#if DEBUG
        Console.WriteLine(str);
        Debug.WriteLine(str);
#endif
    }

    public static void WriteLine()
    {
#if DEBUG
        Console.WriteLine();
#endif
    }
}