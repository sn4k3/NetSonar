using Avalonia.Controls.Notifications;
using Cysharp.Diagnostics;
using NetSonar.Avalonia.SystemOS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ZLinq;
using ZLogger;

namespace NetSonar.Avalonia.Extensions;

public record ProcessXToast
{
    public string? Title { get; init; }
    public bool ShowOnlySuccessGenericMessage { get; init; }
    public string SuccessGenericMessage { get; init; } = "Operation completed successfully.";
    public string? ErrorGenericMessage { get; init; }

    public ProcessXToast()
    {
    }

    public ProcessXToast(string? title, string successGenericMessage = "Operation completed successfully.", string? errorGenericMessage = null)
    {
        Title = title;
        SuccessGenericMessage = successGenericMessage;
        ErrorGenericMessage = errorGenericMessage;
    }
}

public static class ProcessXExtensions
{
    private const string GsudoPath = ".\\binaries\\gsudo\\gsudo.exe";

    public static Task<bool> ExecuteHandled(string command, ProcessXToast toast, bool requireAdminRights = false)
    {
        return ExecuteHandled([command], toast, requireAdminRights);
    }

    public static async Task<bool> ExecuteHandled(IEnumerable<string> commands, ProcessXToast toast, bool requireAdminRights = false)
    {
        var commandList = commands as string[] ?? commands.AsValueEnumerable().ToArray();
        if (commandList.Length == 0) return false;

        var stdBuffered = new List<string>();
        var errorBuffered = new List<string>();

        var currentStdBuffered = new List<string>();

        if (requireAdminRights && Environment.IsPrivilegedProcess) requireAdminRights = false;

        bool success = true;
        bool gsudoCache = false;

        if (requireAdminRights)
        {
            if (OperatingSystem.IsWindows())
            {
                if (commandList.Length > 1)
                {
                    try
                    {
                        App.Logger.ZLogInformation($"Run command: gsudo cache on");
                        await ProcessX.StartAsync($"{GsudoPath} cache on").ToTask();
                        gsudoCache = true;
                    }
                    catch (ProcessErrorException ex)
                    {
                        if (ex.ExitCode != 0)
                        {
                            success = false;
                            if (ex.ExitCode == 999) return false; // Cancelled by user
                            App.ShowExceptionToast(ex, toast.Title, toast.ErrorGenericMessage);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        App.ShowExceptionToast(ex, toast.Title, toast.ErrorGenericMessage);
                        return false;
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                commandList = [
                    $"osascript -e \"do shell script \\\"{string.Join(" && ", commandList).Replace('"', '\'')}\\\" with prompt \\\"{App.Software} - {toast.Title ?? "Elevated action"}\\\" with administrator privileges\"",
                ];
            }
            else if (OperatingSystem.IsLinux())
            {
                if (!SystemAware.TryFindEnvFile("pkexec", out var path))
                {
                    App.ShowToast(NotificationType.Error, toast.Title, """
                                                                       Unable to run the operation due sudo requirement.
                                                                       Tried to run pkexec but it was not found.
                                                                       Please install pkexec or run the application with sudo.
                                                                       """);
                    return false;
                }

                commandList = [$"pkexec bash -c \"{string.Join(" && ", commandList).Replace('"', '\'')}\""];
                Console.WriteLine(commandList[0]);
            }
        }

        foreach (var command in commandList)
        {
            // first argument is Process, if you want to know ProcessID, use StandardInput, use it.

            var processCommand = command.Trim();
            if (requireAdminRights)
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!command.StartsWith("gsudo")) processCommand = $"{GsudoPath} {command}";
                }
                /*else if (OperatingSystem.IsLinux())
                {
                    if (!command.StartsWith("pkexec")) processCommand = $"pkexec {command}";
                }*/
            }

            try
            {
                App.Logger.ZLogInformation($"Run command: {processCommand}");
                var (process, stdOut, stdError) = ProcessX.GetDualAsyncEnumerable(processCommand);

                var consumeStdOut = Task.Run(async () =>
                {
                    currentStdBuffered.Clear();
                    await foreach (var item in stdOut)
                    {
                        if(string.IsNullOrWhiteSpace(item)) continue;
                        currentStdBuffered.Add(item);
                        if (stdBuffered.Count > 0 && stdBuffered[^1] == item) continue;
                        stdBuffered.Add(item);
                    }
                });

                var consumeStdError = Task.Run(async () =>
                {
                    await foreach (var item in stdError)
                    {
                        if (string.IsNullOrWhiteSpace(item)) continue;
                        errorBuffered.Add(item);
                    }
                });


                await Task.WhenAll(consumeStdOut, consumeStdError);
            }
            catch (ProcessErrorException ex)
            {
                success = false;
                if (OperatingSystem.IsWindows())
                {
                    if (ex.ExitCode == 999) break; // Cancelled by user
                }
                else if (OperatingSystem.IsMacOS())
                {
                    if (requireAdminRights && errorBuffered.Count > 0)
                    {
                        if (ex.ExitCode == 1 && errorBuffered[^1].EndsWith("(-128)")) break; // Cancelled by user
                    }
                }
                else if (OperatingSystem.IsLinux())
                {
                    if (requireAdminRights && errorBuffered.Count > 0)
                    {
                        if (ex.ExitCode == 126) break; // Cancelled by user
                    }
                }
                var msg = string.Empty;
                if (errorBuffered.Count == 0)
                {
                    if (currentStdBuffered.Count > 0)
                    {
                        msg = string.Join(Environment.NewLine, currentStdBuffered);
                    }
                }
                else
                {
                    msg = string.Join(Environment.NewLine, errorBuffered);
                }

                if (!string.IsNullOrWhiteSpace(toast.ErrorGenericMessage)) msg = $"{toast.ErrorGenericMessage}{Environment.NewLine}{msg}";
                msg += $" ({ex.ExitCode})";
                App.ShowExceptionToast(toast.Title, msg);
                break;
            }
            catch (Exception ex)
            {
                success = false;
                App.ShowExceptionToast(ex, toast.Title, toast.ErrorGenericMessage);
                break;
            }
        }

        if (gsudoCache)
        {
            try
            {
                App.Logger.ZLogInformation($"Run command: gsudo cache off");
                await ProcessX.StartAsync($"{GsudoPath} cache off").ToTask();
            }
            catch
            {
                // ignored
            }
        }

        if (success)
        {
            var msg = stdBuffered.Count > 0
                      && !toast.ShowOnlySuccessGenericMessage
                      && !stdBuffered[0].Equals("Ok", StringComparison.OrdinalIgnoreCase)
                      && !stdBuffered[0].Equals("Ok.", StringComparison.OrdinalIgnoreCase)
                ? string.Join(Environment.NewLine, stdBuffered)
                : toast.SuccessGenericMessage;
            App.ShowToast(NotificationType.Success, toast.Title, msg);
        }

        return success;
    }
}