using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cxt1024Bridge
{
    internal static class NativeMethods
    {
        internal const uint DigcfPresent = 0x00000002;
        internal const uint DigcfDeviceInterface = 0x00000010;
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const int ErrorNoMoreItems = 259;
        internal const int WhKeyboardLl = 13;
        internal const int WmKeyDown = 0x0100;
        internal const int WmKeyUp = 0x0101;
        internal const int WmSysKeyDown = 0x0104;
        internal const int WmSysKeyUp = 0x0105;
        internal const uint WmMouseWheel = 0x020A;
        internal const uint LlkhfInjected = 0x00000010;
        internal const uint InputMouse = 0;
        internal const uint InputKeyboard = 1;
        internal const uint KeyeventfExtendedKey = 0x0001;
        internal const uint KeyeventfKeyup = 0x0002;
        internal const uint KeyeventfUnicode = 0x0004;
        internal const uint MouseeventfWheel = 0x0800;
        internal const uint MouseeventfHwheel = 0x01000;
        internal const int SwRestore = 9;
        internal const int SwHide = 0;
        internal const uint EventSystemForeground = 0x0003;
        internal const uint WineventOutofcontext = 0x0000;
        internal const int WmInput = 0x00FF;
        internal const int WmInputDeviceChange = 0x00FE;
        internal const int WmAppStopRawInput = 0x8001;
        internal const uint GidcArrival = 1;
        internal const uint GidcRemoval = 2;
        internal const uint RimTypeKeyboard = 1;
        internal const uint RidInput = 0x10000003;
        internal const uint RidiDeviceName = 0x20000007;
        internal const uint RidiDeviceInfo = 0x2000000B;
        internal const uint RidevRemove = 0x00000001;
        internal const uint RidevInputSink = 0x00000100;
        internal const uint RidevDevNotify = 0x00002000;
        internal const ushort RiKeyBreak = 0x0001;
        internal const ushort RiKeyE0 = 0x0002;
        internal const ushort RiKeyE1 = 0x0004;
        internal const ushort KeyboardOverrunMakeCode = 0x00FF;
        internal const uint CrSuccess = 0x00000000;
        internal const uint DevPropTypeGuid = 0x0000000D;

        internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        internal delegate void WinEventProc(
            IntPtr hook,
            uint eventType,
            IntPtr window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SpDeviceInterfaceData
        {
            internal int Size;
            internal Guid InterfaceClassGuid;
            internal int Flags;
            internal IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HiddAttributes
        {
            internal int Size;
            internal ushort VendorId;
            internal ushort ProductId;
            internal ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidpCaps
        {
            internal ushort Usage;
            internal ushort UsagePage;
            internal ushort InputReportByteLength;
            internal ushort OutputReportByteLength;
            internal ushort FeatureReportByteLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            internal ushort[] Reserved;

            internal ushort NumberLinkCollectionNodes;
            internal ushort NumberInputButtonCaps;
            internal ushort NumberInputValueCaps;
            internal ushort NumberInputDataIndices;
            internal ushort NumberOutputButtonCaps;
            internal ushort NumberOutputValueCaps;
            internal ushort NumberOutputDataIndices;
            internal ushort NumberFeatureButtonCaps;
            internal ushort NumberFeatureValueCaps;
            internal ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Kbdllhookstruct
        {
            internal uint VirtualKeyCode;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawInputDevice
        {
            internal ushort UsagePage;
            internal ushort Usage;
            internal uint Flags;
            internal IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawInputDeviceList
        {
            internal IntPtr Device;
            internal uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawInputHeader
        {
            internal uint Type;
            internal uint Size;
            internal IntPtr Device;
            internal IntPtr WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawKeyboard
        {
            internal ushort MakeCode;
            internal ushort Flags;
            internal ushort Reserved;
            internal ushort VirtualKey;
            internal uint Message;
            internal uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DevPropKey
        {
            internal Guid FormatId;
            internal uint PropertyId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            internal MouseInput Mouse;

            [FieldOffset(0)]
            internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort VirtualKeyCode;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LastInputInfo
        {
            internal uint Size;
            internal uint Time;
        }

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            string enumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelKeyboardProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterRawInputDevices(
            [MarshalAs(UnmanagedType.LPArray), In] RawInputDevice[] devices,
            uint deviceCount,
            uint size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputData(
            IntPtr rawInput,
            uint command,
            IntPtr data,
            ref uint size,
            uint headerSize);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputDeviceList(
            IntPtr list,
            ref uint count,
            uint size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetRawInputDeviceInfo(
            IntPtr device,
            uint command,
            System.Text.StringBuilder data,
            ref uint size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputDeviceInfo(
            IntPtr device,
            uint command,
            IntPtr data,
            ref uint size);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_PropertyW")]
        internal static extern uint CmGetDeviceInterfaceProperty(
            string deviceInterface,
            ref DevPropKey propertyKey,
            out uint propertyType,
            [Out] byte[] propertyBuffer,
            ref uint propertyBufferSize,
            uint flags);

        [DllImport("user32.dll")]
        internal static extern int GetMessageTime();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKeyCode);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint numberOfInputs,
            [MarshalAs(UnmanagedType.LPArray), In] Input[] inputs,
            int sizeOfInput);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr eventHookModule,
            WinEventProc callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

        internal static void ThrowLastWin32(string operation)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }
    }
}
