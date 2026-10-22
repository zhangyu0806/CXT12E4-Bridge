using System;
using System.Collections.Generic;
using System.Globalization;

namespace Cxt1024Bridge
{
    internal sealed class LayerDefinition
    {
        internal int Number;
        internal string Name;
        internal byte Red;
        internal byte Green;
        internal byte Blue;
        internal int IndicatorKey;

        internal string HexColor
        {
            get
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "#{0:X2}{1:X2}{2:X2}",
                    Red,
                    Green,
                    Blue);
            }
        }
    }

    internal static class BridgeDefinitions
    {
        private static readonly LayerDefinition[] Layers =
        {
            Layer(0, "通用 / 浏览器", 0x70, 0x88, 0xB8, 8),
            Layer(1, "ChatGPT", 0xC0, 0x00, 0xC8, 1),
            Layer(2, "终端 / tmux", 0xFF, 0x14, 0x64, 2),
            Layer(3, "Notion", 0xFF, 0x60, 0x00, 3),
            Layer(4, "Obsidian", 0x80, 0x00, 0xFF, 4),
            Layer(5, "3D建模 / Blender", 0xFF, 0xDC, 0x00, 5),
            Layer(6, "Total Commander", 0x00, 0xC8, 0xFF, 6),
            Layer(7, "通讯", 0x00, 0xFF, 0x00, 7)
        };

        internal static LayerDefinition GetLayer(int number)
        {
            if (number < 0 || number >= Layers.Length)
                return Layers[0];
            return Layers[number];
        }

        internal static IList<LayerDefinition> AllLayers
        {
            get { return Layers; }
        }

        internal static int ToInternalLayer(int displayNumber)
        {
            if (displayNumber == 8)
                return 0;
            if (displayNumber >= 1 && displayNumber <= 7)
                return displayNumber;
            throw new ArgumentOutOfRangeException("displayNumber");
        }

        internal static void RgbToQmkHsv(byte red, byte green, byte blue, out byte hue, out byte saturation)
        {
            double r = red / 255.0;
            double g = green / 255.0;
            double b = blue / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;
            double h;

            if (delta == 0)
                h = 0;
            else if (max == r)
                h = 60.0 * (((g - b) / delta) % 6.0);
            else if (max == g)
                h = 60.0 * (((b - r) / delta) + 2.0);
            else
                h = 60.0 * (((r - g) / delta) + 4.0);

            if (h < 0)
                h += 360.0;
            double s = max == 0 ? 0 : delta / max;
            hue = (byte)Math.Round(h / 360.0 * 255.0);
            saturation = (byte)Math.Round(s * 255.0);
        }

        private static LayerDefinition Layer(int number, string name, byte red, byte green, byte blue, int indicatorKey)
        {
            return new LayerDefinition
            {
                Number = number == 0 ? 8 : number,
                Name = name,
                Red = red,
                Green = green,
                Blue = blue,
                IndicatorKey = indicatorKey
            };
        }
    }
}
