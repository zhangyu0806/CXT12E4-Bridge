using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    internal static class KeyboardConfiguration
    {
        internal const int Layers = 8;
        internal const int Rows = 4;
        internal const int Columns = 6;
        private const ushort F13 = 0x0068;
        private const ushort RightControlMods = 0x1100;

        internal static ushort[,,] BuildTriggerKeymap()
        {
            var result = new ushort[Layers, Rows, Columns];
            for (int layer = 0; layer < Layers; layer++)
            {
                // Physical keys 1-12, row-major.
                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 4; column++)
                        result[layer, row, column] = (ushort)(F13 + row * 4 + column);
                }

                // K1: CCW, press, CW => RCtrl+F13/F14/F15.
                result[layer, 0, 4] = ModifiedF(13);
                result[layer, 3, 2] = ModifiedF(14);
                result[layer, 0, 5] = ModifiedF(15);

                // K2: CCW, press, CW => RCtrl+F16/F17/F18.
                result[layer, 3, 4] = ModifiedF(16);
                result[layer, 3, 3] = ModifiedF(17);
                result[layer, 3, 5] = ModifiedF(18);

                // K3: CCW, press, CW => RCtrl+F19/F20/F21.
                result[layer, 1, 4] = ModifiedF(19);
                result[layer, 3, 1] = ModifiedF(20);
                result[layer, 1, 5] = ModifiedF(21);

                // K4: CCW, press, CW => RCtrl+F22/F23/F24.
                result[layer, 2, 4] = ModifiedF(22);
                result[layer, 3, 0] = ModifiedF(23);
                result[layer, 2, 5] = ModifiedF(24);
            }
            return result;
        }

        internal static Dictionary<string, object> Describe()
        {
            ushort[,,] keymap = BuildTriggerKeymap();
            var layerList = new List<object>();
            for (int layer = 0; layer < Layers; layer++)
            {
                var rowList = new List<object>();
                for (int row = 0; row < Rows; row++)
                {
                    var values = new List<object>();
                    for (int column = 0; column < Columns; column++)
                        values.Add("0x" + keymap[layer, row, column].ToString("X4", CultureInfo.InvariantCulture));
                    rowList.Add(values);
                }
                layerList.Add(new Dictionary<string, object>
                {
                    { "layer", layer },
                    { "matrix", rowList }
                });
            }

            return new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "writesSent", false },
                { "purpose", "CXT 12E4 private bridge trigger keymap" },
                { "buttonTriggers", "F13-F24" },
                { "encoderTriggers", "RCtrl+F13-F24, grouped CCW/press/CW" },
                { "layers", layerList }
            };
        }

        internal static void ValidateBackup(string path, string expectedSha256)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("The required pre-write keyboard backup does not exist.", path);
            string actualHash = ComputeSha256(path);
            if (!string.Equals(actualHash, expectedSha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backup SHA-256 mismatch. Expected " + expectedSha256 + " but found " + actualHash + ".");

            var serializer = new JavaScriptSerializer();
            object parsed = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            var root = parsed as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("writesSent") || !Equals(root["writesSent"], false))
                throw new InvalidOperationException("The supplied file is not a read-only CXT 12E4 backup.");
            var device = root.ContainsKey("device") ? root["device"] as Dictionary<string, object> : null;
            if (device == null ||
                !string.Equals(Convert.ToString(device["vendorId"], CultureInfo.InvariantCulture), "0x5754", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToString(device["productId"], CultureInfo.InvariantCulture), "0xC401", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The supplied backup belongs to a different keyboard.");
        }

        internal static Dictionary<string, object> Configure(HidDeviceInfo device)
        {
            ushort[,,] desired = BuildTriggerKeymap();
            var report = new Dictionary<string, object>();
            report["schemaVersion"] = 1;
            report["configuredAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report["device"] = device.ToString();

            using (var via = new ViaProtocol(device))
            {
                int protocol = via.GetProtocolVersion();
                int layers = via.GetLayerCount(protocol);
                if (layers != Layers)
                    throw new InvalidOperationException("Expected 8 layers but the keyboard reported " + layers + ".");
                via.WriteAllLayers(desired, protocol);

                for (int layer = 0; layer < Layers; layer++)
                {
                    ushort[,] actual = via.ReadLayer(layer, Rows, Columns, protocol);
                    for (int row = 0; row < Rows; row++)
                    {
                        for (int column = 0; column < Columns; column++)
                        {
                            if (actual[row, column] != desired[layer, row, column])
                            {
                                throw new InvalidOperationException(string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Keymap verification failed at L{0} R{1} C{2}: expected 0x{3:X4}, read 0x{4:X4}.",
                                    layer,
                                    row,
                                    column,
                                    desired[layer, row, column],
                                    actual[row, column]));
                            }
                        }
                    }
                }

                // Persist a safe power-on baseline: static mode, hardware index 0 off.
                // User-facing L8 runtime color starts when the Bridge reconnects.
                via.SetBacklightValue(ViaProtocol.QmkRgbEffect, 1);
                via.SetBacklightValue(ViaProtocol.QmkRgbColor, 0, 0);
                via.SetBacklightValue(ViaProtocol.QmkRgbBrightness, 0);
                via.SaveLighting();
                report["protocolVersion"] = protocol;
            }

            report["keymapVerified"] = true;
            report["lightingBaseline"] = "static mode, brightness 0 (hardware index 0 off; user-facing L8 resumes at runtime)";
            report["macrosChanged"] = false;
            report["firmwareFlashed"] = false;
            report["writesSent"] = true;
            return report;
        }

        internal static bool Verify(HidDeviceInfo device, out string detail)
        {
            ushort[,,] desired = BuildTriggerKeymap();
            using (var via = new ViaProtocol(device))
            {
                int protocol = via.GetProtocolVersion();
                if (via.GetLayerCount(protocol) != Layers)
                {
                    detail = "Layer count is not 8.";
                    return false;
                }
                for (int layer = 0; layer < Layers; layer++)
                {
                    ushort[,] actual = via.ReadLayer(layer, Rows, Columns, protocol);
                    for (int row = 0; row < Rows; row++)
                    {
                        for (int column = 0; column < Columns; column++)
                        {
                            if (actual[row, column] != desired[layer, row, column])
                            {
                                detail = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Mismatch at L{0} R{1} C{2}.",
                                    layer,
                                    row,
                                    column);
                                return false;
                            }
                        }
                    }
                }
            }
            detail = "All 192 matrix entries match the bridge trigger keymap.";
            return true;
        }

        internal static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(stream);
                var text = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                    text.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static ushort ModifiedF(int functionNumber)
        {
            return (ushort)(RightControlMods | (F13 + functionNumber - 13));
        }
    }
}
