using System.Text.RegularExpressions;

namespace Vortex.Desktop.Services;

/// <summary>
/// Pure static router: user message text → LocalAgent intent.
/// No UI, no HTTP. Deterministic and unit-testable.
/// Hierarchy: prepared jarvis_*/pardus_* tools first; else run_cmd with confirmation.
/// </summary>
public sealed record LocalAgentIntent(
    bool IsSystemAction,
    string ToolName,
    Dictionary<string, string> Arguments,
    bool RequiresConfirmation,
    bool UsesPreparedTool,
    string? FallbackCommand,
    string Summary);

public static class LocalAgentIntentRouter
{
    private static readonly LocalAgentIntent NotSystemAction = new(
        IsSystemAction: false,
        ToolName: string.Empty,
        Arguments: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        RequiresConfirmation: false,
        UsesPreparedTool: false,
        FallbackCommand: null,
        Summary: string.Empty);

    /// <summary>
    /// Match user message to a LocalAgent system action, or return IsSystemAction=false for pure chat.
    /// </summary>
    public static LocalAgentIntent TryMatch(string? message)
    {
        var text = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return NotSystemAction;
        }

        // --- Prepared tools (UsesPreparedTool=true) ---

        // create folder / klasör oluştur
        var folder = MatchCreateFolder(text);
        if (folder is not null) return folder;

        // open notepad / not defteri
        if (IsOpenNotepad(text))
        {
            return Prepared(
                "jarvis_open_app",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["appName"] = "notepad" },
                requiresConfirmation: true,
                summary: "Not Defteri aç");
        }

        // calculator / hesap makinesi
        if (IsOpenCalculator(text))
        {
            return Prepared(
                "jarvis_open_app",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["appName"] = "calculator" },
                requiresConfirmation: true,
                summary: "Hesap makinesi aç");
        }

        // paint / mspaint
        if (IsOpenPaint(text))
        {
            return Prepared(
                "jarvis_open_app",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["appName"] = "mspaint" },
                requiresConfirmation: true,
                summary: "Paint aç");
        }

        // lock screen / ekranı kilitle
        if (IsLockScreen(text))
        {
            return Prepared(
                "jarvis_lock_screen",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                requiresConfirmation: true,
                summary: "Ekranı kilitle");
        }

        // add note / not ekle / not al
        var note = MatchAddNote(text);
        if (note is not null) return note;

        // write document / belge yaz / belge oluştur
        var doc = MatchWriteDocument(text);
        if (doc is not null) return doc;

        // dark / light theme
        var theme = MatchTheme(text);
        if (theme is not null) return theme;

        // black wallpaper
        if (IsBlackWallpaper(text))
        {
            return Prepared(
                "pardus_set_wallpaper",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["color"] = "black",
                    ["dryRun"] = "false"
                },
                requiresConfirmation: false,
                summary: "Siyah duvar kağıdı");
        }

        // --- Explicit run_cmd prefixes / "çalıştır X" / "run X" ---
        var runCmd = MatchRunCmd(text);
        if (runCmd is not null) return runCmd;

        // Pure chat / greetings / questions
        return NotSystemAction;
    }

    private static LocalAgentIntent Prepared(
        string toolName,
        Dictionary<string, string> args,
        bool requiresConfirmation,
        string summary) =>
        new(
            IsSystemAction: true,
            ToolName: toolName,
            Arguments: args,
            RequiresConfirmation: requiresConfirmation,
            UsesPreparedTool: true,
            FallbackCommand: null,
            Summary: summary);

    private static LocalAgentIntent RunCmd(string command, string summary) =>
        new(
            IsSystemAction: true,
            ToolName: "run_cmd",
            Arguments: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["command"] = command },
            RequiresConfirmation: true,
            UsesPreparedTool: false,
            FallbackCommand: command,
            Summary: summary);

    private static LocalAgentIntent? MatchCreateFolder(string text)
    {
        // "klasör oluştur Test", "klasor olustur Test", "create folder Test"
        var patterns = new[]
        {
            @"^(?:klas[oö]r\s+olu[sş]tur|create\s+folder)\s+(.+)$",
            @"^(?:olu[sş]tur\s+klas[oö]r|folder\s+create)\s+(.+)$"
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim().Trim('"', '\'');
                if (string.IsNullOrWhiteSpace(name)) continue;
                return Prepared(
                    "jarvis_create_folder",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = name },
                    requiresConfirmation: false,
                    summary: $"Klasör oluştur: {name}");
            }
        }

        return null;
    }

    private static bool IsOpenNotepad(string text)
    {
        var t = NormalizeLoose(text);
        return t is "not defteri" or "not defteri ac" or "not defteri aç"
            or "open notepad" or "notepad" or "notepad ac" or "notepad aç"
            or "notpadi ac" or "notpadi aç"
            or "not defterini ac" or "not defterini aç";
    }

    private static bool IsOpenCalculator(string text)
    {
        var t = NormalizeLoose(text);
        return t is "hesap makinesi" or "hesap makinesi ac" or "hesap makinesi aç"
            or "calculator" or "open calculator" or "calc"
            or "hesap makinesini ac" or "hesap makinesini aç";
    }

    private static bool IsOpenPaint(string text)
    {
        var t = NormalizeLoose(text);
        return t is "paint" or "mspaint" or "open paint" or "open mspaint"
            or "paint ac" or "paint aç" or "mspaint ac" or "mspaint aç";
    }

    private static bool IsLockScreen(string text)
    {
        var t = NormalizeLoose(text);
        return t is "ekrani kilitle" or "ekranı kilitle" or "lock screen" or "lockscreen"
            or "ekran kilitle" or "kilitle";
    }

    private static LocalAgentIntent? MatchAddNote(string text)
    {
        var patterns = new[]
        {
            @"^(?:not\s+ekle|not\s+al|add\s+note)\s+(.+)$"
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success)
            {
                var noteText = m.Groups[1].Value.Trim().Trim('"', '\'');
                if (string.IsNullOrWhiteSpace(noteText)) continue;
                return Prepared(
                    "jarvis_add_note",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["text"] = noteText },
                    requiresConfirmation: false,
                    summary: "Not ekle");
            }
        }

        return null;
    }

    private static LocalAgentIntent? MatchWriteDocument(string text)
    {
        var patterns = new[]
        {
            @"^(?:belge\s+yaz|belge\s+olu[sş]tur|write\s+document|create\s+document)\s+(.+)$"
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success)
            {
                var topic = m.Groups[1].Value.Trim().Trim('"', '\'');
                if (string.IsNullOrWhiteSpace(topic)) continue;
                return Prepared(
                    "jarvis_write_document",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["topic"] = topic },
                    requiresConfirmation: false,
                    summary: $"Belge yaz: {topic}");
            }
        }

        return null;
    }

    private static LocalAgentIntent? MatchTheme(string text)
    {
        var t = NormalizeLoose(text);

        // dark
        if (t is "dark theme" or "koyu tema" or "temayi koyu yap" or "temayı koyu yap"
            or "tema degistir dark" or "tema değiştir dark" or "set dark theme"
            or "tema koyu" or "koyu moda gec" or "koyu moda geç")
        {
            return Prepared(
                "pardus_set_theme",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mode"] = "dark" },
                requiresConfirmation: false,
                summary: "Koyu tema");
        }

        // light
        if (t is "light theme" or "acik tema" or "açık tema" or "temayi acik yap" or "temayı açık yap"
            or "tema degistir light" or "tema değiştir light" or "set light theme"
            or "tema acik" or "tema açık" or "acik moda gec" or "açık moda geç")
        {
            return Prepared(
                "pardus_set_theme",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mode"] = "light" },
                requiresConfirmation: false,
                summary: "Açık tema");
        }

        return null;
    }

    private static bool IsBlackWallpaper(string text)
    {
        var t = NormalizeLoose(text);
        return t is "black wallpaper" or "siyah duvar kagidi" or "siyah duvar kağıdı"
            or "siyah wallpaper" or "set black wallpaper" or "duvar kagidi siyah" or "duvar kağıdı siyah";
    }

    private static LocalAgentIntent? MatchRunCmd(string text)
    {
        // Prefixes: run: / çalıştır: / cmd:
        var prefix = Regex.Match(
            text,
            @"^(?:run|cmd|çalıştır|calistir)\s*:\s*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (prefix.Success)
        {
            var command = prefix.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(command))
            {
                return RunCmd(command, $"Komut çalıştır: {command}");
            }
        }

        // "çalıştır X" / "calistir X" / "run X" (X not empty, not pure chat)
        var verb = Regex.Match(
            text,
            @"^(?:çalıştır|calistir|run)\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (verb.Success)
        {
            var command = verb.Groups[1].Value.Trim().Trim('"', '\'');
            // Avoid swallowing "run notepad" style that we already map as prepared — but those matched earlier.
            if (!string.IsNullOrWhiteSpace(command))
            {
                return RunCmd(command, $"Komut çalıştır: {command}");
            }
        }

        return null;
    }

    /// <summary>
    /// Lowercase + fold common Turkish diacritics for loose equality checks.
    /// </summary>
    private static string NormalizeLoose(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        t = t
            .Replace('ı', 'i')
            .Replace('İ', 'i')
            .Replace('ş', 's')
            .Replace('Ş', 's')
            .Replace('ğ', 'g')
            .Replace('Ğ', 'g')
            .Replace('ü', 'u')
            .Replace('Ü', 'u')
            .Replace('ö', 'o')
            .Replace('Ö', 'o')
            .Replace('ç', 'c')
            .Replace('Ç', 'c');
        // collapse whitespace
        t = Regex.Replace(t, @"\s+", " ");
        return t;
    }
}
