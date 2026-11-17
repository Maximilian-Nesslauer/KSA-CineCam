# Interface language

Open Settings and use Language / Sprache to select Auto, English or Deutsch.
The change applies at once, including text that was produced before the change.
The selection is saved as `language` in `CineCam/settings.json` in the active game profile.
The file values are `Auto`, `English` and `German`.
Auto uses the current .NET UI culture, with German for `de` cultures and English for all other cultures.

The language changes the CineCam window, its tabs and controls, the timeline, the recording strip, help, status, warnings and problem text.
Take names, shot names, vehicle and part names, cue names, event identifiers and paths keep their original text.
Console commands, console output, logs, JSON, CSV and local control files stay English.
An operating system or game exception can include technical detail in its original language.

# Language files and fallback

`CineCam/Localization/en.json` and `de.json` are embedded in `CineCam.dll`.
The release needs no separate language folder or satellite assembly.
Every message has an ID and an English fallback in code.
A missing German entry or an incompatible format uses the English fallback.
Format checks preserve argument indices, repeated arguments, alignment and number formats.
Arguments are formatted with the invariant culture.

`UiText` keeps the canonical English message and its arguments until a draw boundary calls `Render`.
Message metadata uses string references, so a user name that has the same content as a translated label does not become a label.
The metadata follows composition, case changes and the field-path parsing of a diagnostic.
Built-in event details read from recordings use a fixed set of templates, with captured values left unchanged.
Unknown event details remain protocol text.

Controls use their canonical English ID or an explicit ID.
Visible text is translated before text measurement, wrapping or clipping.
Direct ImGui controls use a `###` suffix to separate their translated label from their ID.
The stock segmented control uses its fixed ID and the segment index.

# Game localization support

In KSA build 2026.11.16.5901, `GameContentTags.Build` preloads `LanguageFile` assets and `LanguageFileReference.DoLoad` calls `Languages.AddOrAppend`.
These methods load and merge entries, but do not select a translation provider for `Brutal.Localization.LStrings`.
The inspected game source has no call to `SetLocalizedStringProvider` or `RemoveLocalizedStringProvider`, and `GameSettings` exposes no active language.
The stock language folder contains only `en-US.resx`.
CineCam therefore uses its own embedded table and an explicit language preference.
It does not replace the shared game provider.

# Checks

Run builds and tests with `-maxcpucount:4 -p:DeployToMods=false`.
The console tests cover fallback, formatting, names and identifiers, control identity, language changes, composed status, parsed diagnostics and stored event details.
Set `CINECAM_PACKAGE` to the Release ZIP to also inspect both embedded language resources in the packaged DLL.

Build `tools/Localization/Localization.csproj`, then run its DLL with the repository root as its only argument.
This read-only check rejects unregistered interface text and resource IDs without both language entries.
Its exclusions are canonical identifiers, file paths, units, language names and the CineCam brand.

`tools/LocalizationCheck` is a game test mod for an isolated profile.
It changes internal page selections without keyboard or mouse input and uses the native screenshot request.
Set `CINECAM_LOCALIZATION_EVIDENCE` to the output folder.
It saves the Quick page, every live Editor tab and Settings in both languages, then requests a normal game exit.
The fixture keeps the take name `Ready`, the shot name `Ignition` and the cue name `overlay-in` unchanged.
The driver is not part of the release archive.
