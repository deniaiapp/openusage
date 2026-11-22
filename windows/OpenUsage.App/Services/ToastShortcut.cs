using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OpenUsage.App.Services;

internal static class ToastShortcut
{
    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid ProtocolOnly = new("0E444F42-49D8-411F-B07B-0548B3B80694");
    private static readonly Guid ShellLink = new("00021401-0000-0000-C000-000000000046");

    public static bool IsOwned(string shortcutPath, string appId)
    {
        var link = NewLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);
            var key = new PropertyKey(AppUserModel, 5);
            ((IPropertyStore)link).GetValue(ref key, out var value);
            try
            {
                return value.Type == 31 && Marshal.PtrToStringUni(value.Pointer) == appId;
            }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    public static void Write(string shortcutPath, string executable, string appId)
    {
        var link = NewLink();
        try
        {
            ((IShellLinkW)link).SetPath(executable);
            var store = (IPropertyStore)link;
            Set(store, 5, 31, Marshal.StringToCoTaskMemUni(appId));
            // No COM server: Windows routes toast clicks through the associated URI.
            var clsid = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(ProtocolOnly, clsid, false);
            Set(store, 26, 72, clsid);
            store.Commit();
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static object NewLink() => Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLink)!)!;

    private static void Set(IPropertyStore store, uint property, ushort type, nint pointer)
    {
        var key = new PropertyKey(AppUserModel, property);
        var value = new PropertyValue { Type = type, Pointer = pointer };
        try { store.SetValue(ref key, ref value); }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropertyValue value);
        void SetValue(ref PropertyKey key, ref PropertyValue value);
        void Commit();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(nint path, int size, nint findData, uint flags);
        void GetIdList(out nint itemIdList);
        void SetIdList(nint itemIdList);
        void GetDescription(nint description, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory(nint directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(nint arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCommand(out int showCommand);
        void SetShowCommand(int showCommand);
        void GetIconLocation(nint path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
