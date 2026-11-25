using System.Runtime.CompilerServices;
using CineCam.Director;
using CineCam.Features.Takes;
using HarmonyLib;
using KSA;

namespace CineCam.Tests
{
    internal static class CheckpointLoadGuardTests
    {
        public static void Run(Action<string, Action> check, Action<bool, string> expect)
        {
            check("checkpoint guard: only 5949 requires the patch and unavailable protection fails closed", () =>
            {
                expect(CheckpointLoadGuard.IsAffectedBuild(5949) && !CheckpointLoadGuard.IsAffectedBuild(5901)
                    && !CheckpointLoadGuard.IsAffectedBuild(5950), "scope the workaround to the affected revision");
                var save = new UncompressedSave("checkpoint") { BeforeRequest = UnexpectedLoad };
                expect(CheckpointLoadGuard.Load(save, save.Application().Method, UnexpectedLoad) == CheckpointLoadGuard.Unavailable,
                    "do not call stock while the patch is unavailable");
                WithGuard(() => expect(CheckpointLoadGuard.Load(save, null, UnexpectedLoad) == CheckpointLoadGuard.Unavailable,
                    "do not call stock if the exact callback method cannot be verified"));
            });

            check("checkpoint guard: same-system and contained payloads still apply in stock", () => WithGuard(() =>
            {
                foreach (string system in new[] { "same", "contained" })
                {
                    var save = new UncompressedSave("checkpoint") { PayloadSystem = system };
                    int prepared = 0;
                    string? refusal = CheckpointLoadGuard.Load(save, save.Application().Method, () =>
                    {
                        expect(SaveSystemSwap.LastSystem == system, "prepare only inside the immediate stock application path");
                        prepared++;
                    });
                    expect(refusal == null && prepared == 1 && SaveSystemSwap.Applied == 1 && SaveSystemSwap.Pending == null,
                        "the exact checkpoint applies once and does not queue a swap");
                    SaveSystemSwap.Reset();
                }
            }));

            check("checkpoint guard: swap, missing, empty and unknown plans refuse before stock or reset preparation", () => WithGuard(() =>
            {
                foreach ((string system, string reason) in new[] { ("swap", "requires a swap"), ("missing", "not installed"),
                    ("", "does not identify"), ("unknown", "unknown load plan") })
                {
                    var flow = new ResetFlow();
                    flow.Begin("take", "checkpoint", true, 0, 0);
                    expect(flow.Step(new ResetInput(0, true, false), admitBeforeEndingTake: true).Action == ResetAction.LoadSave,
                        "admit the reset before ending the active take");
                    var save = new UncompressedSave("checkpoint") { PayloadSystem = system };
                    string? refusal = CheckpointLoadGuard.Load(save, save.Application().Method, UnexpectedLoad);
                    expect(refusal?.Contains(reason, StringComparison.Ordinal) == true, "report the actual refusal");
                    if (system == "swap")
                        expect(!refusal!.Contains("not installed", StringComparison.Ordinal), "an installed system is not missing");
                    expect(SaveSystemSwap.LastSystem == null && SaveSystemSwap.Applied == 0 && SaveSystemSwap.Pending == null,
                        "refusal never enters stock dispatch or applies a save");
                    flow.LoadRefused(refusal!);
                    expect(flow.Phase == ResetPhase.Failed && !flow.HoldsSim && flow.Step(new ResetInput(1, true, true)).Action == ResetAction.None,
                        "refusal fails immediately without a hold or take start");
                }
            }));

            check("checkpoint guard: only the exact stock delegate for the exact save consumes the request", () => WithGuard(() =>
            {
                var save = new UncompressedSave("checkpoint");
                var sameId = new UncompressedSave(save.Id);
                Action otherMethod = save.Apply;
                Action multicast = UnexpectedLoad;
                multicast += save.Application();
                save.BeforeRequest = () =>
                {
                    SaveSystemSwap.RequestLoad(save.Id, "swap", sameId.Application());
                    expect(ReferenceEquals(SaveSystemSwap.Pending?.Target, sameId), "a same-ID save still queues its stock swap");
                    SaveSystemSwap.RequestLoad(save.Id, "swap", otherMethod);
                    expect(SaveSystemSwap.Pending == otherMethod, "a different method on the exact save stays unguarded");
                    SaveSystemSwap.RequestLoad(save.Id, "swap", multicast);
                    expect(SaveSystemSwap.Pending == multicast, "a multicast delegate keeps all its targets");
                };
                expect(CheckpointLoadGuard.Load(save, save.Application().Method, () => { }) == null
                    && SaveSystemSwap.Applied == 1 && SaveSystemSwap.Pending == multicast,
                    "the exact request still applies and leaves unrelated pending work alone");
            }));

            check("checkpoint guard: exceptions propagate and the scope closes after failure or refusal", () => WithGuard(() =>
            {
                var save = new UncompressedSave("checkpoint");
                var failure = new IOException("test exception before stock dispatch");
                save.BeforeRequest = () => throw failure;
                Exception? observed = null;
                try { CheckpointLoadGuard.Load(save, save.Application().Method, UnexpectedLoad); }
                catch (IOException ex) { observed = ex; }
                expect(ReferenceEquals(failure, observed), "preserve the original exception");

                save.BeforeRequest = null;
                save.PayloadSystem = "swap";
                save.Load();
                expect(ReferenceEquals(SaveSystemSwap.Pending?.Target, save), "a direct stock load works after the exception");
                expect(CheckpointLoadGuard.Load(save, save.Application().Method, UnexpectedLoad) != null,
                    "the next guarded call still refuses the swap");
                SaveSystemSwap.Reset();
                save.Load();
                expect(ReferenceEquals(SaveSystemSwap.Pending?.Target, save), "a direct stock load also works after refusal");

                save.PayloadSystem = "same";
                save.Fail = true;
                observed = null;
                try { CheckpointLoadGuard.Load(save, save.Application().Method, () => { }); }
                catch (IOException ex) { observed = ex; }
                expect(observed?.Message == "test failure applying the save", "preserve an exception from stock apply");
                save.PayloadSystem = "swap";
                SaveSystemSwap.Reset();
                save.Load();
                expect(ReferenceEquals(SaveSystemSwap.Pending?.Target, save), "a direct stock load also works after an apply exception");
            }));
        }

        private static void UnexpectedLoad() => throw new InvalidOperationException("unexpected load or reset preparation");

        private static void WithGuard(Action exercise)
        {
            var harmony = new Harmony("cinecam.tests.checkpoint-load-guard");
            try
            {
                harmony.Patch(AccessTools.DeclaredMethod(typeof(SaveSystemSwap), nameof(SaveSystemSwap.RequestLoad)),
                    prefix: new HarmonyMethod(typeof(CheckpointLoadGuard), nameof(CheckpointLoadGuard.Prefix)));
                CheckpointLoadGuard.Available = true;
                exercise();
            }
            finally
            {
                harmony.UnpatchAll(harmony.Id);
                CheckpointLoadGuard.Available = false;
                SaveSystemSwap.Reset();
            }
        }
    }
}

namespace KSA
{
    internal sealed class VersionInfo
    {
        public static readonly VersionInfo Current = new();
        public int Revision => 5949;
    }

    internal enum SaveLoadPlan : byte { Load, LoadIntoRunning, Swap, NotInstalled }

    internal static class SaveSystemSwap
    {
        public static string? LastSystem { get; private set; }
        public static Action? Pending { get; private set; }
        public static int Applied { get; private set; }

        public static SaveLoadPlan PlanLoad(string systemId) => systemId switch
        {
            "same" or "" => SaveLoadPlan.Load,
            "contained" => SaveLoadPlan.LoadIntoRunning,
            "swap" => SaveLoadPlan.Swap,
            "missing" => SaveLoadPlan.NotInstalled,
            _ => (SaveLoadPlan)255,
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RequestLoad(string saveName, string savedSystemId, Action apply)
        {
            LastSystem = savedSystemId;
            switch (PlanLoad(savedSystemId))
            {
                case SaveLoadPlan.Swap:
                    Pending = apply;
                    break;
                case SaveLoadPlan.Load:
                case SaveLoadPlan.LoadIntoRunning:
                    apply();
                    Applied++;
                    break;
            }
        }

        public static void Reset()
        {
            LastSystem = null;
            Pending = null;
            Applied = 0;
        }
    }
}
