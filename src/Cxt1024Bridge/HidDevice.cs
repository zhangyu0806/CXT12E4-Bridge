using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cxt1024Bridge
{
    internal sealed class HidDeviceInfo
    {
        internal string Path;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort VersionNumber;
        internal ushort UsagePage;
        internal ushort Usage;
        internal ushort InputReportLength;
        internal ushort OutputReportLength;
        internal ushort FeatureReportLength;

        internal bool IsCxt1024Raw
        {
            get
            {
                return VendorId == 0x5754 &&
                       ProductId == 0xC401 &&
                       UsagePage == 0xFF60 &&
                       Usage == 0x0061;
            }
        }

        public override string ToString()
        {
            return string.Format(
                "VID_{0:X4} PID_{1:X4} REV_{2:X4} UP_{3:X4} U_{4:X4} in={5} out={6} feature={7}",
                VendorId,
                ProductId,
                VersionNumber,
                UsagePage,
                Usage,
                InputReportLength,
                OutputReportLength,
                FeatureReportLength);
        }
    }

    internal static class HidEnumerator
    {
        internal static IList<HidDeviceInfo> Enumerate()
        {
            var devices = new List<HidDeviceInfo>();
            Guid hidGuid;
            NativeMethods.HidD_GetHidGuid(out hidGuid);
            IntPtr infoSet = NativeMethods.SetupDiGetClassDevs(
                ref hidGuid,
                null,
                IntPtr.Zero,
                NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

            if (infoSet == new IntPtr(-1))
                NativeMethods.ThrowLastWin32("SetupDiGetClassDevs failed");

            try
            {
                uint index = 0;
                while (true)
                {
                    var interfaceData = new NativeMethods.SpDeviceInterfaceData();
                    interfaceData.Size = Marshal.SizeOf(typeof(NativeMethods.SpDeviceInterfaceData));
                    if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                        infoSet,
                        IntPtr.Zero,
                        ref hidGuid,
                        index,
                        ref interfaceData))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == NativeMethods.ErrorNoMoreItems)
                            break;
                        throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces failed");
                    }

                    index++;
                    uint requiredSize;
                    NativeMethods.SetupDiGetDeviceInterfaceDetail(
                        infoSet,
                        ref interfaceData,
                        IntPtr.Zero,
                        0,
                        out requiredSize,
                        IntPtr.Zero);

                    IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            infoSet,
                            ref interfaceData,
                            detail,
                            requiredSize,
                            out requiredSize,
                            IntPtr.Zero))
                        {
                            continue;
                        }

                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        // Inspect only this keyboard. Opening every HID interface on the
                        // machine can block on unrelated Bluetooth/vendor devices.
                        if (string.IsNullOrEmpty(path) ||
                            path.IndexOf("vid_5754&pid_c401", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        HidDeviceInfo device = ReadDeviceInfo(path);
                        if (device != null)
                            devices.Add(device);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(infoSet);
            }

            return devices;
        }

        private static HidDeviceInfo ReadDeviceInfo(string path)
        {
            using (SafeFileHandle handle = NativeMethods.CreateFile(
                path,
                0,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return null;

                var attributes = new NativeMethods.HiddAttributes();
                attributes.Size = Marshal.SizeOf(typeof(NativeMethods.HiddAttributes));
                if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
                    return null;

                IntPtr preparsed;
                if (!NativeMethods.HidD_GetPreparsedData(handle, out preparsed))
                    return null;

                try
                {
                    NativeMethods.HidpCaps caps;
                    if (NativeMethods.HidP_GetCaps(preparsed, out caps) < 0)
                        return null;

                    return new HidDeviceInfo
                    {
                        Path = path,
                        VendorId = attributes.VendorId,
                        ProductId = attributes.ProductId,
                        VersionNumber = attributes.VersionNumber,
                        UsagePage = caps.UsagePage,
                        Usage = caps.Usage,
                        InputReportLength = caps.InputReportByteLength,
                        OutputReportLength = caps.OutputReportByteLength,
                        FeatureReportLength = caps.FeatureReportByteLength
                    };
                }
                finally
                {
                    NativeMethods.HidD_FreePreparsedData(preparsed);
                }
            }
        }

        internal static HidDeviceInfo FindCxt1024Raw()
        {
            foreach (HidDeviceInfo device in Enumerate())
            {
                if (device.IsCxt1024Raw)
                    return device;
            }

            return null;
        }
    }

    internal sealed class HidSession : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly FileStream _stream;
        private readonly int _inputLength;
        private readonly int _outputLength;
        private bool _disposed;

        internal HidSession(HidDeviceInfo device)
        {
            if (device == null)
                throw new ArgumentNullException("device");

            _inputLength = Math.Max(1, (int)device.InputReportLength);
            _outputLength = Math.Max(33, (int)device.OutputReportLength);
            _handle = NativeMethods.CreateFile(
                device.Path,
                NativeMethods.GenericRead | NativeMethods.GenericWrite,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagOverlapped,
                IntPtr.Zero);

            if (_handle.IsInvalid)
                NativeMethods.ThrowLastWin32("Unable to open the CXT 12E4 raw HID interface");

            _stream = new FileStream(_handle, FileAccess.ReadWrite, Math.Max(64, _inputLength), true);
        }

        internal byte[] Exchange(byte command, byte[] arguments, int timeoutMilliseconds)
        {
            if (_disposed)
                throw new ObjectDisposedException("HidSession");
            if (arguments == null)
                arguments = new byte[0];
            if (arguments.Length > 31)
                throw new ArgumentOutOfRangeException("arguments", "A VIA packet can contain at most 31 argument bytes.");

            byte[] output = new byte[_outputLength];
            output[0] = 0;
            output[1] = command;
            Buffer.BlockCopy(arguments, 0, output, 2, arguments.Length);

            IAsyncResult write = _stream.BeginWrite(output, 0, output.Length, null, null);
            WaitOrCancel(write, timeoutMilliseconds, "write");
            _stream.EndWrite(write);
            _stream.Flush();

            byte[] input = new byte[_inputLength];
            IAsyncResult read = _stream.BeginRead(input, 0, input.Length, null, null);
            WaitOrCancel(read, timeoutMilliseconds, "read");
            int bytesRead = _stream.EndRead(read);
            if (bytesRead <= 0)
                throw new IOException("The keyboard returned an empty HID response.");

            byte[] response;
            if (input[0] == 0 && bytesRead > 1 && input[1] == command)
            {
                response = new byte[bytesRead - 1];
                Buffer.BlockCopy(input, 1, response, 0, response.Length);
            }
            else
            {
                response = new byte[bytesRead];
                Buffer.BlockCopy(input, 0, response, 0, response.Length);
            }

            if (response.Length == 0 || response[0] != command)
                throw new IOException("The keyboard returned a response for a different VIA command.");
            for (int i = 0; i < arguments.Length; i++)
            {
                int responseIndex = i + 1;
                if (responseIndex >= response.Length || response[responseIndex] != arguments[i])
                    throw new IOException("The keyboard response did not echo the VIA command arguments.");
            }

            return response;
        }

        internal byte[] ExchangeRaw(byte[] payload, int timeoutMilliseconds)
        {
            if (_disposed)
                throw new ObjectDisposedException("HidSession");
            if (payload == null)
                throw new ArgumentNullException("payload");
            if (payload.Length != 32)
                throw new ArgumentOutOfRangeException("payload", "A QMK Raw HID report must contain exactly 32 payload bytes.");

            byte[] output = new byte[_outputLength];
            output[0] = 0;
            Buffer.BlockCopy(payload, 0, output, 1, payload.Length);

            IAsyncResult write = _stream.BeginWrite(output, 0, output.Length, null, null);
            WaitOrCancel(write, timeoutMilliseconds, "raw write");
            _stream.EndWrite(write);
            _stream.Flush();

            byte[] input = new byte[_inputLength];
            IAsyncResult read = _stream.BeginRead(input, 0, input.Length, null, null);
            WaitOrCancel(read, timeoutMilliseconds, "raw read");
            int bytesRead = _stream.EndRead(read);
            if (bytesRead < payload.Length)
                throw new IOException("The keyboard returned a short Raw HID response.");

            int offset = bytesRead >= payload.Length + 1 && input[0] == 0 ? 1 : 0;
            if (bytesRead - offset < payload.Length)
                throw new IOException("The keyboard Raw HID response did not contain 32 payload bytes.");
            var response = new byte[payload.Length];
            Buffer.BlockCopy(input, offset, response, 0, response.Length);
            return response;
        }

        private void WaitOrCancel(IAsyncResult operation, int timeoutMilliseconds, string operationName)
        {
            if (operation.AsyncWaitHandle.WaitOne(timeoutMilliseconds))
                return;

            NativeMethods.CancelIoEx(_handle, IntPtr.Zero);
            operation.AsyncWaitHandle.WaitOne(250);
            throw new TimeoutException("CXT 12E4 HID " + operationName + " timed out.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _stream.Dispose();
        }
    }
}
