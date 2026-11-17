using System.Globalization;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CineCam.Localization;

public enum UiLanguage { Auto, English, German }

// Messages stay English until drawn. Reference keys keep user text separate from messages with the same content.
public static class UiText
{
    private sealed record Message(string Id, string English, object?[] Arguments);
    private static readonly ConditionalWeakTable<string, Message> Messages = new();
    private static readonly IReadOnlyDictionary<string, string> German = Load("de");
    private static readonly ConcurrentDictionary<(string Id, string English), string> Templates = new();
    private static readonly ConcurrentDictionary<(string Id, string English), string> Literals = new();
    public static UiLanguage Language { get; set; }
    public static string Locale => Resolve(Language, CultureInfo.CurrentUICulture);

    public static string Resolve(UiLanguage language, CultureInfo culture) => language switch
    {
        UiLanguage.English => "en",
        UiLanguage.German => "de",
        _ => culture.TwoLetterISOLanguageName == "de" ? "de" : "en",
    };

    public static string MessageText(string id, string english) => Literals.GetOrAdd((id, english), key => Register(key.Id, key.English, []));
    public static string Format(string id, FormattableString english) => Register(id, english.Format, english.GetArguments());
    public static string EnumValue<T>(T value) where T : struct, Enum => MessageText("enum." + typeof(T).Name + "." + value, value.ToString());

    private static string Register(string id, string english, object?[] arguments)
    {
        string value = new((arguments.Length == 0 ? english : string.Format(CultureInfo.InvariantCulture, english, arguments)).AsSpan());
        if (value.Length == 0) return "";
        Messages.Add(value, new Message(id, english, arguments));
        return value;
    }

    public static string Concat(object? left, object? right) => Register("", "{0}{1}", [left, right]);
    public static string Join(string separator, IEnumerable<string> parts)
    {
        string[] values = parts.ToArray();
        return Register("", string.Join(separator, values.Select((_, i) => "{" + i + "}")), values);
    }

    public static string Upper(string value) => Case(value, true);
    public static string Lower(string value) => Case(value, false);

    private static string Case(string value, bool upper)
    {
        if (value.Length == 0) return "";
        string result = new((upper ? value.ToUpperInvariant() : value.ToLowerInvariant()).AsSpan());
        Messages.Add(result, new Message(upper ? "@upper" : "@lower", "{0}", [value]));
        return result;
    }

    public static string Capital(string value) => Transform("@capital", value,
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..]);
    public static string TrimEnd(string value) => Transform("@trim", value, value.TrimEnd());

    private static string Transform(string operation, string original, string english)
    {
        if (english.Length == 0) return "";
        string result = new(english.AsSpan());
        Messages.Add(result, new Message(operation, "{0}", [original]));
        return result;
    }

    // The diagnostic reader removes a field path before it presents the message.
    public static string Fragment(string source, string fragment)
    {
        if (source == fragment) return source;
        if (!Messages.TryGetValue(source, out Message? message)) return fragment;
        foreach (string part in message.Arguments.OfType<string>().OrderByDescending(s => s.Length))
        {
            if (part.Length == 0) continue;
            if (part.Contains(fragment, StringComparison.Ordinal)) return Fragment(part, fragment);
            int at = fragment.IndexOf(part, StringComparison.Ordinal);
            if (at >= 0 && Messages.TryGetValue(part, out _))
                return Concat(Concat(fragment[..at], part), fragment[(at + part.Length)..]);
        }
        return fragment;
    }

    public static string Render(string value)
    {
        if (Locale == "en") return value;
        if (!Messages.TryGetValue(value, out Message? message)) return value;
        string template = message.Id.Length > 0
            ? Templates.GetOrAdd((message.Id, message.English), key => Fallback(key.Id, key.English, German)) : message.English;
        object?[] arguments = message.Arguments.Select(a => a is string s ? Render(s) : a).ToArray();
        string result;
        try { result = arguments.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, arguments); }
        catch (FormatException) { result = string.Format(CultureInfo.InvariantCulture, message.English, arguments); }
        return message.Id switch
        {
            "@upper" => result.ToUpperInvariant(),
            "@lower" => result.ToLowerInvariant(),
            "@capital" => result.Length == 0 ? result : char.ToUpperInvariant(result[0]) + result[1..],
            "@trim" => result.TrimEnd(),
            _ => result,
        };
    }

    public static string Visible(string value)
    {
        string rendered = Render(value);
        int cut = rendered.IndexOf("##", StringComparison.Ordinal);
        return cut < 0 ? rendered : rendered[..cut];
    }

    public static string Control(string value) => Visible(value) + "###" + Identity(value);
    public static string Identity(string value)
    {
        int cut = value.IndexOf("###", StringComparison.Ordinal);
        return cut < 0 ? value : value[(cut + 3)..];
    }

    public static bool Compatible(string english, string translation)
    {
        try
        {
            var source = System.Text.CompositeFormat.Parse(english);
            var target = System.Text.CompositeFormat.Parse(translation);
            return source.MinimumArgumentCount == target.MinimumArgumentCount && Slots(english).SequenceEqual(Slots(translation));
        }
        catch (FormatException) { return false; }
    }

    private static IEnumerable<string> Slots(string format)
    {
        var slots = new List<string>();
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '{') continue;
            if (i + 1 < format.Length && format[i + 1] == '{') { i++; continue; }
            int end = format.IndexOf('}', i + 1);
            slots.Add(format[(i + 1)..end]);
            i = end;
        }
        return slots.Order(StringComparer.Ordinal);
    }

    public static string Fallback(string id, string english, IReadOnlyDictionary<string, string> translations) =>
        translations.TryGetValue(id, out string? translated) && Compatible(english, translated) ? translated : english;

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        using Stream? stream = typeof(UiText).Assembly.GetManifestResourceStream("CineCam.Localization." + language + ".json");
        if (stream == null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
