using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    internal static class BackupService
    {
        private const int Rows = 4;
        private const int Columns = 6;

        internal static Dictionary<string, object> Probe(HidDeviceInfo device, bool includeCompleteBackup)
        {
            var root = new Dictionary<string, object>();
            root["schemaVersion"] = 1;
            root["capturedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            root["mode"] = includeCompleteBackup ? "read-only-backup" : "read-only-probe";
            root["writesSent"] = false;
            root["device"] = DeviceRecord(device);

            using (var via = new ViaProtocol(device))
            {
                int capabilityC = via.GetCapabilityC();
                int capabilityX = via.GetCapabilityX();
                int capabilityT = via.GetCapabilityT();
                int protocol = via.GetProtocolVersion();
                int layerCount = via.GetLayerCount(protocol);

                root["protocol"] = new Dictionary<string, object>
                {
                    { "version", protocol },
                    { "capabilityC", capabilityC },
                    { "capabilityX", capabilityX },
                    { "capabilityT", capabilityT },
                    { "layerCount", layerCount },
                    { "rows", Rows },
                    { "columns", Columns }
                };

                if (!includeCompleteBackup)
                    return root;

                var layers = new List<object>();
                for (int layer = 0; layer < layerCount; layer++)
                {
                    ushort[,] matrix = via.ReadLayer(layer, Rows, Columns, protocol);
                    var rows = new List<object>();
                    for (int row = 0; row < Rows; row++)
                    {
                        var values = new List<object>();
                        for (int column = 0; column < Columns; column++)
                        {
                            ushort keycode = matrix[row, column];
                            values.Add(new Dictionary<string, object>
                            {
                                { "decimal", keycode },
                                { "hex", "0x" + keycode.ToString("X4", CultureInfo.InvariantCulture) }
                            });
                        }
                        rows.Add(values);
                    }
                    layers.Add(new Dictionary<string, object>
                    {
                        { "layer", layer },
                        { "matrix", rows }
                    });
                }
                root["layers"] = layers;

                try
                {
                    int macroCount = via.GetMacroCount();
                    int macroBufferSize = via.GetMacroBufferSize();
                    byte[] macroBytes = via.ReadMacroBuffer(macroBufferSize);
                    root["macros"] = new Dictionary<string, object>
                    {
                        { "count", macroCount },
                        { "bufferSize", macroBufferSize },
                        { "bytesBase64", Convert.ToBase64String(macroBytes) }
                    };
                }
                catch (Exception ex)
                {
                    root["macros"] = new Dictionary<string, object>
                    {
                        { "readError", ex.Message }
                    };
                }

                root["lighting"] = ReadLighting(via);
            }

            return root;
        }

        private static Dictionary<string, object> ReadLighting(ViaProtocol via)
        {
            var lighting = new Dictionary<string, object>();
            ReadLightingValue(via, lighting, "qmkRgbBrightness", ViaProtocol.QmkRgbBrightness, null);
            ReadLightingValue(via, lighting, "qmkRgbEffect", ViaProtocol.QmkRgbEffect, null);
            ReadLightingValue(via, lighting, "qmkRgbEffectSpeed", ViaProtocol.QmkRgbEffectSpeed, null);
            ReadLightingValue(via, lighting, "qmkRgbColor", ViaProtocol.QmkRgbColor, null);
            return lighting;
        }

        private static void ReadLightingValue(
            ViaProtocol via,
            Dictionary<string, object> target,
            string name,
            byte valueId,
            byte[] extra)
        {
            try
            {
                byte[] response = extra == null
                    ? via.GetBacklightValue(valueId)
                    : via.GetBacklightValue(valueId, extra);
                target[name] = Convert.ToBase64String(response);
            }
            catch (Exception ex)
            {
                target[name + "Error"] = ex.Message;
            }
        }

        private static Dictionary<string, object> DeviceRecord(HidDeviceInfo device)
        {
            return new Dictionary<string, object>
            {
                { "vendorId", "0x" + device.VendorId.ToString("X4", CultureInfo.InvariantCulture) },
                { "productId", "0x" + device.ProductId.ToString("X4", CultureInfo.InvariantCulture) },
                { "revision", "0x" + device.VersionNumber.ToString("X4", CultureInfo.InvariantCulture) },
                { "usagePage", "0x" + device.UsagePage.ToString("X4", CultureInfo.InvariantCulture) },
                { "usage", "0x" + device.Usage.ToString("X4", CultureInfo.InvariantCulture) },
                { "inputReportLength", device.InputReportLength },
                { "outputReportLength", device.OutputReportLength },
                { "featureReportLength", device.FeatureReportLength },
                { "path", device.Path }
            };
        }

        internal static void WriteJson(string path, Dictionary<string, object> data)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            string json = serializer.Serialize(data);
            File.WriteAllText(path, PrettyJson.Format(json), new UTF8Encoding(false));
        }
    }

    internal static class PrettyJson
    {
        internal static string Format(string json)
        {
            var output = new StringBuilder(json.Length * 2);
            bool quoted = false;
            bool escaped = false;
            int depth = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (quoted)
                {
                    output.Append(c);
                    if (escaped)
                        escaped = false;
                    else if (c == '\\')
                        escaped = true;
                    else if (c == '"')
                        quoted = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        quoted = true;
                        output.Append(c);
                        break;
                    case '{':
                    case '[':
                        output.Append(c);
                        output.AppendLine();
                        depth++;
                        AppendIndent(output, depth);
                        break;
                    case '}':
                    case ']':
                        output.AppendLine();
                        depth--;
                        AppendIndent(output, depth);
                        output.Append(c);
                        break;
                    case ',':
                        output.Append(c);
                        output.AppendLine();
                        AppendIndent(output, depth);
                        break;
                    case ':':
                        output.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(c))
                            output.Append(c);
                        break;
                }
            }
            output.AppendLine();
            return output.ToString();
        }

        private static void AppendIndent(StringBuilder output, int depth)
        {
            output.Append(' ', depth * 2);
        }
    }
}
