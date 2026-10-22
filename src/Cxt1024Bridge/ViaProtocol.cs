using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    internal sealed class ViaProtocol : IDisposable
    {
        internal const byte QmkRgbBrightness = 128;
        internal const byte QmkRgbEffect = 129;
        internal const byte QmkRgbEffectSpeed = 130;
        internal const byte QmkRgbColor = 131;
        private readonly HidSession _session;

        internal ViaProtocol(HidDeviceInfo device)
        {
            _session = new HidSession(device);
        }

        internal byte[] Command(byte command, params byte[] arguments)
        {
            return _session.Exchange(command, arguments, 1200);
        }

        internal int GetProtocolVersion()
        {
            return ReadU16(Command(11), 1);
        }

        internal int GetCapabilityC()
        {
            return ReadU16(Command(32), 1);
        }

        internal int GetCapabilityX()
        {
            return ReadU16(Command(33), 1);
        }

        internal int GetCapabilityT()
        {
            return ReadU16(Command(34), 1);
        }

        internal int GetLayerCount(int protocolVersion)
        {
            if (protocolVersion >= 8)
            {
                byte[] response = Command(9);
                RequireLength(response, 2);
                return response[1];
            }

            if (protocolVersion == 7)
                return 4;
            throw new InvalidOperationException("Unsupported VIA protocol version: " + protocolVersion);
        }

        internal ushort[,] ReadLayer(int layer, int rows, int columns, int protocolVersion)
        {
            var result = new ushort[rows, columns];
            if (protocolVersion >= 8)
            {
                int keyCount = rows * columns;
                int baseOffset = layer * keyCount * 2;
                var bytes = new List<byte>(keyCount * 2);
                int remaining = keyCount * 2;
                int offset = 0;
                while (remaining > 0)
                {
                    int length = Math.Min(28, remaining);
                    byte[] offsetBytes = SplitU16(baseOffset + offset);
                    byte[] response = Command(7, offsetBytes[0], offsetBytes[1], (byte)length);
                    RequireLength(response, 4 + length);
                    for (int i = 0; i < length; i++)
                        bytes.Add(response[4 + i]);
                    offset += length;
                    remaining -= length;
                }

                for (int index = 0; index < keyCount; index++)
                    result[index / columns, index % columns] = (ushort)((bytes[index * 2] << 8) | bytes[index * 2 + 1]);
                return result;
            }

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    byte[] response = Command(10, (byte)layer, (byte)row, (byte)column);
                    RequireLength(response, 6);
                    result[row, column] = (ushort)((response[4] << 8) | response[5]);
                }
            }

            return result;
        }

        internal void WriteAllLayers(ushort[,,] layers, int protocolVersion)
        {
            if (layers == null)
                throw new ArgumentNullException("layers");
            int layerCount = layers.GetLength(0);
            int rows = layers.GetLength(1);
            int columns = layers.GetLength(2);
            if (protocolVersion >= 8)
            {
                var bytes = new List<byte>(layerCount * rows * columns * 2);
                for (int layer = 0; layer < layerCount; layer++)
                {
                    for (int row = 0; row < rows; row++)
                    {
                        for (int column = 0; column < columns; column++)
                        {
                            ushort keycode = layers[layer, row, column];
                            bytes.Add((byte)((keycode >> 8) & 0xFF));
                            bytes.Add((byte)(keycode & 0xFF));
                        }
                    }
                }

                for (int offset = 0; offset < bytes.Count; offset += 28)
                {
                    int length = Math.Min(28, bytes.Count - offset);
                    byte[] offsetBytes = SplitU16(offset);
                    var arguments = new byte[3 + length];
                    arguments[0] = offsetBytes[0];
                    arguments[1] = offsetBytes[1];
                    arguments[2] = (byte)length;
                    bytes.CopyTo(offset, arguments, 3, length);
                    Command(12, arguments);
                }
                return;
            }

            for (int layer = 0; layer < layerCount; layer++)
            {
                for (int row = 0; row < rows; row++)
                {
                    for (int column = 0; column < columns; column++)
                    {
                        ushort keycode = layers[layer, row, column];
                        Command(
                            16,
                            (byte)layer,
                            (byte)row,
                            (byte)column,
                            (byte)((keycode >> 8) & 0xFF),
                            (byte)(keycode & 0xFF));
                    }
                }
            }
        }

        internal int GetMacroCount()
        {
            byte[] response = Command(19);
            RequireLength(response, 2);
            return response[1];
        }

        internal int GetMacroBufferSize()
        {
            return ReadU16(Command(8), 1);
        }

        internal byte[] ReadMacroBuffer(int size)
        {
            var result = new List<byte>(size);
            for (int offset = 0; offset < size; offset += 28)
            {
                int length = Math.Min(28, size - offset);
                byte[] offsetBytes = SplitU16(offset);
                byte[] response = Command(6, offsetBytes[0], offsetBytes[1], (byte)length);
                RequireLength(response, 4 + length);
                for (int i = 0; i < length; i++)
                    result.Add(response[4 + i]);
            }
            return result.ToArray();
        }

        internal byte[] GetBacklightValue(byte valueId, params byte[] extraArguments)
        {
            var arguments = new byte[1 + extraArguments.Length];
            arguments[0] = valueId;
            Buffer.BlockCopy(extraArguments, 0, arguments, 1, extraArguments.Length);
            return Command(13, arguments);
        }

        internal void SetBacklightValue(byte valueId, params byte[] values)
        {
            var arguments = new byte[1 + (values == null ? 0 : values.Length)];
            arguments[0] = valueId;
            if (values != null && values.Length > 0)
                Buffer.BlockCopy(values, 0, arguments, 1, values.Length);
            Command(18, arguments);
        }

        internal void SaveLighting()
        {
            Command(17);
        }

        private static int ReadU16(byte[] value, int offset)
        {
            RequireLength(value, offset + 2);
            return (value[offset] << 8) | value[offset + 1];
        }

        private static byte[] SplitU16(int value)
        {
            if (value < 0 || value > ushort.MaxValue)
                throw new ArgumentOutOfRangeException("value");
            return new[] { (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) };
        }

        private static void RequireLength(byte[] value, int minimum)
        {
            if (value == null || value.Length < minimum)
                throw new InvalidOperationException("The VIA response was shorter than expected.");
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
