using System.IO;
using System.Runtime.InteropServices;

namespace SupportCaseManager.App;

internal sealed class WindowsCaseFolderRecycler : ICaseFolderRecycler
{
    public void Recycle(string absoluteFolderPath)
    {
        // Windows 8+ recycle-only operation. Never fall back to Directory.Delete.
        // https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags
        if (!Path.IsPathFullyQualified(absoluteFolderPath) || !Directory.Exists(absoluteFolderPath))
            throw new IOException("ごみ箱へ移動するフォルダーを確認できません。");
        object? operation = null;
        IShellItem? item = null;
        try
        {
            operation = Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!);
            var fileOperation = (IFileOperation)operation!;
            const uint recycleOnDelete = 0x00080000, addUndoRecord = 0x20000000, earlyFailure = 0x00100000,
                noErrorUi = 0x0400, noConfirmation = 0x0010, wantNukeWarning = 0x4000;
            fileOperation.SetOperationFlags(recycleOnDelete | addUndoRecord | earlyFailure | noErrorUi | noConfirmation | wantNukeWarning);
            var shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(absoluteFolderPath, IntPtr.Zero, ref shellItemId, out item));
            fileOperation.DeleteItem(item!, IntPtr.Zero);
            fileOperation.PerformOperations();
            fileOperation.GetAnyOperationsAborted(out var aborted);
            if (aborted) throw new OperationCanceledException("ごみ箱への移動は中止されました。除外設定は保持します。");
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.ReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        out IShellItem? item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint nameType, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr changes);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
        void CopyItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem([MarshalAs(UnmanagedType.Interface)] object destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
