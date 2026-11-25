using System.Runtime.CompilerServices;
using CineCam.Features.Takes;
using HarmonyLib;

namespace CineCam.Tests
{
    internal static class SaveLoadWatchTests
    {
        public static void Run(Action<string, Action> check, Action<bool, string> expect)
        {
            check("checkpoint watch: immediate and deferred applications count for the requested save", () =>
            {
                WithWatch(() =>
                {
                    var requested = new KSA.UncompressedSave("checkpoint");
                    var other = new KSA.UncompressedSave("other");
                    requested.Apply();
                    int before = SaveLoadWatch.Completed;
                    expect(before == 1 && !SaveLoadWatch.AppliedSince(requested.Id, before), "a previous load is stale");

                    Action deferred = requested.Application();
                    expect(SaveLoadWatch.Completed == before, "queuing the callback does not count");
                    other.Apply();
                    expect(SaveLoadWatch.Completed == before + 1, "other saves still advance the global count");
                    expect(!SaveLoadWatch.AppliedSince(requested.Id, before), "the wrong save does not complete the request");

                    deferred();
                    expect(SaveLoadWatch.AppliedSince(requested.Id, before), "the deferred call completes the request");
                    other.Apply();
                    expect(!SaveLoadWatch.AppliedSince(requested.Id, before), "the latest application owns the world");
                    requested.Apply();
                    expect(SaveLoadWatch.AppliedSince(requested.Id, before), "the immediate call completes the request");
                });
            });

            check("checkpoint watch: refused, skipped and throwing applications do not count", () =>
            {
                WithWatch(() =>
                {
                    var requested = new KSA.UncompressedSave("checkpoint");
                    int before = SaveLoadWatch.Completed;
                    _ = requested.Application();
                    expect(!SaveLoadWatch.AppliedSince(requested.Id, before), "stock can decline the callback");

                    requested.Skip = true;
                    requested.Apply();
                    expect(SaveLoadWatch.Completed == before, "a prefix skipped application");

                    bool threw = false;
                    requested.Skip = false;
                    requested.Fail = true;
                    try { requested.Apply(); }
                    catch (IOException) { threw = true; }
                    expect(threw && SaveLoadWatch.Completed == before, "a failed application leaves the count unchanged");
                });
            });
        }

        private static void WithWatch(Action exercise)
        {
            var harmony = new Harmony("cinecam.tests.save-load-watch");
            try
            {
                harmony.Patch(AccessTools.DeclaredMethod(typeof(KSA.UncompressedSave), "ApplyLoaded", Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(SaveLoadWatchTests), nameof(AllowApplication)),
                    postfix: new HarmonyMethod(typeof(SaveLoadWatch), nameof(SaveLoadWatch.Postfix)));
                exercise();
            }
            finally
            {
                harmony.UnpatchAll(harmony.Id);
                SaveLoadWatch.Reset();
            }
        }

        private static bool AllowApplication(KSA.UncompressedSave __instance) => !__instance.Skip;
    }
}

namespace KSA
{
    internal sealed class UncompressedSave(string id)
    {
        public string Id { get; } = id;
        public bool Skip { get; set; }
        public bool Fail { get; set; }

        public Action Application() => ApplyLoaded;
        public void Apply() => ApplyLoaded();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyLoaded()
        {
            if (Fail)
                throw new IOException("test failure applying the save");
        }
    }
}
