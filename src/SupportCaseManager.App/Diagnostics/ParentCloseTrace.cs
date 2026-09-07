using System;
using System.IO;
using System.Text;

namespace SupportCaseManager.App.Diagnostics;

internal static class ParentCloseTrace
{
    private static readonly object Gate = new();
    private static readonly string TracePath = ResolveTracePath();

    public static void Write(string eventName, IntPtr windowHandle = default, bool? cancel = null)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" PID=").Append(Environment.ProcessId)
                .Append(" TID=").Append(Environment.CurrentManagedThreadId)
                .Append(" HWND=0x").Append(windowHandle.ToInt64().ToString("X"))
                .Append(" EVENT=").Append(eventName);

            if (cancel.HasValue)
            {
                builder.Append(" Cancel=").Append(cancel.Value);
            }

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TracePath)!);
                File.AppendAllText(TracePath, builder.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never affect application shutdown.
        }
    }

    public static void WriteException(string stage, Exception exception, IntPtr windowHandle = default)
    {
        Write($"EXCEPTION_{stage}_TYPE={exception.GetType().FullName}", windowHandle);
    }

    private static string ResolveTracePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("SUPPORTCASEMANAGER_PARENT_CLOSE_TRACE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Temp", "SupportCaseManager", "parent-close-trace.log");
    }
}
