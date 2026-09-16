using System.Runtime.InteropServices;

namespace SupportCaseManager.App.Outlook;

public enum OutlookSearchStatus
{
    Succeeded,
    Skipped,
    Busy,
    Failed,
}

public sealed record OutlookSearchResult(OutlookSearchStatus Status, bool OutlookStarted, string Message)
{
    public static OutlookSearchResult Succeeded(bool outlookStarted) =>
        new(OutlookSearchStatus.Succeeded, outlookStarted, string.Empty);

    public static OutlookSearchResult Skipped() =>
        new(OutlookSearchStatus.Skipped, false, string.Empty);

    public static OutlookSearchResult Busy() =>
        new(OutlookSearchStatus.Busy, false, string.Empty);

    public static OutlookSearchResult Failed() =>
        new(OutlookSearchStatus.Failed, false, "Classic Outlookで検索を開始できませんでした。");
}

public interface IOutlookSearchService
{
    Task<OutlookSearchResult> SearchSupportIdAsync(
        string supportId,
        CancellationToken cancellationToken = default);
}

public interface IOutlookComGateway
{
    object? TryGetRunningApplication();
    object StartApplication();
    void ShowCurrentStoreSearch(object application, string query);
}

public sealed class OutlookSearchService : IOutlookSearchService
{
    private readonly IOutlookComGateway gateway;
    private int searchInProgress;

    public OutlookSearchService(IOutlookComGateway? gateway = null)
    {
        this.gateway = gateway ?? new OutlookComGateway();
    }

    public Task<OutlookSearchResult> SearchSupportIdAsync(
        string supportId,
        CancellationToken cancellationToken = default)
    {
        var query = supportId?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return Task.FromResult(OutlookSearchResult.Skipped());
        }

        if (Interlocked.CompareExchange(ref searchInProgress, 1, 0) != 0)
        {
            return Task.FromResult(OutlookSearchResult.Busy());
        }

        var completion = new TaskCompletionSource<OutlookSearchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => SearchOnStaThread(query, cancellationToken, completion))
        {
            IsBackground = true,
            Name = "Classic Outlook support search",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private void SearchOnStaThread(
        string query,
        CancellationToken cancellationToken,
        TaskCompletionSource<OutlookSearchResult> completion)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var application = gateway.TryGetRunningApplication();
            var started = application is null;
            application ??= gateway.StartApplication();
            gateway.ShowCurrentStoreSearch(application, query);
            completion.TrySetResult(OutlookSearchResult.Succeeded(started));
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception)
        {
            completion.TrySetResult(OutlookSearchResult.Failed());
        }
        finally
        {
            Interlocked.Exchange(ref searchInProgress, 0);
        }
    }
}

public sealed class OutlookComGateway : IOutlookComGateway
{
    private const string OutlookApplicationProgId = "Outlook.Application";
    private const int OutlookFolderInbox = 6;
    private const int OutlookFolderDisplayNormal = 0;
    private const int OutlookSearchScopeCurrentStore = 4;

    public object? TryGetRunningApplication()
    {
        var applicationType = GetOutlookApplicationType();
        var classId = applicationType.GUID;
        return GetActiveObject(ref classId, IntPtr.Zero, out var application) >= 0
            ? application
            : null;
    }

    public object StartApplication()
    {
        return Activator.CreateInstance(GetOutlookApplicationType())
            ?? throw new InvalidOperationException("Classic Outlookを起動できませんでした。");
    }

    public void ShowCurrentStoreSearch(object application, string query)
    {
        ArgumentNullException.ThrowIfNull(application);
        dynamic outlook = application;
        dynamic? explorer = null;
        dynamic? outlookNamespace = null;
        dynamic? inbox = null;
        try
        {
            explorer = outlook.ActiveExplorer();
            if (explorer is null)
            {
                outlookNamespace = outlook.GetNamespace("MAPI");
                inbox = outlookNamespace.GetDefaultFolder(OutlookFolderInbox);
                explorer = inbox.GetExplorer(OutlookFolderDisplayNormal);
                explorer.Display();
            }

            explorer.Activate();
            explorer.Search(query, OutlookSearchScopeCurrentStore);
            explorer.Activate();
            TryBringToForeground(explorer);
        }
        finally
        {
            ReleaseComObject(inbox);
            ReleaseComObject(outlookNamespace);
            ReleaseComObject(explorer);
            ReleaseComObject(application);
        }
    }

    private static Type GetOutlookApplicationType() =>
        Type.GetTypeFromProgID(OutlookApplicationProgId, throwOnError: false)
        ?? throw new InvalidOperationException("Classic Outlookがインストールされていません。");

    private static void TryBringToForeground(dynamic explorer)
    {
        try
        {
            var windowHandle = new IntPtr((long)explorer.HWND);
            if (windowHandle != IntPtr.Zero)
            {
                _ = SetForegroundWindow(windowHandle);
            }
        }
        catch (Exception)
        {
            // Explorer.Activate already provides the supported Outlook fallback.
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? application);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
