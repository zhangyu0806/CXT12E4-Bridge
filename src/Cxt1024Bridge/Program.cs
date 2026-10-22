using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string command = args.Length == 0 ? "--run" : args[0].ToLowerInvariant();
                if (command == "--run" || command == "--watchdog" || command == "--watchdog-once")
                    HideStartupConsole();
                if (command == "--help" || command == "-h" || command == "/?")
                {
                    PrintHelp();
                    return 0;
                }
                if (command == "--dump-default-profiles")
                {
                    var result = new List<object>();
                    foreach (int layer in new[] { 1, 2, 3, 4, 5, 6, 7, 0 })
                    {
                        var devices = new List<object>();
                        foreach (string device in new[] { "keyboard", "xbox" })
                        {
                            var controls = new List<object>();
                            Dictionary<string, string> defaults = WindowsLayerProfiles.Defaults(device, layer);
                            foreach (ProfileControl control in WindowsLayerProfiles.Controls(device))
                            {
                                string action = defaults[control.Id];
                                controls.Add(new { control = control.Id, label = control.Title, action = action,
                                    title = WindowsLayerProfiles.Title(action, device, control.Id, layer) });
                            }
                            devices.Add(new { device = device, controls = controls });
                        }
                        result.Add(new { layer = layer == 0 ? 8 : layer, name = WindowsLayerProfiles.LayerName(layer), devices = devices });
                    }
                    Console.OutputEncoding = new UTF8Encoding(false);
                    Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result));
                    return 0;
                }
                if (command == "--run")
                    return RunBridge();
                if (command == "--exit")
                    return ExitRunningBridge();
                if (command == "--watchdog")
                    return BridgeWatchdog.Run(args, false);
                if (command == "--watchdog-once")
                    return BridgeWatchdog.Run(args, true);
                if (command == "--watchdog-exit")
                    return BridgeWatchdog.ExitRunningWatchdog();
                if (command == "--list")
                    return ListDevices();
                if (command == "--firmware-probe")
                    return FirmwareProbe();
                if (command == "--task-light-test-white")
                    return TaskLightTestWhite(args);
                if (command == "--all-light-test-white")
                    return AllLightTestWhite(args);
                if (command == "--task-light-test")
                    return TaskLightTest(args);
                if (command == "--rgb-test")
                    return RgbTest(args);
                if (command == "--rgb-compare")
                    return RgbCompare(args);
                if (command == "--layer-palette-preview")
                    return LayerPalettePreview();
                if (command == "--enter-custom-bootloader")
                    return EnterCustomBootloader();
                if (command == "--probe")
                    return Probe(false, args);
                if (command == "--backup")
                    return Probe(true, args);
                if (command == "--readonly-capability-probe")
                    return ReadOnlyCapabilityProbe(args);
                if (command == "--dry-run")
                    return DryRun(args);
                if (command == "--self-test")
                    return SelfTest();
                if (command == "--raw-input-probe")
                    return RawInputProbe(args);
                if (command == "--raw-input-list")
                    return RawInputList();
                if (command == "--status")
                    return Status();
                if (command == "--chatgpt-probe")
                    return ChatGptProbe(args);
                if (command == "--codex-model-controls")
                {
                    var controls = new ChatGptAutomation();
                    string action = args.Length > 1 ? args[1].ToLowerInvariant() : "inspect";
                    Dictionary<string, object> result = controls.InspectModelControls(action);
                    Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result));
                    return result.ContainsKey("actionSucceeded") && !(bool)result["actionSucceeded"] ? 1 : 0;
                }
                if (command == "--chatgpt-status-probe")
                    return ChatGptStatusProbe(args);
                if (command == "--tmux-session-probe")
                    return TmuxSessionProbe();
                if (command == "--codex-app-server-probe")
                    return CodexAppServerProbe(args);
                if (command == "--chatgpt-deeplink-probe")
                    return ChatGptDeepLinkProbe();
                if (command == "--verify-keyboard")
                    return VerifyKeyboard();
                if (command == "--lighting-preview")
                    return LightingPreview(args);
                if (command == "--enter-factory-bootloader")
                    return EnterFactoryBootloader(args);
                if (command == "--configure-keyboard")
                    return ConfigureKeyboard(args);

                Console.Error.WriteLine("Unknown command: " + args[0]);
                PrintHelp();
                return 2;
            }
            catch (Exception ex)
            {
                Logger.Error("Command failed", ex);
                Console.Error.WriteLine("ERROR: " + ex.Message);
                if (Environment.GetEnvironmentVariable("CXT_BRIDGE_DEBUG") == "1")
                    Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                Logger.FlushAndStop();
            }
        }

        private static int RunBridge()
        {
            // Keep the PE console subsystem (accepted by Smart App Control), then detach
            // immediately so normal/startup use has no persistent console window.
            CodexAppServerThreadCatalog.PrepareStdioEncoding();
            NativeMethods.FreeConsole();
            bool created;
            using (var mutex = new Mutex(true, "Local\\Cxt1024Bridge-5754-C401", out created))
            using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Cxt1024Bridge-Exit"))
            {
                if (!created)
                {
                    Logger.Warn("A second bridge instance was rejected.");
                    return 4;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var context = new BridgeApplicationContext())
                {
                    RegisteredWaitHandle exitWait = ThreadPool.RegisterWaitForSingleObject(
                        exitEvent,
                        delegate { context.RequestExit(); },
                        null,
                        Timeout.Infinite,
                        true);
                    try
                    {
                        Application.Run(context);
                    }
                    finally
                    {
                        exitWait.Unregister(null);
                    }
                }
            }
            return 0;
        }

        private static void HideStartupConsole()
        {
            IntPtr console = NativeMethods.GetConsoleWindow();
            if (console != IntPtr.Zero)
                NativeMethods.ShowWindow(console, NativeMethods.SwHide);
        }

        private static int ExitRunningBridge()
        {
            try
            {
                using (EventWaitHandle exitEvent = EventWaitHandle.OpenExisting("Local\\Cxt1024Bridge-Exit"))
                    exitEvent.Set();
                Console.WriteLine("Exit signal sent to the running bridge.");
                return 0;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Console.WriteLine("No running bridge instance was found.");
                return 3;
            }
        }

        private static int ListDevices()
        {
            IList<HidDeviceInfo> devices = HidEnumerator.Enumerate();
            int matches = 0;
            foreach (HidDeviceInfo device in devices)
            {
                if (device.VendorId != 0x5754 || device.ProductId != 0xC401)
                    continue;
                Console.WriteLine((device.IsCxt1024Raw ? "RAW  " : "HID  ") + device);
                Console.WriteLine("     " + device.Path);
                if (device.IsCxt1024Raw)
                    matches++;
            }
            Console.WriteLine("CXT 12E4 raw HID interfaces found: " + matches.ToString(CultureInfo.InvariantCulture));
            return matches == 1 ? 0 : 3;
        }

        private static int Probe(bool completeBackup, string[] args)
        {
            HidDeviceInfo device = RequireDevice();
            Dictionary<string, object> result = BackupService.Probe(device, completeBackup);
            string outputPath = GetOutputPath(args, completeBackup
                ? "cxt1024-backup-" + Stamp() + ".json"
                : "cxt1024-probe.json");
            BackupService.WriteJson(outputPath, result);
            Console.WriteLine(completeBackup ? "Read-only backup completed." : "Read-only probe completed.");
            Console.WriteLine(Path.GetFullPath(outputPath));
            Console.WriteLine("No keyboard write/configuration command was sent.");
            return 0;
        }

        private static int FirmwareProbe()
        {
            HidDeviceInfo device = RequireDevice();
            if (!Cxt12E4LightingProtocol.IsCustomFirmware(device))
                throw new InvalidOperationException("CXT 12E4 task-light firmware version 1.0 was not detected.");
            using (var protocol = new Cxt12E4LightingProtocol(device))
                protocol.Ping();
            Console.WriteLine("CXT 12E4 task-light firmware protocol 1 ping/response passed.");
            Console.WriteLine(device);
            Console.WriteLine("No layer, task state, lighting, EEPROM, or keymap value was changed.");
            return 0;
        }

        private static int TaskLightTestWhite(string[] args)
        {
            int seconds = 120;
            if (args.Length >= 2 &&
                (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) ||
                 seconds < 1 || seconds > 600))
                throw new ArgumentException("Test duration must be from 1 to 600 seconds.");

            HidDeviceInfo device = RequireDevice();
            if (!Cxt12E4LightingProtocol.IsCustomFirmware(device))
                throw new InvalidOperationException("CXT 12E4 task-light firmware version 1.0 was not detected.");
            var states = new AiTaskState[6];
            var present = new bool[6];
            for (int i = 0; i < present.Length; i++)
                present[i] = true;

            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            using (var protocol = new Cxt12E4LightingProtocol(device))
            {
                while (DateTime.UtcNow < end)
                {
                    protocol.SetState(1, states, present, false);
                    Thread.Sleep(500);
                }
            }
            return 0;
        }

        private static int AllLightTestWhite(string[] args)
        {
            int seconds = 15;
            if (args.Length >= 2 &&
                (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) ||
                 seconds < 1 || seconds > 600))
                throw new ArgumentException("Test duration must be from 1 to 600 seconds.");

            HidDeviceInfo device = RequireDevice();
            if (!Cxt12E4LightingProtocol.IsCustomFirmware(device))
                throw new InvalidOperationException("CXT 12E4 task-light firmware version 1.0 was not detected.");

            var taskStates = new AiTaskState[6];
            var taskPresent = new bool[6];
            var terminalStates = new AiTaskState[6];
            var terminalPresent = new bool[6];
            for (int i = 0; i < 6; i++)
            {
                taskPresent[i] = true;
                terminalPresent[i] = true;
            }

            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            using (var protocol = new Cxt12E4LightingProtocol(device))
            {
                while (DateTime.UtcNow < end)
                {
                    // L2 exposes both six-key banks, covering all 12 LEDs.
                    protocol.SetState(2, taskStates, taskPresent, terminalStates, terminalPresent, false);
                    Thread.Sleep(500);
                }
            }
            return 0;
        }

        private static int TaskLightTest(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("Usage: --task-light-test SLOT idle|thinking|completed|waiting|error [SEC]");
            int slot;
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) || slot < 1 || slot > 6)
                throw new ArgumentException("Task-light test slot must be from 1 to 6.");
            AiTaskState state;
            if (!Enum.TryParse(args[2], true, out state))
                throw new ArgumentException("Unknown task-light state: " + args[2]);
            int seconds = 120;
            if (args.Length >= 4 &&
                (!int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) ||
                 seconds < 1 || seconds > 600))
                throw new ArgumentException("Test duration must be from 1 to 600 seconds.");

            HidDeviceInfo device = RequireDevice();
            if (!Cxt12E4LightingProtocol.IsCustomFirmware(device))
                throw new InvalidOperationException("CXT 12E4 task-light firmware version 1.0 was not detected.");
            var states = new AiTaskState[6];
            var present = new bool[6];
            states[slot - 1] = state;
            present[slot - 1] = true;
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            using (var protocol = new Cxt12E4LightingProtocol(device))
            {
                while (DateTime.UtcNow < end)
                {
                    protocol.SetState(1, states, present, false);
                    Thread.Sleep(500);
                }
            }
            return 0;
        }

        private static int RgbTest(string[] args)
        {
            if (args.Length < 5)
                throw new ArgumentException("Usage: --rgb-test KEY R G B [SEC]");
            int key = ParseByteRange(args[1], "KEY", 1, 12);
            byte red = (byte)ParseByteRange(args[2], "R", 0, 255);
            byte green = (byte)ParseByteRange(args[3], "G", 0, 255);
            byte blue = (byte)ParseByteRange(args[4], "B", 0, 255);
            int seconds = args.Length >= 6 ? ParseByteRange(args[5], "SEC", 1, 600) : 120;
            HidDeviceInfo device = RequireDevice();
            using (var protocol = new Cxt12E4LightingProtocol(device))
            {
                DateTime end = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < end)
                {
                    protocol.SetTestRgb(key, red, green, blue);
                    Thread.Sleep(500);
                }
            }
            return 0;
        }

        private static int RgbCompare(string[] args)
        {
            if (args.Length < 6)
                throw new ArgumentException("Usage: --rgb-compare KEY1 KEY2 R G B [SEC]");
            int firstKey = ParseByteRange(args[1], "KEY1", 1, 12);
            int secondKey = ParseByteRange(args[2], "KEY2", 1, 12);
            byte firstRed = (byte)ParseByteRange(args[3], "R1", 0, 255);
            byte firstGreen = (byte)ParseByteRange(args[4], "G1", 0, 255);
            byte firstBlue = (byte)ParseByteRange(args[5], "B1", 0, 255);
            int seconds = args.Length >= 7 ? ParseByteRange(args[6], "SEC", 1, 600) : 120;
            byte secondRed = args.Length >= 10 ? (byte)ParseByteRange(args[7], "R2", 0, 255) : firstRed;
            byte secondGreen = args.Length >= 10 ? (byte)ParseByteRange(args[8], "G2", 0, 255) : firstGreen;
            byte secondBlue = args.Length >= 10 ? (byte)ParseByteRange(args[9], "B2", 0, 255) : firstBlue;
            HidDeviceInfo device = RequireDevice();
            using (var protocol = new Cxt12E4LightingProtocol(device))
            {
                DateTime end = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < end)
                {
                    // Firmware test mode stores one key. Alternate faster than
                    // visual persistence so two physical LEDs can be compared
                    // without flashing a special diagnostic firmware.
                    protocol.SetTestRgb(firstKey, firstRed, firstGreen, firstBlue);
                    Thread.Sleep(4);
                    protocol.SetTestRgb(secondKey, secondRed, secondGreen, secondBlue);
                    Thread.Sleep(4);
                }
            }
            return 0;
        }

        private static int LayerPalettePreview()
        {
            HidDeviceInfo device = RequireDevice();
            using (var protocol = new Cxt12E4LightingProtocol(device))
            {
                for (int layer = 1; layer <= 7; layer++)
                {
                    LayerDefinition definition = BridgeDefinitions.GetLayer(layer);
                    DateTime end = DateTime.UtcNow.AddMilliseconds(1900);
                    while (DateTime.UtcNow < end)
                    {
                        protocol.SetTestRgb(
                            definition.IndicatorKey,
                            definition.Red,
                            definition.Green,
                            definition.Blue);
                        Thread.Sleep(150);
                    }
                    protocol.SetTestRgb(definition.IndicatorKey, 0, 0, 0);
                    Thread.Sleep(300);
                }
            }
            return 0;
        }

        private static int EnterCustomBootloader()
        {
            HidDeviceInfo device = RequireDevice();
            using (var protocol = new Cxt12E4LightingProtocol(device))
                protocol.EnterBootloader();
            Console.WriteLine("Custom firmware bootloader request acknowledged.");
            return 0;
        }

        private static int ParseByteRange(string value, string name, int minimum, int maximum)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ||
                parsed < minimum || parsed > maximum)
                throw new ArgumentException(name + " must be from " + minimum + " to " + maximum + ".");
            return parsed;
        }

        private static int DryRun(string[] args)
        {
            Dictionary<string, object> result = BridgeSpecification.Describe();
            string outputPath = GetOutputPath(args, "cxt1024-bridge-dry-run.json");
            BackupService.WriteJson(outputPath, result);
            Console.WriteLine("Bridge dry run completed; no hook installed and no input or HID write was sent.");
            Console.WriteLine(Path.GetFullPath(outputPath));
            return 0;
        }

        private static int ReadOnlyCapabilityProbe(string[] args)
        {
            HidDeviceInfo device = RequireDevice();
            var result = new Dictionary<string, object>();
            result["schemaVersion"] = 1;
            result["capturedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            result["mode"] = "read-only-capability-probe";
            result["writesSent"] = false;

            using (var via = new ViaProtocol(device))
            {
                result["protocolVersion"] = via.GetProtocolVersion();
                result["keyboardValues"] = ReadValueRange(via, false);
                result["lightingValues"] = ReadValueRange(via, true);
            }

            string outputPath = GetOutputPath(args, "cxt1024-readonly-capability-probe-" + Stamp() + ".json");
            BackupService.WriteJson(outputPath, result);
            Console.WriteLine("Read-only capability probe completed.");
            Console.WriteLine(Path.GetFullPath(outputPath));
            Console.WriteLine("Only VIA get-keyboard-value (3) and get-backlight-value (13) commands were sent.");
            Console.WriteLine("No set, save, keymap-write, macro-write, reset, bootloader, or firmware command was sent.");
            return 0;
        }

        private static List<object> ReadValueRange(ViaProtocol via, bool lighting)
        {
            var values = new List<object>();
            for (int id = 0; id <= 255; id++)
            {
                byte[] response;
                try
                {
                    response = lighting
                        ? via.Command(13, (byte)id)
                        : via.Command(3, (byte)id);
                }
                catch (Exception ex)
                {
                    values.Add(new Dictionary<string, object>
                    {
                        { "id", id },
                        { "error", ex.Message }
                    });
                    continue;
                }

                var entry = new Dictionary<string, object>();
                entry["id"] = id;
                entry["responseHex"] = BitConverter.ToString(response).Replace("-", string.Empty);
                values.Add(entry);
            }
            return values;
        }

        private static int EnterFactoryBootloader(string[] args)
        {
            string confirmation = GetRequiredArgument(args, "--confirm");
            if (!string.Equals(confirmation, "CXT12E4-REV8", StringComparison.Ordinal))
                throw new InvalidOperationException("Bootloader confirmation must be exactly CXT12E4-REV8.");

            HidDeviceInfo device = RequireDevice();
            if (device.VersionNumber != 0x0008)
                throw new InvalidOperationException(
                    "Refusing factory VIA bootloader command: expected revision 0x0008 but found 0x" +
                    device.VersionNumber.ToString("X4", CultureInfo.InvariantCulture) + ".");

            Console.WriteLine("Verified factory CXT 12E4 Rev.8 raw HID interface.");
            Console.WriteLine("Sending the CXT VIA-C bootloader-jump command (0x01)...");
            try
            {
                using (var via = new ViaProtocol(device))
                    via.Command(0x01);
            }
            catch (Exception ex)
            {
                // A successful jump normally disconnects HID before a response.
                // The caller must still verify 03EB:2FF4 before any erase/write.
                Console.WriteLine("HID disconnected while jumping: " + ex.Message);
            }
            Console.WriteLine("Bootloader command sent. No erase or firmware write was performed.");
            return 0;
        }

        private static int SelfTest()
        {
            WindowsProfileTests.Run();
            WindowsControllerTests.Run();
            WindowsDeviceIntegrationTests.Run();
            InputResilienceTests.Run();
            AuditL1Tests.Run();
            AuditRuntimeTests.Run();
            AuditApplicationTests.Run();
            PrivateTriggerTests.Run();
            AuditLayerTests.Run();
            var exportStates = new[] { AiTaskState.Idle, AiTaskState.Thinking, AiTaskState.Completed, AiTaskState.WaitingForInput, AiTaskState.Error, AiTaskState.Idle };
            var exportPresent = new[] { true, true, true, true, true, false };
            string exported = PassportStatusExport.Serialize(exportStates, exportPresent, exportStates, exportPresent, 123);
            Assert(exported == "{\"version\":1,\"observed_at_ms\":123,\"codex\":[1,2,3,4,5,0],\"tmux\":[1,2,3,4,5,0]}", "Passport semantic LED export contains only states");
            ushort[,,] map = KeyboardConfiguration.BuildTriggerKeymap();
            Assert(map.GetLength(0) == 8 && map.GetLength(1) == 4 && map.GetLength(2) == 6, "keymap dimensions");
            var unique = new HashSet<ushort>();
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 6; column++)
                    Assert(unique.Add(map[0, row, column]), "unique trigger at R" + row + " C" + column);
            }
            Assert(unique.Count == 24, "24 unique triggers");
            Assert(KeyboardHook.NormalizeEncoderNumber(1, EncoderGesture.Clockwise) == 4, "rotary index 1 is physical K4");
            Assert(KeyboardHook.NormalizeEncoderNumber(2, EncoderGesture.CounterClockwise) == 3, "rotary index 2 is physical K3");
            Assert(KeyboardHook.NormalizeEncoderNumber(3, EncoderGesture.Clockwise) == 1, "rotary index 3 is physical K1");
            Assert(KeyboardHook.NormalizeEncoderNumber(4, EncoderGesture.CounterClockwise) == 2, "rotary index 4 is physical K2");
            for (int encoder = 1; encoder <= 4; encoder++)
                Assert(KeyboardHook.NormalizeEncoderNumber(encoder, EncoderGesture.Press) == encoder, "encoder presses retain physical order K" + encoder);
            Assert(RawKeyboardInput.NormalizeVirtualKey(0x11, NativeMethods.RiKeyE0) == 0xA3, "Raw Input E0 control is normalized to right control");
            Assert(RawKeyboardInput.NormalizeVirtualKey(0x11, 0) == 0x11, "Raw Input left control remains generic control");
            Assert(RawKeyboardInput.IsTargetDeviceName("\\\\?\\HID#VID_5754&PID_C401&MI_00#ABC"), "strict CXT Raw Input device path is accepted");
            Assert(!RawKeyboardInput.IsTargetDeviceName("\\\\?\\HID#VID_5754&PID_C4010#ABC"), "near-match Raw Input product id is rejected");
            Assert(!RawKeyboardInput.IsTargetDeviceName("\\\\?\\HID#VID_57540&PID_C401#ABC"), "near-match Raw Input vendor id is rejected");
            var expectedRawInterfaces = new List<KeyValuePair<IntPtr, string>>
            {
                new KeyValuePair<IntPtr, string>(new IntPtr(1), "\\\\?\\HID#VID_5754&PID_C401&MI_00#A"),
                new KeyValuePair<IntPtr, string>(new IntPtr(2), "\\\\?\\HID#VID_5754&PID_C401&MI_02&Col04#B")
            };
            Assert(RawKeyboardInput.HasExpectedFirmwareKeyboardTopology(expectedRawInterfaces), "one boot and one NKRO TLC form one CXT firmware keyboard");
            expectedRawInterfaces.Add(new KeyValuePair<IntPtr, string>(new IntPtr(3), "\\\\?\\HID#VID_5754&PID_C401&MI_00#C"));
            Assert(!RawKeyboardInput.HasExpectedFirmwareKeyboardTopology(expectedRawInterfaces), "a second same-model keyboard fails closed");
            uint idleTimeout = LightingController.IdleLightsOffMilliseconds;
            Assert(!LightingController.HasReachedIdleLightsOff(idleTimeout - 1u, 0u), "lighting remains active before 40 minutes of Windows input idle time");
            Assert(LightingController.HasReachedIdleLightsOff(idleTimeout, 0u), "lighting heartbeat pauses at exactly 40 minutes of Windows input idle time");
            Assert(LightingController.HasReachedIdleLightsOff(1000u, unchecked(uint.MaxValue - idleTimeout + 1001u)), "lighting idle timeout remains correct across the Windows tick-count wrap");
            Assert(!LightingController.HasReachedIdleLightsOff(1000u, 2000u), "an anomalous future last-input tick fails open and keeps lighting active");
            Assert(ChatGptAutomation.GetReasoningRank("Effort Light") == 0, "Light reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("Effort Medium") == 1, "Medium reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("Effort High") == 2, "High reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("Effort Extra High") == 3, "Extra High reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("Effort Max") == 4, "Max reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("Effort Ultra") == 5, "Ultra reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("极高 更快消耗使用额度") == 5, "current Chinese purple Ultra option rank");
            Assert(ChatGptAutomation.GetReasoningRank("5.6 Sol 轻度") == 0, "current Chinese low reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("5.6 Sol 中") == 1, "current Chinese medium reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("5.6 Sol 高") == 2, "current Chinese high reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("5.6 Sol 极高") == 3, "current Chinese extra-high reasoning rank");
            Assert(ChatGptAutomation.GetReasoningRank("5.6 Sol 最高") == 4, "current Chinese maximum reasoning rank");
            Assert(ChatGptAutomation.IsUltraReasoningOptionName("极高 更快消耗使用额度"), "current Chinese purple Ultra option is recognized");
            Assert(!ChatGptAutomation.IsUltraReasoningOptionName("极高"), "ordinary extra-high option is not mistaken for purple Ultra");
            Assert(ChatGptAutomation.HighReasoningRank == 2, "K2 encoder press resets reasoning to High");
            Assert(ChatGptAutomation.MaximumRotaryReasoningRank == 4, "reasoning encoder rotation stops at Max before purple Ultra");
            Assert(ChatGptAutomation.UltraReasoningRank == 5, "K1 encoder press targets purple Ultra");
            Assert(ChatGptAutomation.IsRotaryReasoningRank(4), "Max remains reachable by reasoning encoder rotation");
            Assert(!ChatGptAutomation.IsRotaryReasoningRank(5), "purple Ultra is excluded from reasoning encoder rotation");
            Assert(ChatGptAutomation.ResolveReasoningRank(3, -1, int.MaxValue, true) == 3, "clockwise from Chinese extra-high can advance to Max without skipping into Ultra");
            Assert(ChatGptAutomation.ResolveReasoningRank(3, 5, 500, false) == 5, "recent purple Ultra selection decreases from rank five");
            Assert(ChatGptAutomation.ResolveReasoningRank(3, 5, 15000, false) == 3, "stale Ultra memory is not reused indefinitely");
            Assert(ChatGptAutomation.IsUltraFullAccessDialogName("是否启用 Ultra 搭配完整访问权限？"), "current Chinese Ultra full-access dialog is accepted");
            Assert(ChatGptAutomation.IsUltraFullAccessDialogName("Enable Ultra with full access?"), "English Ultra full-access dialog is accepted");
            Assert(!ChatGptAutomation.IsUltraFullAccessDialogName("允许执行这个命令？"), "generic permission dialog is not accepted for automatic full access");
            Assert(ChatGptAutomation.IsReasoningSelectorName("5.6 Sol 最高"), "current Chinese model selector is accepted");
            Assert(ChatGptAutomation.IsReasoningSelectorName("GPT-6 Astra 高"), "Astra Chinese High selector is recognized");
            Assert(ChatGptAutomation.IsReasoningSelectorName("6 Astra Ultra"), "Astra selector without GPT prefix is recognized");
            Assert(!ChatGptAutomation.IsReasoningSelectorName("Debug GPT-6 Astra High"), "Astra diagnostic labels cannot be invoked as model selectors");
            Assert(ChatGptAutomation.IsAstraModelLabel("GPT-6 Astra"), "current Astra radio option is accepted");
            Assert(!ChatGptAutomation.IsAstraModelLabel("GPT-6 Astra Pro"), "Astra preset never selects a different Astra variant");
            Assert(ChatGptAutomation.AstraReasoningMatches("GPT-6 Astra 高", 2), "K2 requires Astra High");
            Assert(ChatGptAutomation.AstraReasoningMatches("GPT-6 Astra Ultra", 5), "K1 requires Astra Ultra");
            Assert(ChatGptAutomation.AstraReasoningMatches("6 Astra 最高", 4), "Astra rotary maximum is Max");
            Assert(!ChatGptAutomation.AstraReasoningMatches("5.6 Sol High", 2), "the right effort on the old model cannot pass Astra verification");
            Assert(!ChatGptAutomation.AstraReasoningMatches("GPT-6 Astra 极高", 5), "Extra High cannot pass the Ultra postcondition");
            Assert(!ChatGptAutomation.IsAstraReasoningSelectorName("GPT-6 Astra Pro High"), "variant text is rejected by Astra postconditions");
            Assert(!ChatGptAutomation.IsAstraReasoningSelectorName("Debug GPT-6 Astra High"), "a task label cannot pass the Astra model check");
            Assert(!ChatGptAutomation.IsAstraReasoningSelectorName("GPT-6 Astra High\ncommand"), "multiline tool text cannot pass the Astra model check");
            Assert(ChatGptAutomation.IsReasoningSelectorName("GPT-5.6 Extra High"), "English model selector is accepted");
            Assert(ChatGptAutomation.IsReasoningSelectorName("Effort High"), "legacy effort selector is accepted");
            Assert(!ChatGptAutomation.IsReasoningSelectorName("正在运行 reasoningSelector diagnostic High\ncommand body"), "running tool button is not mistaken for the reasoning selector");
            Assert(ChatGptAutomation.IsGenericReasoningSelectorName("推理强度"), "generic Chinese reasoning selector is accepted");
            Assert(ChatGptAutomation.IsGenericReasoningSelectorName("Select effort"), "expanded current selector remains discoverable for deterministic collapse");
            Assert(!ChatGptAutomation.IsGenericReasoningSelectorName("正在运行 reasoning probe"), "arbitrary reasoning text is not a generic selector");
            Assert(ChatGptAutomation.IsCompactReasoningSliderIdentity("强度", "menu _SliderKeyboardControl_abc"), "current Chinese compact keyboard slider is accepted");
            Assert(ChatGptAutomation.IsCompactReasoningSliderIdentity("Strength", "menu _SliderKeyboardControl_abc"), "current English compact keyboard slider is accepted");
            Assert(!ChatGptAutomation.IsCompactReasoningSliderIdentity("强度", "ordinary-menu-item"), "ordinary strength menu item is not mistaken for the compact slider");
            Assert(ChatGptAutomation.IsSolModelLabel("GPT-6 Sol") && !ChatGptAutomation.IsSolModelLabel("GPT-5.6 Sol"),
                "K1 model crossing selects exact GPT-6 Sol, never the older Sol");
            Assert(ChatGptAutomation.IsSolReasoningSelectorName("GPT-6 Sol High") &&
                !ChatGptAutomation.IsSolReasoningSelectorName("GPT-5.6 Sol High"), "Sol selector is version-scoped");
            Assert(ChatGptAutomation.ParseCompactSolPosition("GPT-6 Sol 深度，第 5 项，共 6 项。") == 4,
                "Sol Max live slider position is recognized");
            Assert(ChatGptAutomation.ParseCompactSolPosition("GPT-5.6 Sol 深度，第 5 项，共 6 项。") == -1,
                "old Sol slider is not mistaken for GPT-6 Sol");
            Assert(ChatGptAutomation.IsAdvancedReasoningToggleName("Show advanced options"), "current English compact reasoning menu opens advanced options");
            Assert(ChatGptAutomation.IsAdvancedReasoningToggleName("显示高级模式选项"), "current Chinese compact reasoning menu opens advanced options");
            Assert(!ChatGptAutomation.IsAdvancedReasoningToggleName("显示紧凑模式选项"), "advanced reasoning toggle does not reverse back to compact mode");
            Assert(ChatGptAutomation.IsSpeedRowName("Speed Standard"), "English speed row is accepted");
            Assert(ChatGptAutomation.IsSpeedRowName("速度 标准"), "Chinese speed row is accepted");
            Assert(ChatGptAutomation.IsFastSpeedName("Speed Fast"), "English fast speed is recognized");
            Assert(ChatGptAutomation.IsFastSpeedName("速度 快速"), "Chinese fast speed is recognized");
            Assert(ChatGptAutomation.IsStandardSpeedName("Standard"), "English standard speed option is recognized");
            Assert(ChatGptAutomation.IsStandardSpeedName("标准"), "Chinese standard speed option is recognized");
            Assert(ChatGptAutomation.ResolveSpeedTarget(false, true), "K1 Ultra press deterministically requests Fast from Standard");
            Assert(ChatGptAutomation.ResolveSpeedTarget(true, true), "K1 Ultra press keeps Fast selected instead of toggling back to Standard");
            Assert(!ChatGptAutomation.ResolveSpeedTarget(true, false), "K2 High press deterministically requests Standard from Fast");
            Assert(!ChatGptAutomation.ResolveSpeedTarget(false, false), "K2 High press keeps Standard selected instead of toggling to Fast");
            Assert(!ChatGptAutomation.ResolveSpeedTarget(true, null), "legacy speed toggle still resolves Fast to Standard");
            Assert(ChatGptAutomation.ResolveSpeedTarget(false, null), "legacy speed toggle still resolves Standard to Fast");
            Assert(ChatGptAutomation.IsStandardSpeedName("速度 普通"), "Chinese ordinary speed label is recognized as Standard");
            Assert(ChatGptAutomation.IsStandardSpeedName("普通"), "Chinese ordinary speed option is recognized as Standard");
            Assert(!ChatGptAutomation.IsStandardSpeedName("普通任务"), "ordinary task labels are not mistaken for Standard speed");
            Assert(ChatGptAutomation.IsCompactFastToggleName("启用快速模式"), "current Chinese compact Fast checkbox is accepted");
            Assert(ChatGptAutomation.IsCompactFastToggleName("启用标准模式"), "active Fast checkbox action label remains accepted for switching back to Standard");
            Assert(ChatGptAutomation.IsCompactFastToggleName("Enable fast mode"), "current English compact Fast checkbox is accepted");
            Assert(!ChatGptAutomation.IsCompactFastToggleName("快速聊天"), "Quick chat is not mistaken for the compact Fast checkbox");
            Assert(ChatGptAutomation.IsGlobalNewTaskButtonName("New chat"), "English global new-task button is accepted");
            Assert(ChatGptAutomation.IsGlobalNewTaskButtonName("新对话"), "current Chinese global new-task button is accepted");
            Assert(!ChatGptAutomation.IsGlobalNewTaskButtonName("Start new chat in CXT12E4"), "English project-scoped new-task button is rejected");
            Assert(!ChatGptAutomation.IsGlobalNewTaskButtonName("在 CXT12E4 中开始新聊天"), "Chinese project-scoped new-task button is rejected");
            Assert(ChatGptAutomation.IsTemporaryTaskButtonName("Quick chat"), "current English temporary-task button is accepted");
            Assert(ChatGptAutomation.IsTemporaryTaskButtonName("快速聊天"), "current Chinese temporary-task button is accepted");
            Assert(!ChatGptAutomation.IsTemporaryTaskButtonName("新对话"), "normal new-task button is not mistaken for Quick chat");
            var quickChatToggle = new QuickChatToggleState();
            Assert(!quickChatToggle.ConsumeCloseRequest(), "Quick chat starts in the safe closed state");
            quickChatToggle.MarkOpened();
            Assert(quickChatToggle.ConsumeCloseRequest(), "a successful Quick chat open arms exactly one Escape close");
            Assert(!quickChatToggle.ConsumeCloseRequest(), "the Quick chat close request is consumed and cannot send a second Escape");
            quickChatToggle.MarkOpened();
            quickChatToggle.MarkOpened();
            Assert(quickChatToggle.ConsumeCloseRequest(), "repeated successful open observations still arm only one Escape close");
            var recentSectionBounds = new System.Windows.Rect(24, 200, 480, 42);
            var recentNewTaskBounds = new System.Windows.Rect(554, 203, 36, 36);
            var topNewTaskBounds = new System.Windows.Rect(24, 40, 518, 36);
            Assert(ChatGptAutomation.IsTopLevelNewTaskButtonGeometry(recentNewTaskBounds, recentSectionBounds), "new-task icon beside Recent is accepted");
            Assert(!ChatGptAutomation.IsTopLevelNewTaskButtonGeometry(topNewTaskBounds, recentSectionBounds), "top generic new-task row is rejected while a project is active");
            Assert(ChatGptAutomation.IsForkButtonName("从这里创建聊天分支"), "current Chinese fork button is accepted");
            Assert(ChatGptAutomation.IsForkButtonName("Create chat branch from here"), "current English fork button is accepted");
            Assert(!ChatGptAutomation.IsForkButtonName("新对话"), "global new-task button is not mistaken for a fork button");
            Assert(ChatGptAutomation.IsBuiltInBrowserUrlInputName("搜索或输入网址"), "current Chinese built-in browser URL input is accepted");
            Assert(ChatGptAutomation.IsBuiltInBrowserUrlInputName("Search or enter URL"), "current English built-in browser URL input is accepted");
            Assert(ChatGptAutomation.IsBuiltInBrowserUrlInputName("Search or enter a URL"), "Codex 26.917 English browser URL input is accepted");
            Assert(ChatGptAutomation.IsBuiltInBrowserUrlInputName("输入 URL"), "legacy Chinese built-in browser URL input remains accepted");
            Assert(ChatGptAutomation.IsBuiltInBrowserUrlInputName("Input URL"), "legacy English built-in browser URL input remains accepted");
            Assert(!ChatGptAutomation.IsBuiltInBrowserUrlInputName("与 ChatGPT 聊天"), "ChatGPT composer is not mistaken for the browser URL input");
            string currentSelectedSidebarClass = "hover:bg-primary-ghost-hover data-[app-action-sidebar-thread-selected=true]:bg-primary-ghost-hover sidebar-item bg-primary-ghost-hover";
            string currentIdleSidebarClass = "hover:bg-primary-ghost-hover data-[app-action-sidebar-thread-selected=true]:bg-primary-ghost-hover sidebar-item";
            Assert(ChatGptAutomation.SidebarSelectionScore(currentSelectedSidebarClass) >= 3, "current selected ChatGPT sidebar class is recognized");
            Assert(ChatGptAutomation.SidebarSelectionScore(currentIdleSidebarClass) < 3, "current unselected ChatGPT sidebar class is rejected");
            Assert(ChatGptAutomation.IsSidebarToggleIdentity("隐藏侧边栏", string.Empty, false), "current Chinese hide-sidebar button is recognized");
            Assert(ChatGptAutomation.IsSidebarToggleIdentity("显示侧边栏", string.Empty, false), "current Chinese show-sidebar button is recognized");
            Assert(ChatGptAutomation.IsSidebarToggleIdentity("future locale", "button group/sidebar-trigger browser:size-9", true), "sidebar trigger class is an explicit fallback");
            Assert(!ChatGptAutomation.IsSidebarToggleIdentity("打开侧边面板标签页", "button", true), "right side-panel button is not mistaken for the left sidebar toggle");
            Assert(ActionEngine.ScrollMultiplierForStreak(1) == 1, "scroll starts precise");
            Assert(ActionEngine.ScrollMultiplierForStreak(3) == 2, "scroll acceleration level 2");
            Assert(ActionEngine.ScrollMultiplierForStreak(5) == 4, "scroll acceleration level 3");
            Assert(ActionEngine.ScrollMultiplierForStreak(8) == 7, "scroll acceleration level 4");
            Assert(ActionEngine.ScrollMultiplierForStreak(12) == 10, "scroll acceleration maximum");
            for (int acceleratedLayer = 0; acceleratedLayer <= 7; acceleratedLayer++)
            {
                Assert(ActionEngine.IsAcceleratedScrollEncoder(acceleratedLayer, 4), "physical K4 accelerates scrolling on internal layer " + acceleratedLayer);
                Assert(!ActionEngine.IsAcceleratedScrollEncoder(acceleratedLayer, 1), "K1 is not mistaken for the universal K4 scroll encoder on internal layer " + acceleratedLayer);
            }
            Assert(!ActionEngine.IsAcceleratedScrollEncoder(-1, 4) && !ActionEngine.IsAcceleratedScrollEncoder(8, 4), "accelerated K4 mapping rejects invalid layers");
            Assert(ActionEngine.StandardWheelEventCount(120) == 1, "terminal precise scroll emits one standard wheel event");
            Assert(ActionEngine.StandardWheelEventCount(-480) == 4, "terminal accelerated scroll expands negative delta into repeated wheel events");
            Assert(ActionEngine.StandardWheelEventCount(1200) == 10, "terminal maximum acceleration emits ten standard wheel events");
            Assert(ActionEngine.TerminalTmuxActionForKey(1) == TerminalTmuxAction.SplitRight, "L2 key1 uses direct tmux split-right");
            Assert(ActionEngine.TerminalTmuxActionForKey(5) == TerminalTmuxAction.ToggleZoom, "L2 key5 uses direct tmux zoom instead of an IME-sensitive Z key");
            Assert(ActionEngine.TerminalTmuxActionForKey(9) == TerminalTmuxAction.StashAgentInput, "L2 key9 uses a pane-targeted agent action");
            Assert(ActionEngine.TerminalTmuxActionForKey(11) == TerminalTmuxAction.None, "L2 key11 remains the Windows Terminal search shortcut");
            Assert(ActionEngine.TerminalTmuxActionForKey(12) == TerminalTmuxAction.LastPane, "L2 key12 uses direct tmux last-pane");
            TerminalTmuxAction[] expectedTerminalActions =
            {
                TerminalTmuxAction.None,
                TerminalTmuxAction.SplitRight,
                TerminalTmuxAction.SplitDown,
                TerminalTmuxAction.SelectHorizontal,
                TerminalTmuxAction.SelectVertical,
                TerminalTmuxAction.ToggleZoom,
                TerminalTmuxAction.ToggleOrientation,
                TerminalTmuxAction.RotatePanes,
                TerminalTmuxAction.ChooseWindow,
                TerminalTmuxAction.StashAgentInput,
                TerminalTmuxAction.CopyMode,
                TerminalTmuxAction.None,
                TerminalTmuxAction.LastPane
            };
            for (int terminalKey = 1; terminalKey <= 12; terminalKey++)
                Assert(ActionEngine.TerminalTmuxActionForKey(terminalKey) == expectedTerminalActions[terminalKey], "L2 direct tmux mapping for key " + terminalKey);
            TerminalClientTarget activeClientTarget = TmuxSessionClient.ResolveMostRecentClientTarget(new[]
            {
                "malformed",
                "100\t/dev/pts/1\t$1\t%2\t@1",
                "250\t/dev/pts/2\t$5\t%7\t@4"
            });
            Assert(activeClientTarget != null && activeClientTarget.ClientName == "/dev/pts/2" && activeClientTarget.SessionId == "$5" && activeClientTarget.PaneId == "%7" && activeClientTarget.WindowId == "@4", "L2 direct tmux action targets the most recently active attached client session, window, and pane");
            Assert(TmuxSessionClient.ResolveMostRecentClientTarget(new[] { "250\t/dev/pts/2\t$5\tbad-pane\t@4" }) == null, "L2 direct tmux action rejects malformed target identities");
            Assert(TmuxSessionClient.ResolveMostRecentClientTarget(new[] { "250\t/dev/pts/2\t$5\t%7;display-message bad\t@4" }) == null, "L2 direct tmux action rejects command text disguised as a pane ID");
            IList<string> zoomArguments = TmuxSessionClient.BuildActiveActionArguments(TerminalTmuxAction.ToggleZoom, activeClientTarget);
            Assert(zoomArguments.Count == 4 && zoomArguments[0] == "resize-pane" && zoomArguments[1] == "-Z" && zoomArguments[3] == "%7", "L2 key5 emits pane-targeted resize-pane -Z");
            IList<string> orientationArguments = TmuxSessionClient.BuildToggleOrientationArguments(activeClientTarget, new[] { "0\t0", "0\t80" });
            Assert(orientationArguments.Count == 4 && orientationArguments[0] == "select-layout" && orientationArguments[2] == "%7" && orientationArguments[3] == "even-vertical", "L2 key6 changes side-by-side panes to an exact targeted vertical layout");
            orientationArguments = TmuxSessionClient.BuildToggleOrientationArguments(activeClientTarget, new[] { "0\t0", "25\t0" });
            Assert(orientationArguments[3] == "even-horizontal", "L2 key6 changes stacked panes to an exact targeted horizontal layout");
            orientationArguments = TmuxSessionClient.BuildToggleOrientationArguments(activeClientTarget, new[] { "0\t0", "0\t80", "25\t80" });
            Assert(orientationArguments[3] == "even-horizontal", "L2 key6 normalizes a mixed three-pane layout to an exact targeted horizontal row");
            orientationArguments = TmuxSessionClient.BuildToggleOrientationArguments(activeClientTarget, new[] { "0\t0", "0\t40", "0\t80" });
            Assert(orientationArguments[3] == "even-vertical", "L2 key6 changes a three-pane horizontal row to an exact targeted vertical column");
            Assert(TmuxSessionClient.BuildToggleOrientationArguments(activeClientTarget, new[] { "0\t0" }) == null, "L2 key6 refuses a window with fewer than two panes");
            IList<string> rotateArguments = TmuxSessionClient.BuildActiveActionArguments(TerminalTmuxAction.RotatePanes, activeClientTarget);
            Assert(rotateArguments.Count == 3 && rotateArguments[2] == "@4", "L2 key7 targets the active client's exact window");
            Assert(ActionEngine.TerminalTmuxActionForEncoder(1, EncoderGesture.CounterClockwise) == TerminalTmuxAction.ResizeLeft, "L2 K1 counterclockwise directly resizes the active pane left");
            Assert(ActionEngine.TerminalTmuxActionForEncoder(1, EncoderGesture.Press) == TerminalTmuxAction.EqualizeHorizontal, "L2 K1 press directly equalizes pane widths");
            Assert(ActionEngine.TerminalTmuxActionForEncoder(2, EncoderGesture.Clockwise) == TerminalTmuxAction.ResizeDown, "L2 K2 clockwise directly resizes the active pane down");
            Assert(ActionEngine.TerminalTmuxActionForEncoder(2, EncoderGesture.Press) == TerminalTmuxAction.EqualizeVertical, "L2 K2 press directly equalizes pane heights");
            Assert(ActionEngine.TerminalTmuxActionForEncoder(3, EncoderGesture.CounterClockwise) == TerminalTmuxAction.PreviousWindow, "L2 K3 counterclockwise directly selects the previous window");
            Assert(ActionEngine.TerminalTmuxActionForEncoder(3, EncoderGesture.Clockwise) == TerminalTmuxAction.NextWindow, "L2 K3 clockwise directly selects the next window");
            Assert(ActionEngine.TerminalTmuxActionForEncoder(3, EncoderGesture.Press) == TerminalTmuxAction.LastWindow, "L2 K3 press directly returns to the last window");
            Assert(ActionEngine.TerminalTmuxActionForEncoder(4, EncoderGesture.Press) == TerminalTmuxAction.None, "L2 K4 remains the Windows Terminal scroll control");
            IList<string> resizeArguments = TmuxSessionClient.BuildActiveActionArguments(TerminalTmuxAction.ResizeLeft, activeClientTarget);
            Assert(resizeArguments.Count == 5 && resizeArguments[0] == "resize-pane" && resizeArguments[2] == "-t" && resizeArguments[3] == "%7" && resizeArguments[4] == "5", "L2 pane resize keeps tmux 3.6 positional adjustment after all options");
            Assert(TaskbarLauncher.ShortcutVirtualKey(1) == Vk.One, "taskbar slot 1 uses Win+1");
            Assert(TaskbarLauncher.ShortcutVirtualKey(9) == Vk.Nine, "taskbar slot 9 uses Win+9");
            Assert(TaskbarLauncher.ShortcutVirtualKey(10) == Vk.Zero, "taskbar slot 10 uses Win+0");
            Assert(TaskbarLauncher.ShortcutVirtualKey(11) == 0, "taskbar slot 11 has no native Win+number shortcut");
            Assert(TaskbarLauncher.NavigationRightMoves(11) == 10, "taskbar slot 11 navigates ten positions from Home");
            Assert(TriggerProcessor.GlobalTaskHoldMs == 650, "global task hold protects short layer actions");
            Assert(TriggerProcessor.IsTerminalSessionKey(9) && ActionEngine.TerminalSessionSlotForKey(9) == 1, "global key9 holds tmux session slot 1");
            Assert(TriggerProcessor.IsTerminalSessionKey(10) && ActionEngine.TerminalSessionSlotForKey(10) == 2, "global key10 holds tmux session slot 2");
            Assert(TriggerProcessor.IsTerminalSessionKey(11) && ActionEngine.TerminalSessionSlotForKey(11) == 3, "global key11 holds tmux session slot 3");
            Assert(TriggerProcessor.IsTerminalSessionKey(12) && ActionEngine.TerminalSessionSlotForKey(12) == 4, "global key12 holds tmux session slot 4");
            Assert(TriggerProcessor.IsTerminalSessionKey(8) && ActionEngine.TerminalSessionSlotForKey(8) == 5, "global key8 holds tmux session slot 5");
            Assert(TriggerProcessor.IsTerminalSessionKey(4) && ActionEngine.TerminalSessionSlotForKey(4) == 6, "global key4 holds tmux session slot 6");
            Assert(TriggerProcessor.EncoderMinimumInterval(1, 1) == 650, "ChatGPT reasoning debounce follows K1");
            Assert(TriggerProcessor.EncoderMinimumInterval(1, 2) == 300, "ChatGPT adjacent task navigation debounce follows K2");
            Assert(TriggerProcessor.EncoderMinimumInterval(1, 4) == 35, "ChatGPT scroll remains responsive");
            Assert(ActionEngine.IsForegroundContextCurrent(new IntPtr(7), 3, new IntPtr(7), 3), "queued action keeps matching foreground context");
            Assert(!ActionEngine.IsForegroundContextCurrent(new IntPtr(7), 3, new IntPtr(8), 3), "queued action rejects changed foreground");
            Assert(!ActionEngine.IsForegroundContextCurrent(new IntPtr(7), 3, new IntPtr(7), 5), "queued action rejects ABA context generation");
            var dispatchStarted = new ManualResetEvent(false);
            var releaseDispatch = new ManualResetEvent(false);
            var dispatcherUnderTest = new ActionDispatcher();
            int discardedActionCount = 0;
            dispatcherUnderTest.Enqueue(delegate
            {
                dispatchStarted.Set();
                releaseDispatch.WaitOne();
            });
            Assert(dispatchStarted.WaitOne(1500), "dispatcher begins the in-flight action used by the dispose test");
            dispatcherUnderTest.Enqueue(delegate { Interlocked.Increment(ref discardedActionCount); });
            var disposeThread = new Thread(dispatcherUnderTest.Dispose);
            disposeThread.Start();
            Assert(SpinWait.SpinUntil(delegate { return dispatcherUnderTest.IsDisposalPending; }, 1000), "dispatcher publishes disposal intent before waiting for an in-flight action");
            releaseDispatch.Set();
            Assert(disposeThread.Join(1500), "dispatcher dispose completes after its already-running action returns");
            Assert(dispatcherUnderTest.IsDisposed, "dispatcher publishes final disposal inside the action lifecycle gate");
            Assert(discardedActionCount == 0, "dispatcher dispose discards queued actions instead of executing them during shutdown");
            dispatchStarted.Dispose();
            releaseDispatch.Dispose();
            var dequeuedBeforeStart = new ManualResetEvent(false);
            var releaseDequeuedAction = new ManualResetEvent(false);
            int actionStartedAfterDispose = 0;
            var gapDispatcher = new ActionDispatcher(delegate
            {
                dequeuedBeforeStart.Set();
                releaseDequeuedAction.WaitOne();
            });
            gapDispatcher.Enqueue(delegate { Interlocked.Increment(ref actionStartedAfterDispose); });
            Assert(dequeuedBeforeStart.WaitOne(1500), "dispatcher test pauses a dequeued action before its execution linearization point");
            var gapDisposeThread = new Thread(gapDispatcher.Dispose);
            gapDisposeThread.Start();
            Assert(SpinWait.SpinUntil(delegate { return gapDispatcher.IsDisposed; }, 1000), "dispatcher publishes disposal while a dequeued action is paused before execution");
            releaseDequeuedAction.Set();
            Assert(gapDisposeThread.Join(1500), "dispatcher closes the dequeued-before-start disposal gap");
            Assert(actionStartedAfterDispose == 0, "a dequeued action cannot start after disposal was requested");
            dequeuedBeforeStart.Dispose();
            releaseDequeuedAction.Dispose();
            RunTriggerProcessorSelfTests();
            for (int layer = 1; layer < 8; layer++)
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 6; column++)
                        Assert(map[layer, row, column] == map[0, row, column], "identical hardware layer keymaps");
                }
            }

            Assert(ForegroundResolver.ResolveLayer("ChatGPT", string.Empty) == 1, "ChatGPT auto layer");
            Assert(ForegroundResolver.ResolveLayer("WindowsTerminal", string.Empty) == 2, "Terminal auto layer");
            Assert(ForegroundResolver.ResolveLayer("Notion", string.Empty) == 3, "Notion auto layer");
            Assert(ForegroundResolver.ResolveLayer("Obsidian", string.Empty) == 4, "Obsidian auto layer");
            Assert(ForegroundResolver.ResolveLayer("Weixin", string.Empty) == 7, "Messaging auto layer");
            Assert(ForegroundResolver.ResolveLayer("WeChatAppEx", string.Empty) == 7, "WeChat child-window auto layer");
            DesktopLayerShortcutTests.Run(Assert);
            var profileForeground = new ForegroundInfo { Window = new IntPtr(200), ProcessName = "blender" };
            Assert(ActionEngine.IsDesktopShortcutContextCurrent(5, new IntPtr(200), 10, profileForeground, new IntPtr(200), 10), "L5 allows the original Blender context");
            Assert(!ActionEngine.IsDesktopShortcutContextCurrent(5, new IntPtr(200), 10, profileForeground, new IntPtr(201), 10), "post-marker guard rejects an immediate HWND change");
            Assert(!ActionEngine.IsDesktopShortcutContextCurrent(5, new IntPtr(200), 10, profileForeground, new IntPtr(200), 12), "post-marker guard rejects A-B-A context changes");
            Assert(!ActionEngine.IsDesktopShortcutContextCurrent(0, new IntPtr(201), 10, profileForeground, new IntPtr(200), 10), "L8 does not adopt a different captured HWND");
            profileForeground.ProcessName = "chrome";
            Assert(!ActionEngine.IsDesktopShortcutContextCurrent(5, new IntPtr(200), 10, profileForeground, new IntPtr(200), 10), "manual L5 rejects browser foreground");
            Assert(ActionEngine.IsDesktopShortcutContextCurrent(0, new IntPtr(200), 10, profileForeground, new IntPtr(200), 10), "L8 permits browser foreground");
            profileForeground.ProcessName = "notepad";
            Assert(ActionEngine.IsDesktopShortcutContextCurrent(0, new IntPtr(200), 10, profileForeground, new IntPtr(200), 10), "same L8 bindings permit unmatched foreground");
            Assert(!ActionEngine.IsDesktopShortcutContextCurrent(0, IntPtr.Zero, 10, null, IntPtr.Zero, 10), "missing foreground fails closed");
            foreach (string browser in new[] { "msedge", "chrome", "vivaldi", "brave", "arc", "browser pro+", "browserpro", "o-browser", "obrowser", "flashbrowser", "phoenixsearch", "firefox" })
                Assert(ForegroundResolver.ResolveLayer(browser, "Blender tutorial") == 0, "Browser uses the single L8 profile: " + browser);
            Assert(ForegroundResolver.ResolveLayer("ApplicationFrameHost", "Flash Browser") == 0, "hosted browser also uses L8");
            Assert(ForegroundResolver.ResolveLayer("BlEnDeR", "model.blend") == 5, "exact Blender process uses L5");
            Assert(ForegroundResolver.ResolveLayer("notepad", "Blender") == 0, "Blender title cannot activate modeling shortcuts");
            Assert(ForegroundResolver.ResolveLayer("blender-launcher", "Blender") == 0, "Blender launcher is not its editing window");
            Assert(ForegroundResolver.ResolveLayer("maya", "3D modeling") == 0, "unadapted 3D apps do not receive Blender bindings");
            Assert(!ForegroundResolver.IsBlenderProcess(null) && !ForegroundResolver.IsBlenderProcess("chrome"), "Blender guard rejects missing or unrelated processes");
            Assert(BridgeDefinitions.GetLayer(0).Name == "通用 / 浏览器" && BridgeDefinitions.GetLayer(5).Name == "3D建模 / Blender", "L8 and L5 names follow fixed profile routing");
            Assert(ForegroundResolver.ResolveLayer("TOTALCMD64", string.Empty) == 6, "Total Commander auto layer");
            Assert(ForegroundResolver.ResolveLayer("notepad", string.Empty) == 0, "fallback auto layer");

            var manager = new LayerManager();
            manager.ObserveForeground(new ForegroundInfo { Window = new IntPtr(1), AutomaticLayer = 1, ProcessName = "ChatGPT" });
            Assert(manager.CurrentLayer == 1 && !manager.IsManual, "automatic layer manager state");
            long observedGeneration = manager.ContextGeneration;
            manager.ObserveForeground(new ForegroundInfo { Window = new IntPtr(1), AutomaticLayer = 1, ProcessName = "ChatGPT" });
            Assert(manager.ContextGeneration == observedGeneration, "identical foreground observation keeps context generation");
            manager.SetManualLayer(7);
            Assert(manager.ContextGeneration == observedGeneration + 1, "manual layer change increments context generation");
            manager.ObserveForeground(new ForegroundInfo { Window = new IntPtr(1), AutomaticLayer = 1, ProcessName = "ChatGPT" });
            Assert(manager.CurrentLayer == 7 && manager.IsManual, "manual override survives same foreground HWND");
            manager.ObserveForeground(new ForegroundInfo { Window = new IntPtr(2), AutomaticLayer = 3, ProcessName = "Notion" });
            Assert(manager.CurrentLayer == 3 && !manager.IsManual, "manual override clears on foreground HWND change");

            for (int layer = 1; layer <= 7; layer++)
            {
                LayerDefinition definition = BridgeDefinitions.GetLayer(layer);
                byte hue;
                byte saturation;
                BridgeDefinitions.RgbToQmkHsv(definition.Red, definition.Green, definition.Blue, out hue, out saturation);
                Assert(layer == 1 || saturation > 20, "layer color saturation L" + layer);
            }
            int[] expectedIndicatorKeys = { 8, 1, 2, 3, 4, 5, 6, 7 };
            int[] expectedDisplayNumbers = { 8, 1, 2, 3, 4, 5, 6, 7 };
            for (int layer = 0; layer <= 7; layer++)
            {
                Assert(BridgeDefinitions.GetLayer(layer).IndicatorKey == expectedIndicatorKeys[layer], "layer indicator key L" + layer);
                Assert(BridgeDefinitions.GetLayer(layer).Number == expectedDisplayNumbers[layer], "user-facing layer number for internal layer " + layer);
            }
            Assert(BridgeDefinitions.ToInternalLayer(8) == 0, "user-facing L8 maps to the stable internal layer-zero index");
            Assert(BridgeDefinitions.ToInternalLayer(1) == 1 && BridgeDefinitions.ToInternalLayer(7) == 7, "user-facing L1-L7 keep their internal indices");
            LayerDefinition layer0Definition = BridgeDefinitions.GetLayer(0);
            using (Bitmap layer0Icon = BridgeApplicationContext.CreateLayerIconBitmap(layer0Definition, 8))
            {
                Assert(layer0Icon.Width == 32 && layer0Icon.Height == 32, "tray layer icon uses the full 32x32 canvas");
                Color expectedBackground = Color.FromArgb(layer0Definition.Red, layer0Definition.Green, layer0Definition.Blue);
                Assert(layer0Icon.GetPixel(0, 0).ToArgb() == expectedBackground.ToArgb(), "tray layer icon color reaches the outermost corner");
                int foregroundPixels = 0;
                for (int y = 0; y < layer0Icon.Height; y++)
                {
                    for (int x = 0; x < layer0Icon.Width; x++)
                    {
                        if (layer0Icon.GetPixel(x, y).ToArgb() != expectedBackground.ToArgb())
                            foregroundPixels++;
                    }
                }
                Assert(foregroundPixels >= 100, "tray layer number occupies a prominent area");
            }
            Assert(BridgeApplicationContext.GetLayerIconTextColor(Color.FromArgb(0xC0, 0x00, 0xC8)).ToArgb() == Color.White.ToArgb(), "dark layer color uses white tray text");
            Assert(BridgeApplicationContext.GetLayerIconTextColor(Color.FromArgb(0xFF, 0xDC, 0x00)).ToArgb() == Color.Black.ToArgb(), "bright layer color uses black tray text");
            Assert(TerminalSixSessionStatusMonitor.ParseSessionSlot("01-Chat") == 1, "tmux session 01 maps to slot 1");
            Assert(TerminalSixSessionStatusMonitor.ParseSessionSlot("06-CC") == 6, "tmux session 06 maps to slot 6");
            Assert(TerminalSixSessionStatusMonitor.ParseSessionSlot("07-openwrite") == 7, "tmux session 07 stays outside the six LEDs");
            Assert(TerminalSixSessionStatusMonitor.ParseSessionSlot("1-legacy") == 0, "single-digit tmux names do not impersonate the 01 namespace");
            Assert(TerminalSixSessionStatusMonitor.ParseSessionSlot(" 01-leading-space") == 0, "tmux slot prefix must begin at the first character");
            Assert(TerminalSixSessionStatusMonitor.ParseSessionSlot("misc") == 0, "unnumbered tmux session has no LED slot");
            string terminalTestRoot = Path.Combine(
                Path.GetTempPath(),
                "cxt12e4-terminal-status-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(terminalTestRoot);
            try
            {
                File.WriteAllText(Path.Combine(terminalTestRoot, "pane-1.state"), "01-Chat\t%1\tcompleted\t1\t$1\t1\t624\n");
                File.WriteAllText(Path.Combine(terminalTestRoot, "pane-2.state"), "01-Chat\t%2\twaiting\t1\n");
                File.WriteAllText(Path.Combine(terminalTestRoot, "pane-3.state"), "02-Study\t%3\tthinking\t1\n");
                IList<TerminalSessionStatusSlot> terminalSlots = TerminalSixSessionStatusMonitor.ReadSlots(terminalTestRoot);
                Assert(terminalSlots[0].Present && terminalSlots[0].State == AiTaskState.WaitingForInput, "tmux multi-pane priority");
                Assert(terminalSlots[1].Present && terminalSlots[1].State == AiTaskState.Thinking, "tmux running state");
                File.WriteAllText(Path.Combine(terminalTestRoot, "pane-4.state"), "03-Old\t%4\terror\t1\n");
                File.WriteAllText(Path.Combine(terminalTestRoot, "pane-5.state"), "06-Old\t%5\terror\t1\n");
                var liveTerminalPanes = new List<TerminalPaneSnapshot>
                {
                    new TerminalPaneSnapshot { SessionId = "$1", SessionCreatedUnix = 1, ServerPid = 624, SessionName = "01-Chat", PaneId = "%1" },
                    new TerminalPaneSnapshot { SessionId = "$2", SessionCreatedUnix = 1, ServerPid = 624, SessionName = "02-Study", PaneId = "%3" },
                    new TerminalPaneSnapshot { SessionId = "$4", SessionCreatedUnix = 1, ServerPid = 624, SessionName = "04-CC", PaneId = "%4" },
                    new TerminalPaneSnapshot { SessionId = "$5", SessionCreatedUnix = 1, ServerPid = 624, SessionName = "05-grok", PaneId = "%5" },
                    new TerminalPaneSnapshot { SessionId = "$6", SessionCreatedUnix = 1, ServerPid = 624, SessionName = "06-search", PaneId = "%8" }
                };
                terminalSlots = TerminalSixSessionStatusMonitor.ReadSlots(terminalTestRoot, liveTerminalPanes);
                Assert(terminalSlots[0].Present && terminalSlots[0].State == AiTaskState.Completed, "live tmux inventory accepts a matching current pane state");
                Assert(terminalSlots[1].Present && terminalSlots[1].State == AiTaskState.Thinking, "live tmux inventory keeps another matching pane state");
                Assert(!terminalSlots[2].Present, "stale pane state cannot resurrect an absent session");
                Assert(terminalSlots[3].Present && terminalSlots[3].State == AiTaskState.Idle && terminalSlots[3].SessionName == "04-CC", "pane ID reuse cannot assign an old session state to slot 4");
                Assert(terminalSlots[4].Present && terminalSlots[4].State == AiTaskState.Idle && terminalSlots[4].SessionName == "05-grok", "a live session without an agent report remains present and idle");
                Assert(terminalSlots[5].Present && terminalSlots[5].State == AiTaskState.Idle && terminalSlots[5].SessionName == "06-search", "slot 6 follows live tmux inventory rather than a stale pane file");
                var renamedTerminalPanes = new List<TerminalPaneSnapshot>
                {
                    new TerminalPaneSnapshot { SessionId = "$1", SessionCreatedUnix = 1, ServerPid = 624, SessionName = "04-Renamed", PaneId = "%1" }
                };
                terminalSlots = TerminalSixSessionStatusMonitor.ReadSlots(terminalTestRoot, renamedTerminalPanes);
                Assert(terminalSlots[3].Present && terminalSlots[3].State == AiTaskState.Completed && terminalSlots[3].SessionName == "04-Renamed", "seven-field session identity keeps active state across a live session rename and maps it to the new slot");
                var renamedTarget = new TerminalSessionTarget
                {
                    Slot = 4,
                    SessionId = "$1",
                    SessionIdentity = "$1@1:624",
                    SessionCreatedUnix = 1,
                    ServerPid = 624,
                    SessionName = "04-Renamed"
                };
                AiTaskState renamedTargetState;
                long renamedTargetRevision;
                Assert(TerminalSixSessionStatusMonitor.TryReadExactTargetState(terminalTestRoot, renamedTarget, out renamedTargetState, out renamedTargetRevision) && renamedTargetState == AiTaskState.Completed && renamedTargetRevision == 1000000000L, "exact identity acknowledgement survives a session rename and preserves report revision");
                var restartedServerPanes = new List<TerminalPaneSnapshot>
                {
                    new TerminalPaneSnapshot { SessionId = "$1", SessionCreatedUnix = 2, ServerPid = 999, SessionName = "01-Chat", PaneId = "%1" }
                };
                terminalSlots = TerminalSixSessionStatusMonitor.ReadSlots(terminalTestRoot, restartedServerPanes);
                Assert(terminalSlots[0].Present && terminalSlots[0].State == AiTaskState.Idle, "tmux server restart cannot reuse an old exact-identity pane report");
                liveTerminalPanes.Add(new TerminalPaneSnapshot { SessionId = "$9", SessionCreatedUnix = 1, ServerPid = 624, SessionName = "04-Other", PaneId = "%9" });
                terminalSlots = TerminalSixSessionStatusMonitor.ReadSlots(terminalTestRoot, liveTerminalPanes);
                Assert(!terminalSlots[3].Present && string.IsNullOrEmpty(terminalSlots[3].SessionId), "duplicate tmux slot prefixes fail closed instead of mixing sessions");
                terminalSlots = TerminalSixSessionStatusMonitor.ReadSlots(terminalTestRoot, new List<TerminalPaneSnapshot>());
                Assert(!terminalSlots[0].Present && !terminalSlots[5].Present, "empty or unavailable initial live inventory fails closed instead of trusting stale files");

                DateTime inventorySuccessUtc = new DateTime(2026, 8, 31, 1, 0, 0, DateTimeKind.Utc);
                Assert(
                    TerminalSixSessionStatusMonitor.ShouldRetainLiveInventoryAfterFailure(
                        new TimeoutException("slow WSL startup"),
                        true),
                    "tmux inventory timeout retains the last verified live inventory without an arbitrary expiry");
                Assert(
                    TerminalSixSessionStatusMonitor.ShouldRetainLiveInventoryAfterFailure(
                        new InvalidOperationException("WSL host transport failed"),
                        true),
                    "unknown WSL transport failure retains the last verified live inventory");
                Assert(
                    !TerminalSixSessionStatusMonitor.ShouldRetainLiveInventoryAfterFailure(
                        new TmuxServerUnavailableException("no server running"),
                        true),
                    "explicit tmux no-server result clears the verified live inventory");
                Assert(
                    !TerminalSixSessionStatusMonitor.ShouldRetainLiveInventoryAfterFailure(
                        new TimeoutException("initial timeout"),
                        false),
                    "initial tmux inventory failure remains fail-closed without a verified snapshot");
                int[] expectedRetrySeconds = { 2, 4, 8, 16, 32, 60, 60 };
                for (int i = 0; i < expectedRetrySeconds.Length; i++)
                {
                    Assert(
                        TerminalSixSessionStatusMonitor.GetInventoryRetryDelay(i + 1) == TimeSpan.FromSeconds(expectedRetrySeconds[i]),
                        "tmux inventory retry backoff step " + (i + 1).ToString(CultureInfo.InvariantCulture));
                }
                Assert(TmuxSessionClient.IsExplicitNoServerError("no server running on /tmp/tmux-1000/default"), "standard tmux no-server error is explicit");
                Assert(TmuxSessionClient.IsExplicitNoServerError("failed to connect to server: No such file or directory"), "missing tmux server endpoint is explicit");
                Assert(!TmuxSessionClient.IsExplicitNoServerError("failed to connect to server: Access is denied"), "unknown tmux connection error does not clear lights");
                Assert(!TmuxSessionClient.IsExplicitNoServerError("WSL host transport failed"), "WSL host failure is not mistaken for a stopped tmux server");

                IList<TerminalPaneSnapshot> strictInventory = TmuxSessionClient.ParseStrictPanes(
                    "$1\t1\t624\t01-Chat\t%1\n$2\t2\t624\t02-Study\t%2\n");
                Assert(
                    strictInventory.Count == 2 &&
                    strictInventory[0].SessionIdentity == "$1@1:624" &&
                    strictInventory[1].PaneId == "%2",
                    "strict tmux inventory parser preserves each exact live pane identity");
                bool rejectedEmptyInventory = false;
                try { TmuxSessionClient.ParseStrictPanes(string.Empty); }
                catch (InvalidDataException) { rejectedEmptyInventory = true; }
                Assert(rejectedEmptyInventory, "empty successful tmux output is treated as an invalid transport result");
                bool rejectedPartialInventory = false;
                try
                {
                    TmuxSessionClient.ParseStrictPanes("$1\t1\t624\t01-Chat\t%1\nmalformed\n");
                }
                catch (InvalidDataException) { rejectedPartialInventory = true; }
                Assert(rejectedPartialInventory, "partially malformed tmux output cannot clear or truncate session lights");
                bool rejectedInvalidIdentityInventory = false;
                try
                {
                    TmuxSessionClient.ParseStrictPanes("session\t0\t-1\t01-Chat\tpane\n");
                }
                catch (InvalidDataException) { rejectedInvalidIdentityInventory = true; }
                Assert(rejectedInvalidIdentityInventory, "tmux inventory requires valid IDs, creation time, server PID, and pane ID");
                bool rejectedDuplicatePaneInventory = false;
                try
                {
                    TmuxSessionClient.ParseStrictPanes("$1\t1\t624\t01-Chat\t%1\n$2\t2\t624\t02-Study\t%1\n");
                }
                catch (InvalidDataException) { rejectedDuplicatePaneInventory = true; }
                Assert(rejectedDuplicatePaneInventory, "tmux inventory rejects duplicate pane IDs instead of overwriting live identity");
                bool rejectedExtraFieldInventory = false;
                try
                {
                    TmuxSessionClient.ParseStrictPanes("$1\t1\t624\t01-Chat\t%1\textra\n");
                }
                catch (InvalidDataException) { rejectedExtraFieldInventory = true; }
                Assert(rejectedExtraFieldInventory, "tmux inventory rejects unexpected fields instead of accepting a changed protocol silently");

                string inventoryCachePath = Path.Combine(terminalTestRoot, "live-inventory-v1.cache");
                ulong inventoryCacheUptime = 3600000UL;
                TmuxInventoryCache.Write(
                    inventoryCachePath,
                    liveTerminalPanes,
                    inventorySuccessUtc,
                    inventoryCacheUptime);
                IList<TerminalPaneSnapshot> cachedTerminalPanes;
                Assert(
                    TmuxInventoryCache.TryRead(
                        inventoryCachePath,
                        inventorySuccessUtc.AddSeconds(5),
                        inventoryCacheUptime + 5000UL,
                        out cachedTerminalPanes) &&
                    cachedTerminalPanes.Count == liveTerminalPanes.Count &&
                    cachedTerminalPanes[0].SessionIdentity == "$1@1:624",
                    "same-boot tmux inventory cache round-trips exact session generation identities");
                Assert(
                    !TmuxInventoryCache.TryRead(
                        inventoryCachePath,
                        inventorySuccessUtc.AddHours(1).AddSeconds(1),
                        inventoryCacheUptime + 3601000UL,
                        out cachedTerminalPanes),
                    "tmux inventory cache expires after one hour across Bridge restarts");
                Assert(
                    !TmuxInventoryCache.TryRead(
                        inventoryCachePath,
                        inventorySuccessUtc.AddSeconds(5),
                        inventoryCacheUptime - 1UL,
                        out cachedTerminalPanes),
                    "tmux inventory cache rejects a Windows uptime rollback");
                Assert(
                    !TmuxInventoryCache.TryRead(
                        inventoryCachePath,
                        inventorySuccessUtc.AddMinutes(1),
                        inventoryCacheUptime + 5000UL,
                        out cachedTerminalPanes),
                    "tmux inventory cache rejects wall-clock and uptime disagreement");
                File.WriteAllText(inventoryCachePath, "broken-cache\n", new UTF8Encoding(false));
                Assert(
                    !TmuxInventoryCache.TryRead(
                        inventoryCachePath,
                        inventorySuccessUtc,
                        inventoryCacheUptime,
                        out cachedTerminalPanes),
                    "truncated tmux inventory cache fails closed");
                Assert(
                    TmuxInventoryCache.Delete(inventoryCachePath) && !File.Exists(inventoryCachePath),
                    "tmux inventory cache clear removes stale presence data");
            }
            finally
            {
                if (Directory.Exists(terminalTestRoot))
                    Directory.Delete(terminalTestRoot, true);
            }
            Assert(TmuxSessionClient.ResolveSessionName(4, new[] { "01-Chat", "04-CC", "05-grok" }) == "04-CC", "tmux hold resolves the exact live session name by numbered slot");
            Assert(TmuxSessionClient.ResolveSessionName(3, new[] { "01-Chat", "04-CC" }) == string.Empty, "tmux hold fails closed when its illuminated slot is absent");
            Assert(TmuxSessionClient.ResolveSessionName(4, new[] { "04-A", "04-B" }) == string.Empty, "tmux hold fails closed on duplicate numbered sessions");
            TerminalSessionTarget resolvedTarget = TmuxSessionClient.ResolveSessionTarget(4, new[] { "$4\t1000\t624\t04-CC", "$5\t1001\t624\t05-grok" });
            Assert(resolvedTarget != null && resolvedTarget.SessionId == "$4" && resolvedTarget.SessionIdentity == "$4@1000:624" && resolvedTarget.SessionName == "04-CC", "tmux hold resolves a unique server/session generation identity");
            Assert(TmuxSessionClient.ResolveSessionTarget(4, new[] { "$4\t1000\t624\t04-A", "$9\t1001\t624\t04-B" }) == null, "tmux identity resolution fails closed on duplicate slot prefix");
            Assert(TmuxSessionClient.SelectMostRecentClient(new[] { "100\t/dev/pts/1", "250\t/dev/pts/2" }) == "/dev/pts/2", "tmux hold selects the most recently active attached client");
            IList<TerminalPaneSnapshot> parsedPanes = TmuxSessionClient.ParsePanes(
                "$1\t1000\t624\t01-Chat\t%1\n$5\t1001\t624\t05-grok\t%7\nmalformed\n");
            Assert(parsedPanes.Count == 2, "tmux inventory parser rejects malformed rows");
            Assert(parsedPanes[1].SessionId == "$5" && parsedPanes[1].SessionIdentity == "$5@1001:624", "tmux inventory parser captures server/session generation identity");
            var terminalCompletionLatch = new TerminalCompletionLatch();
            var terminalLatchSlots = new List<TerminalSessionStatusSlot>
            {
                new TerminalSessionStatusSlot
                {
                    Slot = 1,
                    SessionId = "$1",
                    SessionName = "01-Chat",
                    Present = true,
                    State = AiTaskState.Completed
                }
            };
            terminalCompletionLatch.Apply(terminalLatchSlots);
            Assert(terminalLatchSlots[0].State == AiTaskState.Idle, "historical tmux completion starts acknowledged after Bridge restart");
            terminalLatchSlots[0].State = AiTaskState.Thinking;
            terminalCompletionLatch.Apply(terminalLatchSlots);
            terminalLatchSlots[0].State = AiTaskState.Completed;
            terminalCompletionLatch.Apply(terminalLatchSlots);
            Assert(terminalLatchSlots[0].State == AiTaskState.Completed, "new tmux completion latches green");
            terminalLatchSlots[0].State = AiTaskState.Completed;
            terminalCompletionLatch.Apply(terminalLatchSlots);
            Assert(terminalLatchSlots[0].State == AiTaskState.Completed, "tmux completion green persists before session return");
            Assert(terminalCompletionLatch.Acknowledge("$1"), "tmux completion acknowledgement accepted");
            terminalLatchSlots[0].State = AiTaskState.Completed;
            terminalCompletionLatch.Apply(terminalLatchSlots);
            Assert(terminalLatchSlots[0].State == AiTaskState.Idle, "returning to a completed tmux session changes green to white");
            terminalLatchSlots[0].State = AiTaskState.Thinking;
            terminalCompletionLatch.Apply(terminalLatchSlots);
            terminalLatchSlots[0].State = AiTaskState.Completed;
            terminalCompletionLatch.Apply(terminalLatchSlots);
            Assert(terminalLatchSlots[0].State == AiTaskState.Completed, "second tmux completion latches independently");
            terminalCompletionLatch.RetainLiveSessions(new List<TerminalSessionStatusSlot>());
            terminalLatchSlots[0].SessionIdentity = "$1@2000:999";
            terminalLatchSlots[0].State = AiTaskState.Completed;
            terminalCompletionLatch.Apply(terminalLatchSlots);
            Assert(terminalLatchSlots[0].State == AiTaskState.Idle, "recreated same-name session with a new server generation does not inherit stale green");
            var pendingAcknowledgementLatch = new TerminalCompletionLatch();
            var pendingAcknowledgementSlots = new List<TerminalSessionStatusSlot>
            {
                new TerminalSessionStatusSlot
                {
                    Slot = 2,
                    SessionId = "$2",
                    SessionName = "02-Coding",
                    Present = true,
                    State = AiTaskState.Thinking
                }
            };
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            Assert(!pendingAcknowledgementLatch.Acknowledge("$2"), "switch while still thinking has no completion to acknowledge");
            pendingAcknowledgementSlots[0].State = AiTaskState.Completed;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            Assert(pendingAcknowledgementSlots[0].State == AiTaskState.Completed, "completion occurring after the switch sample remains green");
            pendingAcknowledgementSlots[0].State = AiTaskState.Thinking;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            Assert(!pendingAcknowledgementLatch.Acknowledge("$2"), "entering a still-running session does not arm a future acknowledgement");
            pendingAcknowledgementSlots[0].State = AiTaskState.Thinking;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            pendingAcknowledgementSlots[0].State = AiTaskState.Completed;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            Assert(pendingAcknowledgementSlots[0].State == AiTaskState.Completed, "completion occurring after the acknowledged thinking observation still turns green");
            pendingAcknowledgementSlots[0].State = AiTaskState.WaitingForInput;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            pendingAcknowledgementLatch.Acknowledge("$2");
            pendingAcknowledgementSlots[0].State = AiTaskState.WaitingForInput;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            Assert(pendingAcknowledgementSlots[0].State == AiTaskState.WaitingForInput, "acknowledgement never hides a waiting-for-input state");
            pendingAcknowledgementSlots[0].State = AiTaskState.Completed;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            Assert(pendingAcknowledgementSlots[0].State == AiTaskState.Completed, "completion after an acknowledged waiting observation still turns green");
            pendingAcknowledgementSlots[0].State = AiTaskState.Error;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            pendingAcknowledgementLatch.Acknowledge("$2");
            pendingAcknowledgementSlots[0].State = AiTaskState.Error;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            Assert(pendingAcknowledgementSlots[0].State == AiTaskState.Error, "acknowledgement never hides an error state");
            pendingAcknowledgementSlots[0].State = AiTaskState.Completed;
            pendingAcknowledgementLatch.Apply(pendingAcknowledgementSlots);
            Assert(pendingAcknowledgementSlots[0].State == AiTaskState.Completed, "completion after an acknowledged error observation still turns green");
            var revisionLatch = new TerminalCompletionLatch();
            var revisionSlots = new List<TerminalSessionStatusSlot>
            {
                new TerminalSessionStatusSlot
                {
                    Slot = 3,
                    SessionId = "$3",
                    SessionIdentity = "$3@1000:624",
                    SessionName = "03-Explore",
                    Present = true,
                    State = AiTaskState.Completed,
                    StateRevisionNanoseconds = 100
                }
            };
            revisionLatch.Apply(revisionSlots);
            Assert(revisionSlots[0].State == AiTaskState.Idle, "first completion revision is historical and acknowledged");
            revisionSlots[0].State = AiTaskState.Thinking;
            revisionSlots[0].StateRevisionNanoseconds = 200;
            revisionLatch.Apply(revisionSlots);
            revisionSlots[0].State = AiTaskState.Completed;
            revisionSlots[0].StateRevisionNanoseconds = 300;
            revisionLatch.Apply(revisionSlots);
            Assert(revisionSlots[0].State == AiTaskState.Completed, "new completion revision latches green");
            Assert(!revisionLatch.AcknowledgeUpTo("$3@1000:624", 200), "pre-switch cutoff does not acknowledge a later latched completion");
            revisionSlots[0].State = AiTaskState.Completed;
            revisionLatch.Apply(revisionSlots);
            Assert(revisionSlots[0].State == AiTaskState.Completed, "completion later than the cutoff remains green");
            Assert(revisionLatch.AcknowledgeUpTo("$3@1000:624", 300), "cutoff at the completion revision acknowledges it");
            revisionSlots[0].State = AiTaskState.Completed;
            revisionLatch.Apply(revisionSlots);
            Assert(revisionSlots[0].State == AiTaskState.Idle, "same completion revision stays acknowledged");
            revisionSlots[0].State = AiTaskState.Completed;
            revisionSlots[0].StateRevisionNanoseconds = 400;
            revisionLatch.Apply(revisionSlots);
            Assert(revisionSlots[0].State == AiTaskState.Completed, "a later completion revision turns green even when a complete thinking cycle occurred between polls");
            Assert(revisionLatch.Acknowledge("$3@1000:624"), "newer completion revision can be acknowledged");
            revisionSlots[0].State = AiTaskState.Completed;
            revisionSlots[0].StateRevisionNanoseconds = 300;
            revisionLatch.Apply(revisionSlots);
            Assert(revisionSlots[0].State == AiTaskState.Idle, "completed revision rollback after a higher-revision pane disappears does not create a false green");
            revisionSlots[0].State = AiTaskState.Completed;
            revisionSlots[0].StateRevisionNanoseconds = 500;
            revisionLatch.Apply(revisionSlots);
            Assert(revisionSlots[0].State == AiTaskState.Completed, "completion revision above the high-water mark creates a new green latch");
            var hiddenCompletionLatch = new TerminalCompletionLatch();
            var hiddenCompletionSlots = new List<TerminalSessionStatusSlot>
            {
                new TerminalSessionStatusSlot
                {
                    Slot = 4,
                    SessionId = "$4",
                    SessionIdentity = "$4@1000:624",
                    SessionName = "04-CC",
                    Present = true,
                    State = AiTaskState.Thinking,
                    StateRevisionNanoseconds = 200
                }
            };
            hiddenCompletionLatch.Apply(hiddenCompletionSlots);
            Assert(!hiddenCompletionLatch.AcknowledgeUpTo("$4@1000:624", 300), "switch cutoff is recorded even when the aggregate is still running");
            hiddenCompletionSlots[0].State = AiTaskState.Completed;
            hiddenCompletionSlots[0].StateRevisionNanoseconds = 100;
            hiddenCompletionLatch.Apply(hiddenCompletionSlots);
            Assert(hiddenCompletionSlots[0].State == AiTaskState.Idle, "an older completion exposed after a higher-priority pane disappears stays acknowledged");
            hiddenCompletionSlots[0].State = AiTaskState.Thinking;
            hiddenCompletionSlots[0].StateRevisionNanoseconds = 400;
            hiddenCompletionLatch.Apply(hiddenCompletionSlots);
            hiddenCompletionSlots[0].State = AiTaskState.Completed;
            hiddenCompletionSlots[0].StateRevisionNanoseconds = 500;
            hiddenCompletionLatch.Apply(hiddenCompletionSlots);
            Assert(hiddenCompletionSlots[0].State == AiTaskState.Completed, "a genuine completion after the switch cutoff still latches green");
            var firstAfterCutoffLatch = new TerminalCompletionLatch();
            Assert(!firstAfterCutoffLatch.AcknowledgeUpTo("$5@1000:624", 300), "cutoff can precede the first status observation");
            var firstAfterCutoffSlots = new List<TerminalSessionStatusSlot>
            {
                new TerminalSessionStatusSlot
                {
                    Slot = 5,
                    SessionId = "$5",
                    SessionIdentity = "$5@1000:624",
                    SessionName = "05-grok",
                    Present = true,
                    State = AiTaskState.Completed,
                    StateRevisionNanoseconds = 400
                }
            };
            firstAfterCutoffLatch.Apply(firstAfterCutoffSlots);
            Assert(firstAfterCutoffSlots[0].State == AiTaskState.Completed, "first observed completion after an explicit switch cutoff turns green");
            var missedActiveCycleLatch = new TerminalCompletionLatch();
            var missedActiveCycleSlots = new List<TerminalSessionStatusSlot>
            {
                new TerminalSessionStatusSlot
                {
                    Slot = 6,
                    SessionId = "$6",
                    SessionIdentity = "$6@1000:624",
                    SessionName = "06-search",
                    Present = true,
                    State = AiTaskState.Idle,
                    StateRevisionNanoseconds = 100
                }
            };
            missedActiveCycleLatch.Apply(missedActiveCycleSlots);
            missedActiveCycleSlots[0].State = AiTaskState.Completed;
            missedActiveCycleSlots[0].StateRevisionNanoseconds = 400;
            missedActiveCycleLatch.Apply(missedActiveCycleSlots);
            Assert(missedActiveCycleSlots[0].State == AiTaskState.Completed, "idle to newer completed revision latches even when the active cycle occurred between polls");
            Assert(TerminalWindowActivator.IsTerminalProcessName("WindowsTerminal") && TerminalWindowActivator.IsTerminalProcessName("wt"), "terminal focus accepts supported Windows Terminal process names");
            Assert(!TerminalWindowActivator.IsTerminalProcessName("notepad"), "terminal focus rejects a recycled HWND owned by another process");
            string completed = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}";
            string started = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}";
            string aborted = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_aborted\"}}";
            Assert(ChatGptTaskStatusReader.ReadTextStateForTest(completed) == AiTaskState.Idle, "completed task state");
            Assert(ChatGptTaskStatusReader.ReadTextStateForTest(completed + started) == AiTaskState.Thinking, "active task state");
            Assert(ChatGptTaskStatusReader.ReadTextStateForTest(started + aborted) == AiTaskState.Error, "aborted task state");
            string pending = started + "\n{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"id\":\"fc1\",\"name\":\"request_user_input\",\"call_id\":\"call1\"}}";
            string answered = pending + "\n{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"call1\",\"output\":\"ok\"}}";
            Assert(ChatGptTaskStatusReader.ReadTextStateForTest(pending) == AiTaskState.WaitingForInput, "pending user-input task state");
            Assert(ChatGptTaskStatusReader.ReadTextStateForTest(answered) == AiTaskState.Thinking, "answered user-input task state");
            var completionLatch = new ChatGptCompletionLatch();
            var latchSlots = new List<ChatGptTaskStatusSlot>
            {
                new ChatGptTaskStatusSlot
                {
                    Slot = 1,
                    ThreadId = "self-test-thread",
                    State = AiTaskState.Idle,
                    Evidence = "task_complete"
                }
            };
            completionLatch.Apply(latchSlots);
            Assert(latchSlots[0].State == AiTaskState.Idle, "old completion starts acknowledged");
            latchSlots[0].State = AiTaskState.Thinking;
            latchSlots[0].Evidence = "task_started";
            completionLatch.Apply(latchSlots);
            latchSlots[0].State = AiTaskState.Idle;
            latchSlots[0].Evidence = "task_complete";
            completionLatch.Apply(latchSlots);
            Assert(latchSlots[0].State == AiTaskState.Completed, "new completion latches green");
            latchSlots[0].State = AiTaskState.Idle;
            latchSlots[0].Evidence = "task_complete";
            completionLatch.Apply(latchSlots);
            Assert(latchSlots[0].State == AiTaskState.Completed, "green persists across polls");
            Assert(completionLatch.Acknowledge("self-test-thread"), "completion acknowledgement accepted");
            latchSlots[0].State = AiTaskState.Idle;
            latchSlots[0].Evidence = "task_complete";
            completionLatch.Apply(latchSlots);
            Assert(latchSlots[0].State == AiTaskState.Idle, "acknowledged completion returns white");

            var assigned = new ChatGptThreadSlot[6];
            for (int i = 0; i < assigned.Length; i++)
                assigned[i] = TestThread("task-" + (i + 1), 600 - i * 100);
            var reordered = new List<ChatGptThreadSlot>
            {
                TestThread("task-6", 900),
                TestThread("task-1", 800),
                TestThread("task-2", 700),
                TestThread("task-3", 600),
                TestThread("task-4", 500),
                TestThread("task-5", 400)
            };
            ChatGptThreadSlot[] stable = ChatGptTaskSlotRegistry.ReconcileForTest(assigned, reordered, true);
            for (int i = 0; i < stable.Length; i++)
                Assert(stable[i].Id == "task-" + (i + 1), "task slot remains stable " + (i + 1));
            reordered.Insert(0, TestThread("task-new", 1000));
            ChatGptThreadSlot[] adopted = ChatGptTaskSlotRegistry.ReconcileForTest(stable, reordered, true);
            int adoptedCount = 0;
            for (int i = 0; i < adopted.Length; i++)
                if (adopted[i].Id == "task-new")
                    adoptedCount++;
            Assert(adoptedCount == 1, "new task replaces exactly one least-recent slot");

            string appServerSample =
                "{\"id\":2,\"result\":{\"data\":[{" +
                "\"id\":\"00000000-0000-0000-0000-000000000001\"," +
                "\"name\":\"App Server task\",\"recencyAt\":1786000000," +
                "\"path\":\"C:\\\\rollout.jsonl\",\"status\":{\"type\":\"notLoaded\"}}]}}";
            List<ChatGptThreadSlot> appServerThreads =
                CodexAppServerThreadCatalog.ParseThreadListResponseForTest(appServerSample);
            Assert(appServerThreads.Count == 1, "App Server thread/list parser count");
            Assert(appServerThreads[0].Title == "App Server task", "App Server thread title");
            Assert(appServerThreads[0].RecencyAtMilliseconds == 1786000000000L, "App Server timestamp normalized to milliseconds");
            Assert(appServerThreads[0].RuntimeStatus == "notLoaded", "App Server runtime status parsed");

            string currentStateSql = ChatGptThreadCatalog.BuildReadRecentSqlForTest(
                "id", "title", "preview", "rollout_path", "archived", "thread_source", "recency_at_ms", "updated_at_ms");
            Assert(currentStateSql.IndexOf("NULLIF(name,''", StringComparison.OrdinalIgnoreCase) < 0, "current local catalog schema does not require missing name column");
            Assert(currentStateSql.IndexOf("NULLIF(title,''", StringComparison.OrdinalIgnoreCase) >= 0, "current local catalog schema uses title column");
            string legacyStateSql = ChatGptThreadCatalog.BuildReadRecentSqlForTest(
                "id", "name", "rollout_path", "archived", "updated_at_ms");
            Assert(legacyStateSql.IndexOf("NULLIF(name,''", StringComparison.OrdinalIgnoreCase) >= 0, "legacy local catalog schema keeps name column support");
            Assert(legacyStateSql.IndexOf("updated_at_ms DESC", StringComparison.OrdinalIgnoreCase) >= 0, "legacy local catalog schema falls back to updated_at_ms ordering");
            Assert(ChatGptThreadCatalog.NormalizeFileSystemPath(@"\\?\C:\Temp\rollout.jsonl") == @"C:\Temp\rollout.jsonl", "Win32 extended drive path is normalized for .NET Framework file IO");
            Assert(ChatGptThreadCatalog.NormalizeFileSystemPath(@"\\?\UNC\server\share\rollout.jsonl") == @"\\server\share\rollout.jsonl", "Win32 extended UNC path is normalized for .NET Framework file IO");

            string codexHomeTestRoot = Path.Combine(
                Path.GetTempPath(),
                "cxt12e4-codex-home-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string envCodexHome = Path.Combine(codexHomeTestRoot, "custom-codex-home");
                string userProfile = Path.Combine(codexHomeTestRoot, "profile");
                string fallbackCodexHome = Path.Combine(userProfile, ".codex");
                Directory.CreateDirectory(envCodexHome);
                Directory.CreateDirectory(fallbackCodexHome);
                List<string> codexHomes = ChatGptThreadCatalog.BuildCodexHomeCandidatesForTest(envCodexHome, userProfile);
                Assert(codexHomes.Count == 2, "Codex home discovery keeps CODEX_HOME and profile fallback");
                Assert(string.Equals(codexHomes[0], Path.GetFullPath(envCodexHome), StringComparison.OrdinalIgnoreCase), "CODEX_HOME is preferred over profile .codex");
                Assert(string.Equals(codexHomes[1], Path.GetFullPath(fallbackCodexHome), StringComparison.OrdinalIgnoreCase), "profile .codex remains a fallback Codex home");
            }
            finally
            {
                if (Directory.Exists(codexHomeTestRoot))
                    Directory.Delete(codexHomeTestRoot, true);
            }

            Console.WriteLine("SELF-TEST PASS: trigger state sequences, context invalidation, trigger matrix, layer resolver, RGB conversion, ChatGPT/task-state parsing, tmux session aggregation, App Server catalog parsing, completion acknowledgement, and stable task slots.");
            Console.WriteLine("No hook installed and no input or HID write was sent.");
            return 0;
        }

        private static void RunTriggerProcessorSelfTests()
        {
            // Layer reassignment must not change shared tap/hold/chord semantics.
            foreach (int profileLayer in new[] { 0, 5 })
            {
                for (int key = 1; key <= 12; key++)
                {
                    using (var tap = new TriggerHarness(profileLayer))
                    {
                        tap.Down(key, 40000);
                        tap.Up(key, 40649);
                        Assert(tap.Actions.Count == 1 && tap.Actions[0] == "button:" + profileLayer + ":" + key + ":False",
                            "new profile key below 650ms emits only its tap: " + profileLayer + "/" + key);
                    }
                    using (var hold = new TriggerHarness(profileLayer))
                    {
                        hold.Down(key, 41000);
                        hold.Up(key, 41650);
                        string expected = ActionEngine.TerminalSessionSlotForKey(key) > 0 ? "terminal:" + key : "focus:" + key;
                        Assert(hold.Actions.Count == 1 && hold.Actions[0] == expected,
                            "new profile key at 650ms emits only global hold: " + profileLayer + "/" + key);
                    }
                    if (key < 12)
                    {
                        using (var chord = new TriggerHarness(profileLayer))
                        {
                            chord.Down(12, 42000);
                            chord.Down(key, 42030);
                            chord.Up(key, 42100);
                            chord.Up(12, 42120);
                            Assert(chord.Actions.Count == 1 && chord.Actions[0] == "taskbar:" + key,
                                "new profile K12 chord suppresses both taps: " + profileLayer + "/" + key);
                        }
                    }
                }
            }
            int[] globalTaskKeys = { 1, 2, 3, 5, 6, 7 };
            int[] globalTerminalKeys = { 9, 10, 11, 12, 8, 4 };
            int matrixTimestamp = 100000;
            for (int layer = 0; layer <= 7; layer++)
            {
                for (int index = 0; index < globalTaskKeys.Length; index++)
                {
                    int key = globalTaskKeys[index];
                    using (var harness = new TriggerHarness(layer))
                    {
                        harness.Down(key, matrixTimestamp);
                        harness.Up(key, matrixTimestamp + 649);
                        Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:" + layer + ":" + key + ":False", "global task key keeps layer short action below 650ms on L" + layer);
                    }
                    matrixTimestamp += 1000;
                    using (var harness = new TriggerHarness(layer))
                    {
                        harness.Down(key, matrixTimestamp);
                        harness.Up(key, matrixTimestamp + 650);
                        Assert(harness.Actions.Count == 1 && harness.Actions[0] == "focus:" + key, "global task key owns exact 650ms boundary on L" + layer);
                    }
                    matrixTimestamp += 1000;
                }
                for (int index = 0; index < globalTerminalKeys.Length; index++)
                {
                    int key = globalTerminalKeys[index];
                    using (var harness = new TriggerHarness(layer))
                    {
                        harness.Down(key, matrixTimestamp);
                        harness.Up(key, matrixTimestamp + 649);
                        Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:" + layer + ":" + key + ":False", "global tmux key keeps layer short action below 650ms on L" + layer);
                    }
                    matrixTimestamp += 1000;
                    using (var harness = new TriggerHarness(layer))
                    {
                        harness.Down(key, matrixTimestamp);
                        harness.Up(key, matrixTimestamp + 650);
                        Assert(harness.Actions.Count == 1 && harness.Actions[0] == "terminal:" + key, "global tmux key owns exact 650ms boundary on L" + layer);
                    }
                    matrixTimestamp += 1000;
                }
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(12, 1000);
                harness.Down(2, 1010);
                harness.Down(3, 1020);
                harness.Up(2, 1030);
                harness.Up(12, 1040);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "taskbar:2", "K12 third selector cannot overwrite chord owner");
                harness.Up(3, 1050);
                Assert(harness.Actions.Count == 1, "K12 third selector release remains suppressed");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(3, 2000);
                harness.Down(2, 2010);
                harness.Down(12, 2020);
                harness.Up(3, 2030);
                harness.Up(2, 2040);
                harness.Up(12, 2050);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "taskbar:3", "selector-first K12 chord keeps earliest owner and suppresses peers");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(2, 2200);
                harness.Down(12, 2450);
                harness.Down(12, 2451);
                harness.Up(2, 2460);
                harness.Up(12, 2470);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "taskbar:2", "K12 accepts exact 250ms boundary and ignores repeated keydown");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(7, 2300);
                harness.Down(2, 2300);
                harness.Down(12, 2400);
                harness.Up(7, 2410);
                harness.Up(2, 2420);
                harness.Up(12, 2430);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "taskbar:7", "same-timestamp K12 candidates use arrival sequence, not key number");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(2, 2500);
                harness.Down(12, 2751);
                harness.Up(2, 2760);
                harness.Up(12, 2770);
                Assert(harness.Actions.Count == 2 && harness.Actions[0] == "button:0:2:False" && harness.Actions[1] == "button:0:12:False", "K12 rejects selector-first presses outside 250ms window");
            }

            using (var harness = new TriggerHarness(1))
            {
                harness.Down(4, 2900);
                harness.Down(12, 2910);
                harness.Up(4, 2920);
                harness.Up(12, 2930);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "taskbar:4", "K4 plus K12 keeps taskbar-chord priority");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(12, 2940);
                harness.Foreground = new IntPtr(2);
                harness.Generation++;
                harness.Layer = 5;
                harness.Down(2, 2950);
                harness.Up(2, 2960);
                harness.Up(12, 2970);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:2:False", "foreground drift prevents key12 from capturing a selector in the new context");
            }

            using (var harness = new TriggerHarness(1))
            {
                harness.Down(4, 2980);
                harness.Foreground = new IntPtr(2);
                harness.Generation++;
                harness.Layer = 5;
                harness.Down(2, 2990);
                harness.Up(2, 3000);
                harness.Up(4, 3010);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:2:False", "foreground drift prevents L1 key4 from capturing a selector in the new context");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(2, 3020);
                harness.Foreground = new IntPtr(2);
                harness.Generation++;
                harness.Layer = 5;
                harness.Down(12, 3030);
                harness.Up(2, 3040);
                harness.Up(12, 3050);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:12:False", "reverse-order taskbar chord cannot cross foreground, layer, or context generation");
            }

            using (var harness = new TriggerHarness(1))
            {
                harness.Down(2, 3060);
                harness.Foreground = new IntPtr(2);
                harness.Generation++;
                harness.Down(4, 3070);
                harness.Up(2, 3080);
                harness.Up(4, 3090);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:1:4:False", "independent L1 key4 tap keeps its own captured context");
            }

            using (var harness = new TriggerHarness(1))
            {
                harness.Down(4, 3000);
                harness.Down(2, 3010);
                harness.Down(3, 3015);
                harness.Up(3, 3018);
                harness.Down(12, 3020);
                harness.Up(2, 3030);
                harness.Up(4, 3040);
                harness.Up(12, 3050);
                Assert(harness.Actions.Count == 2 && harness.Actions[0] == "button:1:3:False" && harness.Actions[1] == "taskbar:4", "removed key4 prefix leaves K12 as the only chord owner");
            }

            using (var harness = new TriggerHarness(4))
            {
                harness.Down(9, 4000);
                harness.Down(12, 4010);
                Thread.Sleep(800);
                Assert(harness.Actions.Count == 0, "K12 chord suppresses both universal tmux session holds");
                harness.Up(9, 4820);
                harness.Up(12, 4830);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "taskbar:9", "suppressed tmux session keys dispatch only the taskbar chord");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(12, 4840);
                Thread.Sleep(700);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "terminal:12", "key12 tmux hold fires before a late selector");
                harness.Down(2, 5540);
                harness.Up(2, 5550);
                harness.Up(12, 5560);
                Assert(harness.Actions.Count == 1, "selector after completed key12 hold cannot add a taskbar chord");
            }

            using (var harness = new TriggerHarness(1))
            {
                harness.Down(4, 5570);
                Thread.Sleep(700);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "terminal:4", "L1 key4 tmux hold fires before a late selector");
                harness.Down(2, 6270);
                harness.Up(2, 6280);
                harness.Up(4, 6290);
                Assert(harness.Actions.Count == 2 && harness.Actions[1] == "button:1:2:False", "selector after completed L1 key4 hold remains an independent short action");
            }

            using (var harness = new TriggerHarness(4))
            {
                harness.Down(9, 4900);
                harness.Up(9, 5549);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:4:9:False", "tmux light key keeps the current layer short action below 650ms");
            }

            using (var harness = new TriggerHarness(4))
            {
                harness.Down(9, 5800);
                harness.Up(9, 6450);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "terminal:9", "tmux session long hold replaces the former layer-specific key9 hold");
            }

            using (var harness = new TriggerHarness(4))
            {
                harness.Down(9, 6700);
                harness.Foreground = new IntPtr(2);
                harness.Generation++;
                harness.Foreground = new IntPtr(1);
                harness.Generation++;
                Thread.Sleep(800);
                harness.Up(9, 7500);
                Assert(harness.Actions.Count == 0, "ABA foreground change permanently invalidates a tmux session hold");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.Down(1, 7600);
                harness.Down(2, 7620);
                Thread.Sleep(700);
                harness.Up(1, 8300);
                harness.Up(2, 8320);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "focus:1", "overlapping global task holds have one long-press owner");
            }

            using (var harness = new TriggerHarness(1))
            {
                harness.Down(5, 8330);
                harness.Up(5, 8980);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "focus:5", "ChatGPT task lights use the same 650ms hold rule inside L1");
            }

            using (var harness = new TriggerHarness(2))
            {
                harness.Down(4, 8350);
                harness.Up(4, 8999);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:2:4:False", "L2 illuminated session key below 650ms keeps its short action");
            }

            using (var harness = new TriggerHarness(2))
            {
                harness.Down(11, 9000);
                harness.Up(11, 9650);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "terminal:11", "L2 illuminated session key at 650ms switches its tmux slot without the short action");
            }

            using (var harness = new TriggerHarness(5))
            {
                harness.Down(4, 9000);
                harness.Up(4, 9650);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "terminal:4", "tmux illuminated session keys use the same hold rule outside L2");
            }

            using (var harness = new TriggerHarness(5))
            {
                BridgeTrigger down = harness.Publish(2, true, 18000);
                harness.ProcessPublished(down);
                BridgeTrigger up = harness.Publish(2, false, 18649);
                Thread.Sleep(750);
                Assert(harness.Actions.Count == 0, "Raw Input publishes a queued 649ms key-up to the timer before business consumption");
                harness.ProcessPublished(up);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:2:False", "queued Raw Input key-up later completes exactly one short action");
            }

            using (var harness = new TriggerHarness(5))
            {
                BridgeTrigger down = harness.Publish(2, true, 19000);
                harness.ProcessPublished(down);
                harness.InvalidatePhysicalLedger();
                harness.CancelBusinessInputs();
                Thread.Sleep(750);
                Assert(harness.Actions.Count == 0, "device-generation cancellation never synthesizes a short or long action");
                harness.ProcessPublished(harness.Publish(2, false, 19750));
                Assert(harness.Actions.Count == 0, "orphan release after generation cancellation remains action-free");
            }

            using (var harness = new TriggerHarness(5))
            {
                harness.ProcessPublished(harness.Publish(2, true, 20000));
                harness.InvalidatePhysicalLedger();
                BridgeTrigger nextGenerationDown = harness.Publish(2, true, 21000);
                // An old generation's FIFO cancel may run after the new down
                // was physically published, but before its business item.
                harness.CancelBusinessInputs();
                harness.ProcessPublished(nextGenerationDown);
                harness.ProcessPublished(harness.Publish(2, false, 21100));
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:2:False", "a delayed old-generation business cancel does not erase a new physical down");
            }

            using (var harness = new TriggerHarness(2))
            {
                harness.Down(8, 9700);
                Thread.Sleep(750);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "terminal:8", "L2 session timer switches the illuminated tmux slot once");
                harness.Up(8, 10450);
                Assert(harness.Actions.Count == 1, "release after L2 session timer does not send Enter or switch twice");
            }

            using (var harness = new TriggerHarness(2))
            {
                harness.Down(12, 10500);
                harness.Up(12, 11149);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:2:12:False", "L2 key12 short press remains Escape");
            }

            using (var harness = new TriggerHarness(2))
            {
                harness.Down(12, 11200);
                harness.Up(12, 11850);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "terminal:12", "L2 key12 long press switches session 4 instead of selecting L8");
            }

            using (var harness = new TriggerHarness(2))
            {
                harness.Down(12, 11900);
                harness.Down(2, 11920);
                Thread.Sleep(700);
                harness.Up(2, 12620);
                harness.Up(12, 12630);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "taskbar:2", "L2 key12 taskbar chord cancels its session hold and keeps chord priority");
            }

            using (var harness = new TriggerHarness(5))
            {
                harness.Down(2, 8400);
                harness.Up(2, 9049);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:2:False", "browser task key released before 650ms keeps its short action");
            }

            using (var harness = new TriggerHarness(5))
            {
                // Do not wait for the wall-clock timer. This reproduces a delayed
                // timer callback while the hook timestamps still prove a 650ms hold.
                harness.Down(2, 9100);
                harness.Up(2, 9750);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "focus:2", "browser task key at 650ms uses release fallback instead of bookmark action");
            }

            using (var harness = new TriggerHarness(5))
            {
                harness.Down(2, 9800);
                harness.Up(2, 10500);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "focus:2", "browser task key above 650ms uses release fallback exactly once");
            }

            using (var harness = new TriggerHarness(5))
            {
                harness.Down(2, int.MaxValue - 300L);
                harness.Up(2, int.MaxValue + 400L);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "focus:2", "task release fallback remains correct across TickCount wrap");
            }

            using (var harness = new TriggerHarness(5))
            {
                harness.Down(2, 10600);
                harness.Foreground = new IntPtr(2);
                harness.Generation++;
                harness.Up(2, 11300);
                Assert(harness.Actions.Count == 0, "foreground drift cancels browser task-key release fallback");
            }

            using (var harness = new TriggerHarness(5))
            {
                var releaseUp = new ManualResetEvent(false);
                var upObserved = new ManualResetEvent(false);
                var timerStarted = new ManualResetEvent(false);
                Thread upThread = null;
                try
                {
                    harness.PhysicalEdgeObserved = delegate(int key, bool isDown, long token)
                    {
                        if (key == 2 && !isDown)
                        {
                            upObserved.Set();
                            releaseUp.WaitOne(2500);
                        }
                    };
                    harness.TimerCallbackStarting = delegate(string timerName, int key, long token)
                    {
                        if (timerName == "global-task" && key == 2)
                            timerStarted.Set();
                    };

                    harness.Down(2, 12000);
                    upThread = new Thread(new ThreadStart(delegate { harness.Up(2, 12649); }));
                    upThread.IsBackground = true;
                    upThread.Start();
                    Assert(upObserved.WaitOne(1000), "physical key-up is published before waiting for the state-machine lock");
                    Assert(timerStarted.WaitOne(1500), "650ms task timer reached the key-up race test");
                    releaseUp.Set();
                    Assert(upThread.Join(1000), "blocked key-up completes after timer race");
                    Thread.Sleep(100);
                    Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:2:False", "physical 649ms key-up wins over the 650ms timer");
                }
                finally
                {
                    releaseUp.Set();
                    if (upThread != null)
                        upThread.Join(1000);
                    releaseUp.Dispose();
                    upObserved.Dispose();
                    timerStarted.Dispose();
                }
            }

            using (var harness = new TriggerHarness(5))
            {
                var oldTimerStarted = new ManualResetEvent(false);
                var releaseOldTimer = new ManualResetEvent(false);
                long firstPressToken = 0;
                try
                {
                    harness.PhysicalEdgeObserved = delegate(int key, bool isDown, long token)
                    {
                        if (key == 2 && isDown && firstPressToken == 0)
                            firstPressToken = token;
                    };
                    harness.TimerCallbackStarting = delegate(string timerName, int key, long token)
                    {
                        if (timerName == "global-task" && key == 2 && token == firstPressToken)
                        {
                            oldTimerStarted.Set();
                            releaseOldTimer.WaitOne(2500);
                        }
                    };

                    harness.Down(2, 13000);
                    Assert(oldTimerStarted.WaitOne(1500), "first task timer entered its queued callback");
                    harness.Up(2, 13100);
                    harness.Down(2, 13200);
                    releaseOldTimer.Set();
                    Thread.Sleep(100);
                    Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:2:False", "disposed old task timer cannot act on a later press");
                    harness.Up(2, 13250);
                    Assert(harness.Actions.Count == 2 && harness.Actions[1] == "button:5:2:False", "later task press remains independent after stale callback");
                }
                finally
                {
                    releaseOldTimer.Set();
                    oldTimerStarted.Dispose();
                    releaseOldTimer.Dispose();
                }
            }

            using (var harness = new TriggerHarness(5))
            {
                long firstToken = 0;
                long repeatToken = 0;
                int downEdges = 0;
                harness.PhysicalEdgeObserved = delegate(int key, bool isDown, long token)
                {
                    if (key != 2 || !isDown)
                        return;
                    downEdges++;
                    if (downEdges == 1)
                        firstToken = token;
                    else if (downEdges == 2)
                        repeatToken = token;
                };
                harness.Down(2, 14000);
                harness.Down(2, 14001);
                harness.Up(2, 14010);
                Assert(firstToken != 0 && firstToken == repeatToken, "repeated keydown preserves the physical press token");
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:2:False", "repeated keydown does not duplicate the short action");
            }

            using (var harness = new TriggerHarness(5))
            {
                harness.Down(2, 15000);
                Thread.Sleep(750);
                Assert(harness.Actions.Count == 1 && harness.Actions[0] == "focus:2", "task timer dispatches a held press once");
                harness.Up(2, 15750);
                Assert(harness.Actions.Count == 1, "release after task timer does not dispatch a duplicate action");
            }

            using (var harness = new TriggerHarness(5))
            {
                var oldSessionTimerStarted = new ManualResetEvent(false);
                var releaseOldSessionTimer = new ManualResetEvent(false);
                long firstPressToken = 0;
                try
                {
                    harness.PhysicalEdgeObserved = delegate(int key, bool isDown, long token)
                    {
                        if (key == 4 && isDown && firstPressToken == 0)
                            firstPressToken = token;
                    };
                    harness.TimerCallbackStarting = delegate(string timerName, int key, long token)
                    {
                        if (timerName == "terminal-session" && key == 4 && token == firstPressToken)
                        {
                            oldSessionTimerStarted.Set();
                            releaseOldSessionTimer.WaitOne(2500);
                        }
                    };
                    harness.Down(4, 16000);
                    Assert(oldSessionTimerStarted.WaitOne(1500), "first universal tmux timer entered its queued callback");
                    harness.Up(4, 16100);
                    harness.Down(4, 16200);
                    releaseOldSessionTimer.Set();
                    Thread.Sleep(100);
                    Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:5:4:False", "disposed old tmux timer cannot switch on a later press");
                    harness.Up(4, 16250);
                    Assert(harness.Actions.Count == 2 && harness.Actions[1] == "button:5:4:False", "later short press remains independent after a stale tmux callback");
                }
                finally
                {
                    releaseOldSessionTimer.Set();
                    oldSessionTimerStarted.Dispose();
                    releaseOldSessionTimer.Dispose();
                }
            }

            using (var harness = new TriggerHarness(4))
            {
                var oldSessionTimerStarted = new ManualResetEvent(false);
                var releaseOldSessionTimer = new ManualResetEvent(false);
                long firstPressToken = 0;
                try
                {
                    harness.PhysicalEdgeObserved = delegate(int key, bool isDown, long token)
                    {
                        if (key == 9 && isDown && firstPressToken == 0)
                            firstPressToken = token;
                    };
                    harness.TimerCallbackStarting = delegate(string timerName, int key, long token)
                    {
                        if (timerName == "terminal-session" && key == 9 && token == firstPressToken)
                        {
                            oldSessionTimerStarted.Set();
                            releaseOldSessionTimer.WaitOne(2500);
                        }
                    };
                    harness.Down(9, 17000);
                    Assert(oldSessionTimerStarted.WaitOne(1500), "first key9 tmux timer entered its queued callback");
                    harness.Up(9, 17100);
                    harness.Down(9, 17200);
                    releaseOldSessionTimer.Set();
                    Thread.Sleep(100);
                    Assert(harness.Actions.Count == 1 && harness.Actions[0] == "button:4:9:False", "disposed old key9 tmux timer cannot affect a later press");
                    harness.Up(9, 17250);
                    Assert(harness.Actions.Count == 2 && harness.Actions[1] == "button:4:9:False", "later key9 short press remains independent after stale callback");
                }
                finally
                {
                    releaseOldSessionTimer.Set();
                    oldSessionTimerStarted.Dispose();
                    releaseOldSessionTimer.Dispose();
                }
            }

            var disposedHarness = new TriggerHarness(7);
            disposedHarness.Down(9, 5000);
            disposedHarness.Dispose();
            Thread.Sleep(800);
            Assert(disposedHarness.Actions.Count == 0, "Dispose prevents queued universal tmux callbacks");

            using (var harness = new TriggerHarness(5))
            {
                harness.Down(4, 6000);
                harness.Foreground = new IntPtr(2);
                harness.Generation++;
                Thread.Sleep(700);
                harness.Up(4, 6700);
                Assert(harness.Actions.Count == 0, "foreground drift cancels universal tmux long and short actions");
            }

            using (var harness = new TriggerHarness(5))
            {
                harness.ActionDispatchStarting = delegate(string actionName, int key)
                {
                    if (actionName == "global-task" && key == 2)
                    {
                        harness.Foreground = new IntPtr(2);
                        harness.Generation++;
                    }
                };
                harness.Down(2, 6800);
                harness.Up(2, 7450);
                Assert(harness.Actions.Count == 0, "global hold keeps the press-time context when the foreground changes after validation but before enqueue");
            }

            using (var harness = new TriggerHarness(0))
            {
                harness.ActionDispatchStarting = delegate(string actionName, int key)
                {
                    if (actionName == "taskbar-chord")
                    {
                        harness.Foreground = new IntPtr(2);
                        harness.Generation++;
                    }
                };
                harness.Down(12, 7500);
                harness.Down(2, 7510);
                harness.Up(2, 7520);
                harness.Up(12, 7530);
                Assert(harness.Actions.Count == 0, "taskbar chord cannot rebind to a new foreground between final validation and enqueue");
            }

        }

        private sealed class TriggerHarness : IDisposable
        {
            internal int Layer;
            internal IntPtr Foreground = new IntPtr(1);
            internal long Generation = 1;
            internal readonly List<string> Actions = new List<string>();
            internal Action<int, bool, long> PhysicalEdgeObserved;
            internal Action<string, int, long> TimerCallbackStarting;
            internal Action<string, int> ActionDispatchStarting;
            private readonly TriggerProcessor _processor;

            internal TriggerHarness(int layer)
            {
                Layer = layer;
                var hooks = new TriggerProcessorTestHooks();
                hooks.PhysicalEdgeObserved = delegate(int key, bool isDown, long token)
                {
                    Action<int, bool, long> callback = PhysicalEdgeObserved;
                    if (callback != null)
                        callback(key, isDown, token);
                };
                hooks.TimerCallbackStarting = delegate(string timerName, int key, long token)
                {
                    Action<string, int, long> callback = TimerCallbackStarting;
                    if (callback != null)
                        callback(timerName, key, token);
                };
                hooks.ActionDispatchStarting = delegate(string actionName, int key)
                {
                    Action<string, int> callback = ActionDispatchStarting;
                    if (callback != null)
                        callback(actionName, key);
                };
                _processor = new TriggerProcessor(
                    delegate { return Layer; },
                    delegate { return Foreground; },
                    delegate { return Generation; },
                    delegate(int actionLayer, int key, bool longPress, IntPtr expectedForeground, long expectedGeneration)
                    {
                        if (expectedForeground != Foreground || expectedGeneration != Generation)
                            return;
                        Actions.Add(string.Format(CultureInfo.InvariantCulture, "button:{0}:{1}:{2}", actionLayer, key, longPress));
                    },
                    delegate(int actionLayer, int encoder, EncoderGesture gesture, IntPtr expectedForeground, long expectedGeneration)
                    {
                        if (expectedForeground != Foreground || expectedGeneration != Generation)
                            return;
                        Actions.Add(string.Format(CultureInfo.InvariantCulture, "encoder:{0}:{1}:{2}", actionLayer, encoder, gesture));
                    },
                    delegate(int key, IntPtr expectedForeground, long expectedGeneration)
                    {
                        if (expectedForeground == Foreground && expectedGeneration == Generation)
                            Actions.Add("focus:" + key);
                    },
                    delegate(int key, IntPtr expectedForeground, long expectedGeneration)
                    {
                        if (expectedForeground == Foreground && expectedGeneration == Generation)
                            Actions.Add("terminal:" + key);
                    },
                    delegate(int slot, IntPtr expectedForeground, long expectedGeneration)
                    {
                        if (expectedForeground == Foreground && expectedGeneration == Generation)
                            Actions.Add("taskbar:" + slot);
                    },
                    hooks);
            }

            internal void Down(int key, long timestamp)
            {
                _processor.Process(new BridgeTrigger { Number = key, IsDown = true, Timestamp = timestamp });
            }

            internal void Up(int key, long timestamp)
            {
                _processor.Process(new BridgeTrigger { Number = key, IsDown = false, Timestamp = timestamp });
            }

            internal BridgeTrigger Publish(int key, bool isDown, long timestamp)
            {
                return new BridgeTrigger
                {
                    Number = key,
                    IsDown = isDown,
                    Timestamp = timestamp,
                    PhysicalPressToken = _processor.PublishPhysicalButtonEdge(key, isDown)
                };
            }

            internal void ProcessPublished(BridgeTrigger trigger)
            {
                _processor.ProcessPublished(trigger);
            }

            internal void InvalidatePhysicalLedger()
            {
                _processor.InvalidatePhysicalLedger();
            }

            internal void CancelBusinessInputs()
            {
                _processor.CancelBusinessInputs();
            }

            public void Dispose()
            {
                _processor.Dispose();
            }
        }

        private static ChatGptThreadSlot TestThread(string id, long recency)
        {
            return new ChatGptThreadSlot
            {
                Id = id,
                Title = id,
                RecencyAtMilliseconds = recency,
                RolloutPath = string.Empty
            };
        }

        private static int Status()
        {
            ForegroundInfo foreground = ForegroundResolver.Capture();
            Console.WriteLine(foreground);
            Console.WriteLine("Automatic logical layer: L" + foreground.AutomaticLayer + " " + BridgeDefinitions.GetLayer(foreground.AutomaticLayer).Name);
            return 0;
        }

        private static int RawInputProbe(string[] args)
        {
            int seconds = 20;
            if (args.Length > 1 && (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > 300))
                throw new ArgumentException("Raw Input probe duration must be 1..300 seconds.");

            var buttonMakes = new int[12];
            var buttonBreaks = new int[12];
            var encoderMakes = new int[12];
            var encoderBreaks = new int[12];
            var buttonSources = new string[12];
            var encoderSources = new string[12];
            object gate = new object();
            using (var raw = new RawKeyboardInput(null, delegate(RawInputDiagnosticEvent item)
            {
                Console.WriteLine(item);
                if (item.VirtualKey >= 0x7C && item.VirtualKey <= 0x87)
                {
                    int index = item.VirtualKey - 0x7C;
                    lock (gate)
                    {
                        if (item.IsEncoder)
                        {
                            if (item.IsBreak)
                                encoderBreaks[index]++;
                            else if (!string.Equals(item.Detail, "repeat-make", StringComparison.Ordinal))
                                encoderMakes[index]++;
                            if (encoderSources[index] == null)
                                encoderSources[index] = item.DeviceName;
                            else if (!string.Equals(encoderSources[index], item.DeviceName, StringComparison.OrdinalIgnoreCase))
                                encoderSources[index] = "<multiple>";
                        }
                        else if (item.Detail != "right-control")
                        {
                            if (item.IsBreak)
                                buttonBreaks[index]++;
                            else if (!string.Equals(item.Detail, "repeat-make", StringComparison.Ordinal))
                                buttonMakes[index]++;
                            if (buttonSources[index] == null)
                                buttonSources[index] = item.DeviceName;
                            else if (!string.Equals(buttonSources[index], item.DeviceName, StringComparison.OrdinalIgnoreCase))
                                buttonSources[index] = "<multiple>";
                        }
                    }
                }
            }))
            using (var hook = new KeyboardHook())
            {
                raw.Start();
                if (!raw.IsDeviceAvailable)
                    throw new InvalidOperationException("No unique CXT 12E4 Raw Input keyboard interface is available.");
                hook.Install();
                Console.WriteLine("RAW INPUT PROBE READY");
                Console.WriteLine(raw.SelectedDeviceName);
                Console.WriteLine("The suppression hook is returning 1 for physical F13-F24 while this probe records WM_INPUT.");
                Console.WriteLine("Press physical keys 1..12 once, then exercise K1..K4 CCW/press/CW exactly once each. Waiting " + seconds + " seconds...");
                Thread.Sleep(seconds * 1000);
            }

            int buttonComplete = 0;
            int encoderComplete = 0;
            for (int i = 0; i < 12; i++)
            {
                bool buttonOk = buttonMakes[i] == 1 && buttonBreaks[i] == 1 && buttonSources[i] != "<multiple>";
                bool encoderOk = encoderMakes[i] == 1 && encoderBreaks[i] == 1 && encoderSources[i] != "<multiple>";
                if (buttonOk) buttonComplete++;
                if (encoderOk) encoderComplete++;
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "F{0}: button={1}/{2} source={3} {4}; encoder={5}/{6} source={7} {8}",
                    i + 13,
                    buttonMakes[i],
                    buttonBreaks[i],
                    buttonSources[i] ?? "<none>",
                    buttonOk ? "PASS" : "FAIL",
                    encoderMakes[i],
                    encoderBreaks[i],
                    encoderSources[i] ?? "<none>",
                    encoderOk ? "PASS" : "FAIL"));
            }
            Console.WriteLine("Raw Input unique physical button pairs: " + buttonComplete + "/12");
            Console.WriteLine("Raw Input unique encoder pairs: " + encoderComplete + "/12");
            return buttonComplete == 12 && encoderComplete == 12 ? 0 : 5;
        }

        private static int RawInputList()
        {
            List<string> devices = RawKeyboardInput.DescribeRawKeyboards();
            Console.WriteLine("Raw Input keyboard TLCs: " + devices.Count);
            for (int i = 0; i < devices.Count; i++)
                Console.WriteLine(devices[i]);
            return 0;
        }

        private static int ChatGptProbe(string[] args)
        {
            var automation = new ChatGptAutomation();
            Dictionary<string, object> result = automation.Probe();
            string path = GetOutputPath(args, "chatgpt-ui-probe.json");
            BackupService.WriteJson(path, result);
            Console.WriteLine("Read-only ChatGPT UI Automation probe completed.");
            Console.WriteLine(Path.GetFullPath(path));
            Console.WriteLine("No UI element was invoked.");
            return 0;
        }

        private static int ChatGptStatusProbe(string[] args)
        {
            List<ChatGptTaskStatusSlot> slots;
            using (var catalog = new HybridChatGptThreadCatalog())
            {
                var registry = new ChatGptTaskSlotRegistry(catalog.ReadRecent);
                slots = ChatGptTaskStatusReader.ReadSlots(registry.Snapshot());
            }
            var result = new Dictionary<string, object>();
            result["schemaVersion"] = 1;
            result["capturedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            result["source"] = "Codex App Server thread/list plus shared rollout event log, with local read-only fallback";
            result["writesSent"] = false;
            result["keyboardWritesSent"] = false;
            result["codexHomes"] = ChatGptThreadCatalog.CodexHomeCandidatesForProbe();
            var described = new List<object>();
            for (int i = 0; i < slots.Count; i++)
            {
                described.Add(new Dictionary<string, object>
                {
                    { "slot", slots[i].Slot },
                    { "threadId", slots[i].ThreadId },
                    { "title", slots[i].Title },
                    { "state", slots[i].State.ToString() },
                    { "evidence", slots[i].Evidence },
                    { "recencyAtMs", slots[i].RecencyAtMilliseconds },
                    { "rolloutPath", slots[i].RolloutPath },
                    { "rolloutAvailable", ChatGptThreadCatalog.RolloutFileExists(slots[i].RolloutPath) }
                });
            }
            result["slots"] = described;
            string path = GetOutputPath(args, "chatgpt-six-slot-status-" + Stamp() + ".json");
            BackupService.WriteJson(path, result);
            Console.WriteLine("Read-only six-slot ChatGPT status probe completed.");
            Console.WriteLine(Path.GetFullPath(path));
            Console.WriteLine("No UI, keyboard, HID, or firmware write was sent.");
            return 0;
        }

        private static int TmuxSessionProbe()
        {
            IList<TerminalPaneSnapshot> panes = TmuxSessionClient.ReadPanes();
            string statusDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cxt1024Bridge",
                "terminal-status");
            IList<TerminalSessionStatusSlot> slots = TerminalSixSessionStatusMonitor.ReadSlots(statusDirectory, panes);
            for (int i = 0; i < slots.Count; i++)
            {
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: {1} {2} {3}",
                    slots[i].Slot,
                    slots[i].Present ? "present" : "off",
                    slots[i].State,
                    slots[i].SessionName ?? string.Empty));
            }
            Console.WriteLine("Read-only tmux inventory probe completed; no client was switched and no input/HID write was sent.");
            return 0;
        }

        private static int CodexAppServerProbe(string[] args)
        {
            List<ChatGptThreadSlot> official;
            string binaryPath;
            using (var appServer = new CodexAppServerThreadCatalog())
            {
                official = appServer.ReadRecent(12);
                binaryPath = appServer.BinaryPath;
            }
            List<ChatGptThreadSlot> local = ChatGptThreadCatalog.ReadRecent(12);
            var localById = new Dictionary<string, ChatGptThreadSlot>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < local.Count; i++)
                localById[local[i].Id] = local[i];

            int matches = 0;
            bool hasLoadedRuntime = false;
            var described = new List<object>();
            for (int i = 0; i < official.Count; i++)
            {
                ChatGptThreadSlot localThread;
                bool matched = localById.TryGetValue(official[i].Id, out localThread);
                if (matched)
                    matches++;
                if (!string.IsNullOrWhiteSpace(official[i].RuntimeStatus) &&
                    !string.Equals(official[i].RuntimeStatus, "notLoaded", StringComparison.OrdinalIgnoreCase))
                    hasLoadedRuntime = true;
                described.Add(new Dictionary<string, object>
                {
                    { "position", i + 1 },
                    { "threadId", official[i].Id },
                    { "title", official[i].Title },
                    { "recencyAtMs", official[i].RecencyAtMilliseconds },
                    { "runtimeStatus", official[i].RuntimeStatus },
                    { "rolloutAvailable", ChatGptThreadCatalog.RolloutFileExists(official[i].RolloutPath) },
                    { "matchesLocalCatalog", matched },
                    { "sameRecency", matched && localThread.RecencyAtMilliseconds == official[i].RecencyAtMilliseconds }
                });
            }

            var result = new Dictionary<string, object>();
            result["schemaVersion"] = 1;
            result["capturedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            result["codexBinary"] = binaryPath;
            result["transport"] = "stdio JSONL";
            result["method"] = "thread/list";
            result["writesSent"] = false;
            result["officialCount"] = official.Count;
            result["localCount"] = local.Count;
            result["matchingThreadIds"] = matches;
            result["desktopRuntimeAttached"] = hasLoadedRuntime;
            result["statusPolicy"] = hasLoadedRuntime
                ? "App Server runtime status is available for at least one loaded thread."
                : "The independent App Server sees desktop threads as notLoaded; live LEDs must retain the shared rollout-log source.";
            result["threads"] = described;

            string path = GetOutputPath(args, "codex-app-server-probe-" + Stamp() + ".json");
            BackupService.WriteJson(path, result);
            Console.WriteLine("Read-only Codex App Server compatibility probe completed.");
            Console.WriteLine(Path.GetFullPath(path));
            Console.WriteLine("Matched " + matches + " of " + official.Count + " official thread/list entries against the local catalog.");
            Console.WriteLine("No thread was started, resumed, forked, or modified; no UI, keyboard, or HID write was sent.");
            return 0;
        }

        private static int ChatGptDeepLinkProbe()
        {
            IDataObject original = Clipboard.GetDataObject();
            DataObject backup = CloneClipboard(original);
            if (original != null && backup == null)
                throw new InvalidOperationException("Clipboard could not be backed up safely; no shortcut was sent.");

            string deepLink = null;
            try
            {
                var automation = new ChatGptAutomation();
                if (!automation.FocusWindow())
                    throw new InvalidOperationException("ChatGPT window is unavailable.");
                Thread.Sleep(180);
                InputSender.Chord(Vk.L, Vk.LeftControl, Vk.LeftAlt);
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(100);
                    if (Clipboard.ContainsText())
                    {
                        string value = Clipboard.GetText().Trim();
                        if (value.StartsWith("codex://", StringComparison.OrdinalIgnoreCase))
                        {
                            deepLink = value;
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (backup == null)
                    Clipboard.Clear();
                else
                    Clipboard.SetDataObject(backup, true);
            }

            if (string.IsNullOrWhiteSpace(deepLink))
                throw new InvalidOperationException("ChatGPT did not place a codex:// deep link on the clipboard.");
            Console.WriteLine(deepLink);
            Console.WriteLine("Clipboard restored.");
            return 0;
        }

        private static DataObject CloneClipboard(IDataObject source)
        {
            if (source == null)
                return null;
            try
            {
                var clone = new DataObject();
                string[] formats = source.GetFormats(false);
                for (int i = 0; i < formats.Length; i++)
                {
                    object data = source.GetData(formats[i], false);
                    if (data != null)
                        clone.SetData(formats[i], false, data);
                }
                return clone;
            }
            catch
            {
                return null;
            }
        }

        private static int VerifyKeyboard()
        {
            string detail;
            bool valid = KeyboardConfiguration.Verify(RequireDevice(), out detail);
            Console.WriteLine(detail);
            return valid ? 0 : 5;
        }

        private static int LightingPreview(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException("--lighting-preview requires a layer number from 1 to 8.");
            int displayLayer;
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out displayLayer) || displayLayer < 1 || displayLayer > 8)
                throw new ArgumentException("Layer must be from 1 to 8.");
            int layer = BridgeDefinitions.ToInternalLayer(displayLayer);
            HidDeviceInfo device = RequireDevice();
            using (var via = new ViaProtocol(device))
            {
                via.SetBacklightValue(ViaProtocol.QmkRgbEffect, 1);
                LayerDefinition definition = BridgeDefinitions.GetLayer(layer);
                byte hue;
                byte saturation;
                BridgeDefinitions.RgbToQmkHsv(definition.Red, definition.Green, definition.Blue, out hue, out saturation);
                via.SetBacklightValue(ViaProtocol.QmkRgbColor, hue, saturation);
                via.SetBacklightValue(ViaProtocol.QmkRgbBrightness, 255);
            }
            Console.WriteLine("Volatile lighting preview applied for L" + displayLayer + ". EEPROM was not saved.");
            return 0;
        }

        private static int ConfigureKeyboard(string[] args)
        {
            string approvedBackup = GetRequiredArgument(args, "--backup");
            string approvedHash = GetRequiredArgument(args, "--sha256");
            KeyboardConfiguration.ValidateBackup(approvedBackup, approvedHash);

            HidDeviceInfo device = RequireDevice();
            string immediateBackup = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "backups",
                "cxt1024-immediate-prewrite-" + Stamp() + ".json");
            BackupService.WriteJson(immediateBackup, BackupService.Probe(device, true));
            string immediateHash = KeyboardConfiguration.ComputeSha256(immediateBackup);

            Dictionary<string, object> report = KeyboardConfiguration.Configure(device);
            report["approvedBackup"] = Path.GetFullPath(approvedBackup);
            report["approvedBackupSha256"] = approvedHash.ToUpperInvariant();
            report["immediatePreWriteBackup"] = Path.GetFullPath(immediateBackup);
            report["immediatePreWriteBackupSha256"] = immediateHash;
            string reportPath = GetOutputPath(args, "cxt1024-configuration-report-" + Stamp() + ".json");
            BackupService.WriteJson(reportPath, report);
            Console.WriteLine("Keyboard trigger keymap written and read back successfully.");
            Console.WriteLine("Immediate backup: " + Path.GetFullPath(immediateBackup));
            Console.WriteLine("Immediate backup SHA-256: " + immediateHash);
            Console.WriteLine("Report: " + Path.GetFullPath(reportPath));
            Console.WriteLine("No firmware flash command was sent; macros were not changed.");
            return 0;
        }

        private static HidDeviceInfo RequireDevice()
        {
            HidDeviceInfo device = HidEnumerator.FindCxt1024Raw();
            if (device == null)
                throw new InvalidOperationException("CXT 12E4 raw VIA HID interface was not found.");
            return device;
        }

        private static string GetOutputPath(string[] args, string defaultName)
        {
            string value = GetArgument(args, "--out");
            return string.IsNullOrEmpty(value)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups", defaultName)
                : value;
        }

        private static string GetRequiredArgument(string[] args, string name)
        {
            string value = GetArgument(args, name);
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("Missing required argument " + name + ".");
            return value;
        }

        private static string GetArgument(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Self-test failed: " + name + ".");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("CXT 12E4 Bridge");
            Console.WriteLine("  --run                          run the tray bridge (default)");
            Console.WriteLine("  --exit                         gracefully stop the running bridge");
            Console.WriteLine("  --watchdog PATH                keep the installed bridge running");
            Console.WriteLine("  --watchdog-once PATH           start the bridge if missing, then exit");
            Console.WriteLine("  --watchdog-exit                gracefully stop the running watchdog");
            Console.WriteLine("  --status                       show the foreground auto-layer decision");
            Console.WriteLine("  --dump-default-profiles        export both devices' eight default layers; no input/HID writes");
            Console.WriteLine("  --chatgpt-probe [--out PATH]  inspect task slots without invoking the UI");
            Console.WriteLine("  --codex-model-controls [inspect|controls|models|k1|k2|up|down]  scoped inspection or explicit live encoder action");
            Console.WriteLine("  --chatgpt-status-probe        read six task states without UI/HID writes");
            Console.WriteLine("  --tmux-session-probe          read live tmux slots without switching a client");
            Console.WriteLine("  --codex-app-server-probe      compare official thread/list with local task catalog");
            Console.WriteLine("  --dry-run [--out PATH]         export the complete plan; sends no input/write");
            Console.WriteLine("  --self-test                    pure software tests; sends no input/write");
            Console.WriteLine("  --raw-input-probe [SEC]        verify physical F13-F24 WM_INPUT while the suppression hook returns 1");
            Console.WriteLine("  --raw-input-list               list Raw Input keyboard TLC identities; read-only");
            Console.WriteLine("  --list                         list matching HID interfaces");
            Console.WriteLine("  --firmware-probe               verify custom Raw HID ping/response only");
            Console.WriteLine("  --task-light-test-white [SEC]  temporary six-key all-white isolation test");
            Console.WriteLine("  --all-light-test-white [SEC]   temporary all-12-key white LED test");
            Console.WriteLine("  --task-light-test SLOT STATE  temporary single-task LED isolation test");
            Console.WriteLine("  --rgb-test KEY R G B [SEC]     temporary raw per-key RGB diagnostic");
            Console.WriteLine("  --rgb-compare K1 K2 R1 G1 B1 [SEC R2 G2 B2] compare two LEDs");
            Console.WriteLine("  --layer-palette-preview        show candidate L1-L7 colors in sequence");
            Console.WriteLine("  --enter-custom-bootloader     enter DFU without unplugging next time");
            Console.WriteLine("  --probe [--out PATH]           read protocol metadata only");
            Console.WriteLine("  --backup [--out PATH]          read keymap, macros, and lighting into JSON");
            Console.WriteLine("  --readonly-capability-probe    read all keyboard/lighting value IDs; never writes");
            Console.WriteLine("  --verify-keyboard              read and verify the trigger keymap");
            Console.WriteLine("  --lighting-preview 1..8        apply a volatile layer color; does not save EEPROM");
            Console.WriteLine("  --enter-factory-bootloader --confirm CXT12E4-REV8");
            Console.WriteLine("                                 enter Rev.8 Atmel DFU; never erases or flashes");
            Console.WriteLine("  --configure-keyboard --backup PATH --sha256 HASH [--out PATH]");
            Console.WriteLine("                                 guarded keymap write after validated backup");
            Console.WriteLine("This program contains no firmware erase/flash command and does not require KBT.");
        }
    }
}
