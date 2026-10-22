using System;

namespace Cxt1024Bridge
{
    internal sealed class Cxt12E4LightingProtocol : IDisposable
    {
        internal const ushort CustomFirmwareVersion = 0x0100;
        private const byte ProtocolVersion = 1;
        private const byte PingCommand = 1;
        private const byte SetStateCommand = 2;
        private const byte SetTestRgbCommand = 3;
        private const byte BootloaderCommand = 0x7F;
        private const byte ResponseBit = 0x80;
        private readonly HidSession _session;
        private byte _sequence;

        internal Cxt12E4LightingProtocol(HidDeviceInfo device)
        {
            if (!IsCustomFirmware(device))
                throw new InvalidOperationException("The connected CXT 12E4 is not running the task-light firmware.");
            _session = new HidSession(device);
        }

        internal static bool IsCustomFirmware(HidDeviceInfo device)
        {
            return device != null && device.IsCxt1024Raw && device.VersionNumber == CustomFirmwareVersion;
        }

        internal void Ping()
        {
            Exchange(PingCommand, 0, null, null, null, null, false);
        }

        internal void SetState(
            int logicalLayer,
            AiTaskState[] taskStates,
            bool[] taskPresent,
            bool dangerFlash)
        {
            SetState(logicalLayer, taskStates, taskPresent, null, null, dangerFlash);
        }

        internal void SetState(
            int logicalLayer,
            AiTaskState[] taskStates,
            bool[] taskPresent,
            AiTaskState[] terminalStates,
            bool[] terminalPresent,
            bool dangerFlash)
        {
            if (logicalLayer < 0 || logicalLayer > 7)
                logicalLayer = 0;
            Exchange(
                SetStateCommand,
                logicalLayer,
                taskStates,
                taskPresent,
                terminalStates,
                terminalPresent,
                dangerFlash);
        }

        internal void SetTestRgb(int physicalKey, byte red, byte green, byte blue)
        {
            if (physicalKey < 1 || physicalKey > 12)
                throw new ArgumentOutOfRangeException("physicalKey");
            var packet = CreatePacket(SetTestRgbCommand);
            packet[6] = (byte)physicalKey;
            packet[7] = red;
            packet[8] = green;
            packet[9] = blue;
            ExchangePacket(packet, SetTestRgbCommand);
        }

        internal void EnterBootloader()
        {
            var packet = CreatePacket(BootloaderCommand);
            packet[7] = 0x42;
            packet[8] = 0x4F;
            packet[9] = 0x4F;
            packet[10] = 0x54;
            ExchangePacket(packet, BootloaderCommand);
        }

        private void Exchange(
            byte command,
            int logicalLayer,
            AiTaskState[] taskStates,
            bool[] taskPresent,
            AiTaskState[] terminalStates,
            bool[] terminalPresent,
            bool dangerFlash)
        {
            byte[] packet = CreatePacket(command);
            packet[6] = (byte)logicalLayer;
            if (command == SetStateCommand)
            {
                for (int i = 0; i < 6; i++)
                {
                    bool present = taskPresent != null && i < taskPresent.Length && taskPresent[i];
                    AiTaskState state = taskStates != null && i < taskStates.Length
                        ? taskStates[i]
                        : AiTaskState.Idle;
                    packet[7 + i] = EncodeTaskState(present, state);

                    bool terminalIsPresent = terminalPresent != null &&
                        i < terminalPresent.Length && terminalPresent[i];
                    AiTaskState terminalState = terminalStates != null && i < terminalStates.Length
                        ? terminalStates[i]
                        : AiTaskState.Idle;
                    packet[15 + i] = EncodeTaskState(terminalIsPresent, terminalState);
                }
                packet[14] = dangerFlash ? (byte)1 : (byte)0;
            }
            ExchangePacket(packet, command);
        }

        private byte[] CreatePacket(byte command)
        {
            var packet = new byte[32];
            packet[0] = 0x43;
            packet[1] = 0x31;
            packet[2] = 0x32;
            packet[3] = 0x45;
            packet[4] = ProtocolVersion;
            packet[5] = command;
            packet[13] = unchecked(++_sequence);
            return packet;
        }

        private void ExchangePacket(byte[] packet, byte command)
        {
            byte[] response = _session.ExchangeRaw(packet, 700);
            if (response[0] != packet[0] ||
                response[1] != packet[1] ||
                response[2] != packet[2] ||
                response[3] != packet[3] ||
                response[4] != ProtocolVersion ||
                response[5] != (byte)(command | ResponseBit) ||
                response[13] != packet[13])
                throw new InvalidOperationException("The CXT 12E4 task-light firmware returned an invalid protocol response.");
        }

        private static byte EncodeTaskState(bool present, AiTaskState state)
        {
            if (!present)
                return 0;
            switch (state)
            {
                case AiTaskState.Thinking:
                    return 2;
                case AiTaskState.Completed:
                    return 3;
                case AiTaskState.WaitingForInput:
                    return 4;
                case AiTaskState.Error:
                    return 5;
                default:
                    return 1;
            }
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
