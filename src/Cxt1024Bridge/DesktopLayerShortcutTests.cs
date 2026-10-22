using System;
using System.Reflection;

namespace Cxt1024Bridge
{
    // Pure mapping tests: these never call InputSender or inspect live windows.
    internal static class DesktopLayerShortcutTests
    {
        internal static void Run(Action<bool, string> assert)
        {
            if (assert == null)
                throw new ArgumentNullException("assert");

            const DesktopShortcutModifiers none = DesktopShortcutModifiers.None;
            const DesktopShortcutModifiers ctrl = DesktopShortcutModifiers.Control;
            const DesktopShortcutModifiers shift = DesktopShortcutModifiers.Shift;
            const DesktopShortcutModifiers alt = DesktopShortcutModifiers.Alt;

            ushort[] universalKeys = { 0x43, 0x56, 0x5A, 0x59, 0x58, 0x43, 0x56, 0x56, 0x54, 0x54, 0x46, 0 };
            DesktopShortcutModifiers[] universalModifiers =
            {
                ctrl | alt, ctrl | alt, ctrl, ctrl, ctrl, ctrl,
                ctrl, ctrl | shift, ctrl, ctrl | shift, ctrl, none
            };
            ushort[] blenderKeys = { 0x09, 0x45, 0x47, 0x52, 0x53, 0x49, 0x42, 0x44, 0x5A, 0x5A, 0x41, 0x72 };
            DesktopShortcutModifiers[] blenderModifiers =
            {
                none, none, none, none, none, none,
                ctrl, shift, ctrl, ctrl | shift, shift, none
            };

            for (int key = 1; key <= 12; key++)
            {
                Check(assert, DesktopLayerShortcuts.ForKey(0, key), universalKeys[key - 1],
                    universalModifiers[key - 1], key == 12, "L8 key " + key);
                Check(assert, DesktopLayerShortcuts.ForKey(5, key), blenderKeys[key - 1],
                    blenderModifiers[key - 1], false, "L5 key " + key);
                assert(object.ReferenceEquals(DesktopLayerShortcuts.ForKey(0, key), DesktopLayerShortcuts.ForKey(0, key)),
                    "L8 key " + key + " is a fixed context-free descriptor");
            }

            EncoderGesture[] gestures = { EncoderGesture.CounterClockwise, EncoderGesture.Press, EncoderGesture.Clockwise };
            ushort[,] universalEncoderKeys =
            {
                { 0x25, 0x4C, 0x27 },
                { 0x09, 0x57, 0x09 },
                { 0xBD, 0x30, 0xBB },
                { 0, 0x74, 0 }
            };
            DesktopShortcutModifiers[,] universalEncoderModifiers =
            {
                { alt, ctrl, alt },
                { ctrl | shift, ctrl, ctrl },
                { ctrl, ctrl, ctrl },
                { none, none, none }
            };
            ushort[,] blenderEncoderKeys =
            {
                { 0x64, 0x61, 0x66 },
                { 0x68, 0x67, 0x62 },
                { 0x25, 0x20, 0x27 },
                { 0, 0x6E, 0 }
            };
            for (int encoder = 1; encoder <= 4; encoder++)
            {
                for (int column = 0; column < gestures.Length; column++)
                {
                    string suffix = " K" + encoder + " " + gestures[column];
                    DesktopShortcut universal = DesktopLayerShortcuts.ForEncoder(0, encoder, gestures[column]);
                    DesktopShortcut blender = DesktopLayerShortcuts.ForEncoder(5, encoder, gestures[column]);
                    if (encoder == 4 && gestures[column] != EncoderGesture.Press)
                    {
                        assert(universal == null, "L8" + suffix + " remains universal accelerated wheel outside the profile");
                        assert(blender == null, "L5" + suffix + " remains universal accelerated wheel outside the profile");
                    }
                    else
                    {
                        Check(assert, universal, universalEncoderKeys[encoder - 1, column],
                            universalEncoderModifiers[encoder - 1, column], false, "L8" + suffix);
                        Check(assert, blender, blenderEncoderKeys[encoder - 1, column], none, false, "L5" + suffix);
                        assert(object.ReferenceEquals(universal, DesktopLayerShortcuts.ForEncoder(0, encoder, gestures[column])),
                            "L8" + suffix + " is a fixed context-free descriptor");
                    }
                }
            }

            int[] supportedLayers = { 0, 5 };
            int[] invalidKeys = { int.MinValue, -1, 0, 13, 100, int.MaxValue };
            int[] invalidEncoders = { int.MinValue, -1, 0, 5, 100, int.MaxValue };
            EncoderGesture[] invalidGestures =
            {
                (EncoderGesture)int.MinValue, (EncoderGesture)(-1),
                (EncoderGesture)3, (EncoderGesture)100, (EncoderGesture)int.MaxValue
            };
            foreach (int layer in supportedLayers)
            {
                foreach (int key in invalidKeys)
                    assert(DesktopLayerShortcuts.ForKey(layer, key) == null, "invalid key produces no action: " + layer + "/" + key);
                foreach (int encoder in invalidEncoders)
                {
                    foreach (EncoderGesture gesture in gestures)
                        assert(DesktopLayerShortcuts.ForEncoder(layer, encoder, gesture) == null,
                            "invalid encoder produces no action: " + layer + "/" + encoder + "/" + gesture);
                }
                for (int encoder = 1; encoder <= 4; encoder++)
                {
                    foreach (EncoderGesture gesture in invalidGestures)
                        assert(DesktopLayerShortcuts.ForEncoder(layer, encoder, gesture) == null,
                            "invalid gesture produces no action: " + layer + "/" + encoder + "/" + gesture);
                }
            }

            int[] unsupportedLayers = { int.MinValue, -1, 1, 2, 3, 4, 6, 7, 8, 9, int.MaxValue };
            foreach (int layer in unsupportedLayers)
            {
                for (int key = 1; key <= 12; key++)
                    assert(DesktopLayerShortcuts.ForKey(layer, key) == null, "profile does not own layer " + layer + " key " + key);
                for (int encoder = 1; encoder <= 4; encoder++)
                {
                    foreach (EncoderGesture gesture in gestures)
                        assert(DesktopLayerShortcuts.ForEncoder(layer, encoder, gesture) == null,
                            "profile does not own layer " + layer + " K" + encoder + " " + gesture);
                }
            }

            CheckImmutableSurface(assert);
        }

        private static void Check(Action<bool, string> assert, DesktopShortcut actual, ushort key,
            DesktopShortcutModifiers modifiers, bool copyQ, string name)
        {
            assert(actual != null, name + " has a descriptor");
            if (actual == null)
                return;
            assert(actual.Key == key, name + " virtual key");
            assert(actual.Modifiers == modifiers, name + " modifiers");
            assert(actual.IsCopyQ == copyQ, name + " CopyQ dispatch");
            assert(!string.IsNullOrEmpty(actual.Name), name + " readable command name");
        }

        private static void CheckImmutableSurface(Action<bool, string> assert)
        {
            BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic;
            Type descriptor = typeof(DesktopShortcut);
            assert(descriptor.IsSealed, "shared shortcut descriptor cannot be subclassed");
            foreach (FieldInfo field in descriptor.GetFields(all | BindingFlags.Instance))
            {
                assert(field.IsInitOnly, "descriptor field is readonly: " + field.Name);
                assert(!field.FieldType.IsArray, "descriptor does not expose an array: " + field.Name);
            }
            foreach (PropertyInfo property in descriptor.GetProperties(all | BindingFlags.Instance))
            {
                assert(!property.CanWrite, "descriptor property has no setter: " + property.Name);
                assert(!property.PropertyType.IsArray, "descriptor property is not an array: " + property.Name);
            }
            Type profile = typeof(DesktopLayerShortcuts);
            foreach (FieldInfo field in profile.GetFields(all | BindingFlags.Static))
            {
                if (field.FieldType.IsArray)
                    assert(field.IsPrivate && field.IsInitOnly, "profile table remains private and readonly: " + field.Name);
            }
            foreach (MethodInfo method in profile.GetMethods(all | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsPrivate)
                    continue;
                assert(!method.ReturnType.IsArray, "profile does not return mutable tables: " + method.Name);
                foreach (ParameterInfo parameter in method.GetParameters())
                    assert(parameter.ParameterType == typeof(int) || parameter.ParameterType == typeof(EncoderGesture),
                        "profile selection takes only physical mapping coordinates, never foreground context: " + method.Name);
            }
        }
    }
}
