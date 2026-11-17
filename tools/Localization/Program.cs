using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string root = Path.GetFullPath(args[0]);
var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "CineCam/Localization/en.json")))!;
var german = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "CineCam/Localization/de.json")))!;
var identities = new HashSet<string>(StringComparer.Ordinal)
{
    "CINECAM", "Language / Sprache", "English", "Deutsch", "Auto", "cinecam-window", "window-", "window-page-",
    "cinecam-tabs", "cinecam-pages", "cinecam-page", "problem-", "cinecam-source", "cinecam-replay-take-tabs", "cinecam-live-tabs",
    "live", "preRoll", "file-site", "control", "file-hideat", "take-hideat-", "file-aliases", "alias-new", "alias-target", "capture-folder",
    "events.csv", "replay-play", "replay-start", "replay-take-camera", "replay-free-camera", "camera", "speed", "actions", "events",
    "recording-", "take-file", "file-path", "take-", "-name", "-site", "recording-event-", "event-", "shots", "keys", "key", "action",
    "ignition", "preroll-", "shot", "-part", "-point-", "-cue", "-event", "liftoff", "burnout", "landed", "soi", "apoapsis", "periapsis",
    "docked", "undocked", "mark", "maxq", "gimbal-limit", "explosion", "control-lost", "control-back", "reboot", "probe-failed", "power-low",
    "life-support-low", "incapacitated", "arrival", "eva", "board",
};
int failures = 0, messages = 0;
void Fail(SyntaxNode node, string text)
{
    failures++;
    Console.WriteLine($"{node.SyntaxTree.FilePath}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {text}");
}

foreach (string file in Directory.GetFiles(Path.Combine(root, "CineCam"), "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains("\\obj\\") && !p.Contains("\\Localization\\")))
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
    bool ui = file.Contains("\\Ui\\");
    foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        if (ui && invocation.Expression.ToString() is "ImGui.Selectable" or "ImGui.MenuItem" or "ImGui.DragFloat")
        {
            ExpressionSyntax label = invocation.ArgumentList.Arguments[0].Expression;
            bool hiddenId = label is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AsSpan", Expression: LiteralExpressionSyntax hidden }
            } && hidden.Token.ValueText.StartsWith("##", StringComparison.Ordinal);
            if (!hiddenId && !label.ToString().StartsWith("UiText.Control(", StringComparison.Ordinal))
                Fail(invocation, "A translated native control must keep a canonical ID.");
        }
        if (invocation.Expression.ToString() is not ("UiText.MessageText" or "UiText.Format")) continue;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count < 2 || arguments[0].Expression is not LiteralExpressionSyntax idNode) continue;
        string id = idNode.Token.ValueText;
        if (id.Length == 0)
        {
            if (ui && arguments[1].Expression is InterpolatedStringExpressionSyntax neutral)
            {
                string visible = string.Concat(neutral.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)).Split("##")[0];
                if (Regex.IsMatch(visible, "[A-Za-z]{3}") && visible is not ("shot--" or "point" or "key--" or "action--")) Fail(invocation, "Untranslated template: " + visible);
            }
            continue;
        }
        messages++;
        if (!english.ContainsKey(id) || !german.ContainsKey(id)) Fail(invocation, "Missing resource: " + id);
        else if (arguments[1].Expression is LiteralExpressionSyntax fallback && english[id] != fallback.Token.ValueText)
            Fail(invocation, "English resource differs from code fallback: " + id);
        else if (arguments[1].Expression is InterpolatedStringExpressionSyntax formatted)
        {
            int index = 0;
            string template = string.Concat(formatted.Contents.Select(c => c is InterpolatedStringTextSyntax text
                ? text.TextToken.ValueText : "{" + index++ + ((InterpolationSyntax)c).AlignmentClause + ((InterpolationSyntax)c).FormatClause + "}"));
            if (english[id] != template) Fail(invocation, "English template differs from code fallback: " + id);
        }
    }
    if (!ui) continue;
    foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
    {
        string text = literal.Token.ValueText;
        if (!Regex.IsMatch(text, "[A-Za-z]{3}") || text.StartsWith("##") || text.StartsWith("text.") || text.StartsWith("value.")) continue;
        if (literal.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression.ToString() == "UiText.MessageText" || i.Expression.ToString().Contains("Log.") || i.Expression.ToString().Contains("LogHelper."))) continue;
        if (literal.Ancestors().OfType<InterpolatedStringExpressionSyntax>().Any()) continue;
        if (identities.Contains(text) || Regex.IsMatch(text, @"^(ignition|burnout|sequence|ground|q):[0-9]+$") || text == "%.1f deg") continue;
        Fail(literal, "Untranslated literal: " + text);
    }
}
Console.WriteLine($"{messages} message sites, {english.Count} English entries, {german.Count} German entries, {failures} coverage errors.");
return failures == 0 ? 0 : 1;
