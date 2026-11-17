using CineCam.Director.Replay;

/// <summary>The open of a recording of another game build and the check of its part layout.</summary>
static class OldRecordingTests
{
    public static void Run(Action<string, Action> check, Action<bool, string> expect)
    {
        const string b5801 = "v2026.11.10.5801";
        const string b5901 = "v2026.11.16.5901";

        check("old recordings: the game builds parse and compare by date and revision", () =>
        {
            expect(ReplayBuild.TryParse(b5901, out ReplayBuild.Numbers n) && n.Year == 2026 && n.Month == 11 && n.Day == 16 && n.Revision == 5901 && n.Suffix == "",
                "parse 5901");
            expect(ReplayBuild.TryParse("2026.11.16.5901-beta+abc", out ReplayBuild.Numbers s) && s.Suffix == "beta", "no v, a suffix and metadata");
            expect(!ReplayBuild.TryParse("", out _) && !ReplayBuild.TryParse("v2026.11", out _) && !ReplayBuild.TryParse(null, out _), "bad texts");
            expect(ReplayBuild.Compare(b5801, b5901) == ReplayBuildAge.Older, "5801 is older than 5901");
            expect(ReplayBuild.Compare(b5901, b5801) == ReplayBuildAge.Newer, "5901 is newer than 5801");
            expect(ReplayBuild.Compare(b5901, b5901) == ReplayBuildAge.Same, "same build");
            expect(ReplayBuild.Compare("2026.11.16.5901", b5901) == ReplayBuildAge.Same, "same numbers without the v");
            // The month is a number, so September is older than November also where the text sorts the other way.
            expect(ReplayBuild.Compare("v2026.9.22.5482", "v2026.11.10.5801") == ReplayBuildAge.Older, "month as a number");
            expect(ReplayBuild.Compare("v2025.12.30.9999", "v2026.1.2.100") == ReplayBuildAge.Older, "year first");
            expect(ReplayBuild.Compare("v2026.11.16.5901-a", "v2026.11.16.5901-b") == ReplayBuildAge.Unknown, "suffix only");
            expect(ReplayBuild.Compare("v", b5901) == ReplayBuildAge.Unknown && ReplayBuild.Compare(b5801, "dev") == ReplayBuildAge.Unknown, "unparsable");
            expect(ReplayBuild.Short(b5801) == "5801" && ReplayBuild.Short("v2026.11.16.5901-rc1") == "5901-rc1"
                && ReplayBuild.Short("dev") == "dev" && ReplayBuild.Short("") == "-", "short texts");
        });

        check("old recordings: an older build opens only after a confirm, a later or unknown build never", () =>
        {
            expect(ReplayBuild.OpenBlock(b5901, b5901, false) == null, "same build opens");
            expect(ReplayBuild.OpenBlock(b5801, b5901, true) == null, "older build opens with a confirm");
            var older = ReplayBuild.OpenBlock(b5801, b5901, false);
            expect(older is { Code: "older_game_build" } o && o.Message.Contains(b5801) && o.Message.Contains(b5901) && o.Message.Contains("Confirm"),
                older?.Message ?? "no block");
            var newer = ReplayBuild.OpenBlock(b5901, b5801, true);
            expect(newer is { Code: "game_version_mismatch" } n && n.Message.Contains("later build") && n.Message.Contains("Update the game"),
                newer?.Message ?? "no block");
            expect(ReplayBuild.OpenBlock("v2026.11.16.5901-x", b5901, true) is { Code: "game_version_mismatch" }, "unknown order");
            expect(ReplayBuild.OpenBlock("", b5901, true) is { Code: "game_version_mismatch" } e && e.Message.Contains("(unknown build)"), "empty build");
            expect(ReplayBuild.OlderWarning(b5801, b5901).Contains(b5801) && ReplayBuild.OlderWarning(b5801, b5901).Contains("without its recorded state"),
                "warning text");
            expect(ReplayBuild.BuildLine(b5901, b5901) == $"Recorded on KSA {b5901}, the build of this game.", ReplayBuild.BuildLine(b5901, b5901));
            expect(ReplayBuild.BuildLine(b5801, b5901) == $"Recorded on KSA {b5801}. This game is {b5901}.", ReplayBuild.BuildLine(b5801, b5901));
        });

        check("old recordings: the part layout of a segment is checked against the decoded tree", () =>
        {
            ReplayChannel Channel(string kind) => new(kind, [0.0], [0f]);
            string[] kinds = [ReplayChannelKind.Wetness, ReplayChannelKind.Heat, ReplayChannelKind.ThinFilm];
            uint key = ReplayPartKey.Of([("a", "w"), ("a", "h"), ("a", "f")]);
            var segment = new ReplaySegment(0, "", []) { Channels = kinds.Select(Channel).ToList(), PartKey = key };
            expect(ReplayPartLayout.Mismatch(segment, kinds, key) == null, "same layout");
            expect(ReplayPartLayout.Mismatch(segment with { PartKey = null }, kinds, key + 1) == null, "no key checks only the kinds");
            expect(ReplayPartLayout.Mismatch(segment, kinds, key + 1) is { } parts && parts.Contains("parts"), "other parts");
            expect(ReplayPartLayout.Mismatch(segment, kinds[..2], key) is { } count && count.Contains("3 part channels") && count.Contains("give 2"),
                "a module less in this build");
            string[] swapped = [ReplayChannelKind.Wetness, ReplayChannelKind.Light, ReplayChannelKind.ThinFilm];
            expect(ReplayPartLayout.Mismatch(segment, swapped, key) is { } kind && kind.Contains("part channel 2"), "another kind");
            expect(ReplayPartLayout.Mismatch(segment with { Channels = null }, kinds, key) != null, "no channels");
        });

        check("old recordings: the engines of a segment fit the nozzles or show no plumes", () =>
        {
            ReplayNozzle burn = default;
            ReplaySample Sample(int nozzles) => new(0, default, new ReplayRotation(0, 0, 0, 1), Enumerable.Repeat(burn, nozzles).ToArray());
            expect(ReplayPartLayout.NozzlesFit([Sample(0), Sample(3), Sample(3)], 3), "fits, with samples without a burn");
            expect(!ReplayPartLayout.NozzlesFit([Sample(3), Sample(2)], 3), "a sample with other engines");
            expect(ReplayPartLayout.NozzlesFit([Sample(0)], 0) && ReplayPartLayout.NozzlesFit([], 5), "no engines");
        });

        check("old recordings: the report names what the replay left out", () =>
        {
            var fit = new ReplayFitReport { Segments = 4 };
            expect(fit.IsClean && fit.Text() == "", "nothing left out");
            fit.PartStateLeftOut = 2;
            fit.ExhaustLeftOut = 1;
            fit.ConnectorsDropped = 3;
            string text = fit.Text();
            expect(!fit.IsClean && text.Contains("2 of 4 vehicle segments show their parts without the recorded state")
                && text.Contains("1 of 4 vehicle segments show no engine plumes") && text.Contains("3 part connections") && text.EndsWith('.'), text);
        });

        check("old recordings: the list shows the build of each recording", () =>
        {
            var entry = new RecordingEntry("p", "Flight 2026-11-11 15-46-52 x", new DateTime(2026, 11, 11, 15, 46, 52), 2048,
                new RecordingHeader(2, b5801, "Sol", "Earth", "Rocket", 103.07), null);
            expect(RecordingList.Cells(entry)[2] == "5801", string.Join("|", RecordingList.Cells(entry)));
            expect(RecordingList.Cells(entry with { Header = null })[2] == "-", "unread");
            string json = "{\"Version\":2,\"Flight\":{\"GameVersion\":\"" + b5801 + "\",\"SystemId\":\"Sol\",\"BodyId\":\"Earth\",\"StartTime\":1,"
                + "\"Actors\":[{\"Id\":0,\"Name\":\"Rocket\",\"Segments\":[{\"Start\":0,\"Definition\":\"\",\"Samples\":[{\"Time\":2.5}]}]}],\"Markers\":[]}}";
            RecordingHeader header = RecordingHeaderReader.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
            expect(header.GameVersion == b5801 && ReplayBuild.Compare(header.GameVersion, b5901) == ReplayBuildAge.Older, header.GameVersion);
        });
    }
}
