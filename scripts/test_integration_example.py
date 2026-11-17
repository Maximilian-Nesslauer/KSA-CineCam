"""Compile the documented subscriber with the real API and test its overlay state."""

import argparse
from pathlib import Path
import re
import subprocess
from xml.sax.saxutils import quoteattr


CHECKS = r'''
using System.Reflection;
using CineCam.Api;

int count = 0;
void Check(string name, bool passed)
{
    if (!passed) throw new Exception(name);
    count++;
    Console.WriteLine("PASS " + name);
}

CineCamEvents.Reset();
Check("reflection subscription", CineCamLink.TryAttach());
CineCamEvents.RaiseReplayOpened("flight");
CineCamEvents.RaiseTakeStarted("take", "replay", true, 0, []);
Check("hidden before cue", !MyOverlay.Visible);
CineCamEvents.RaiseTakeEnded("paused");
Check("pause before cue stays hidden", !MyOverlay.Visible);
CineCamEvents.RaiseTakeStarted("take", "replay", true, 0, []);
CineCamEvents.RaiseCue("overlay-in", 1, 101);
Check("cue shows overlay", MyOverlay.Visible);
int intros = MyOverlay.Intros, outros = MyOverlay.Outros;
CineCamEvents.RaiseTakeEnded("paused");
Check("pause after cue stays visible", MyOverlay.Visible);
Check("pause has no animation edge", MyOverlay.Intros == intros && MyOverlay.Outros == outros);
CineCamEvents.RaiseTakeStarted("take", "replay", true, 5, ["overlay-in", "overlay-out", "OVERLAY-IN"]);
Check("last cue wins after seek", MyOverlay.Visible);
CineCamEvents.RaiseTakeEnded("seek");
CineCamEvents.RaiseTakeStarted("take", "replay", true, 0, []);
Check("backward seek clears overlay at next play", !MyOverlay.Visible);
CineCamEvents.RaiseTakeEnded("render starts");
CineCamEvents.RaiseTakeStarted("take", "render", true, 5, ["overlay-in"]);
Check("render starts from passed cues", MyOverlay.Visible);
CineCamEvents.RaiseTakeEnded("done");
CineCamEvents.RaiseReplayClosed();
Check("replay close hides overlay", !MyOverlay.Visible);
CineCamEvents.RaiseTakeStarted("take", "live", true, 0, []);
CineCamEvents.RaiseCue("overlay-in", 1, 101);
CineCamEvents.RaiseTakeEnded("done");
Check("live end hides overlay", !MyOverlay.Visible);
CineCamLink.Detach();
Check("detach removes every handler", typeof(CineCamEvents).GetEvents().All(e =>
    typeof(CineCamEvents).GetField(e.Name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) == null));
Console.WriteLine($"All {count} integration example checks passed.");

internal static class MyOverlay
{
    public static bool Visible { get; private set; }
    public static int Intros { get; private set; }
    public static int Outros { get; private set; }
    public static void PlayIntro() { Visible = true; Intros++; }
    public static void PlayOutro() { Visible = false; Outros++; }
    public static void SetVisible(bool visible) => Visible = visible;
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scratch", type=Path, required=True, help="Folder for the test project and build output")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    example = re.search(r"```csharp\s*\n(.*?)```", (root / "docs/integration.md").read_text(encoding="utf-8"), re.S)
    if example is None:
        raise ValueError("docs/integration.md has no C# example")
    scratch = args.scratch.resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    (scratch / "CineCamLink.cs").write_text(example.group(1), encoding="ascii")
    (scratch / "Program.cs").write_text(CHECKS, encoding="ascii")
    source = quoteattr(str(root / "CineCam/Api/CineCamEvents.cs"))
    (scratch / "Example.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup><Compile Include={source} Link="CineCamEvents.cs" /></ItemGroup>
</Project>
''', encoding="ascii")
    subprocess.run(["dotnet", "run", "--project", str(scratch / "Example.csproj"), "-c", "Release",
                    "-maxcpucount:4", "-p:DeployToMods=false"], check=True)


if __name__ == "__main__":
    main()
