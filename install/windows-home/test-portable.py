#!/usr/bin/env python3
"""Run Windows profile/controller state tests in an isolated .NET 8 harness.

This is supplemental verification, not the Windows --self-test. Production
source files are compiled unchanged. The .NET Framework-only JSON API uses a
test-only System.Text.Json adapter; Windows JSON, UI and input APIs are not run.
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape


ROOT = Path(__file__).resolve().parents[2]
ADAPTER = r'''
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace System.Web.Script.Serialization
{
    // Portable harness only. Never compiled into the Windows Bridge.
    internal sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }
        public int RecursionLimit { get; set; }
        private JsonSerializerOptions Options()
        { return new JsonSerializerOptions { MaxDepth = RecursionLimit, PropertyNameCaseInsensitive = false }; }
        private void Check(string text)
        { if (text == null || text.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds harness length limit"); }
        public string Serialize(object value)
        { string text = JsonSerializer.Serialize(value, Options()); Check(text); return text; }
        public T Deserialize<T>(string text)
        { Check(text); return JsonSerializer.Deserialize<T>(text, Options()); }
        public object DeserializeObject(string text)
        {
            Check(text);
            using (JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = RecursionLimit }))
                return ConvertValue(document.RootElement);
        }
        private object ConvertValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject()) result[property.Name] = ConvertValue(property.Value);
                    return result;
                case JsonValueKind.Array:
                    var items = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray()) items.Add(ConvertValue(item));
                    return items.ToArray();
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number:
                    int integer; return element.TryGetInt32(out integer) ? (object)integer : element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
'''

EXPORT = r'''
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
var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 64 };
File.WriteAllText(args[0], serializer.Serialize(result), new UTF8Encoding(false));
Console.WriteLine("Exported actual WindowsLayerProfiles defaults to " + args[0]);
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET_HOST_PATH") or shutil.which("dotnet"))
    parser.add_argument("--suite", choices=("all", "profiles", "controller"), default="all")
    parser.add_argument("--export-defaults", type=Path, help="After tests, export actual default bindings and labels as JSON (same shape as the Windows CLI)")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error("dotnet not found; pass the official .NET 8 SDK host with --dotnet")
    files = ["WindowsLayerProfiles.cs"]
    calls = []
    if args.suite in ("all", "profiles"):
        files.append("WindowsProfileTests.cs"); calls.append("WindowsProfileTests.Run();")
    if args.suite in ("all", "controller"):
        files += ["WindowsControllerEngine.cs", "WindowsControllerState.cs", "WindowsControllerTests.cs"]
        calls.append("WindowsControllerTests.Run();")
    source_root = ROOT / "src" / "Cxt1024Bridge"
    for name in files:
        if not (source_root / name).is_file():
            parser.error("Missing source: " + str(source_root / name))
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    with tempfile.TemporaryDirectory(prefix="cxt-portable-tests-") as temporary:
        directory = Path(temporary)
        compile_items = [str(source_root / name) for name in files] + [str(directory / "Adapter.cs"), str(directory / "Runner.cs")]
        project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><LangVersion>5</LangVersion><EnableDefaultCompileItems>false</EnableDefaultCompileItems><GenerateAssemblyInfo>false</GenerateAssemblyInfo><ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup><ItemGroup>'
        project += "".join('<Compile Include="' + escape(path, {'"': '&quot;'}) + '" />' for path in compile_items)
        project += '</ItemGroup></Project>'
        (directory / "Harness.csproj").write_text(project, encoding="utf-8")
        (directory / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
        (directory / "Adapter.cs").write_text(ADAPTER, encoding="utf-8")
        runner = 'using System; using System.Collections.Generic; using System.IO; using System.Text; namespace Cxt1024Bridge { internal static class PortableRunner { private static int Main(string[] args) { try { ' + " ".join(calls) + (EXPORT if args.export_defaults else "") + ' Console.WriteLine("PORTABLE ONLY: System.Text.Json adapter; no Windows JSON, native UI, input or hardware acceptance."); return 0; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } } } }'
        (directory / "Runner.cs").write_text(runner, encoding="utf-8")
        print("Running unchanged state/profile source with a test-only JSON adapter.", flush=True)
        command = [args.dotnet, "run", "--project", str(directory / "Harness.csproj"), "--configuration", "Release", "--verbosity", "quiet"]
        if args.export_defaults:
            export_path = args.export_defaults.resolve()
            export_path.parent.mkdir(parents=True, exist_ok=True)
            command += ["--", str(export_path)]
        subprocess.run(command, cwd=str(directory), env=env, check=True)


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        print("Portable harness failed with exit code %s." % error.returncode, file=sys.stderr)
        sys.exit(1)
    except OSError as error:
        print("Portable harness failed: " + str(error), file=sys.stderr)
        sys.exit(1)
