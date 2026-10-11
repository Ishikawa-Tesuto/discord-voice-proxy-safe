using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DvpSafeInstaller;

internal static class Program
{
    private const string MarkerName = ".dvp-safe-install.json";
    private const string LoaderName = "DWrite.dll";
    private const string NetworkName = "force-proxy-safe.dll";
    private const string ConfigName = "proxy.txt";

    private sealed record InstallRecord(string LoaderSha256, string NetworkSha256, DateTimeOffset InstalledAt);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0) return ShowMenu();
            var (channel, rest) = ParseChannel(args);
            if (rest.Length == 0) return ShowMenu(channel);
            switch (rest[0].ToLowerInvariant())
            {
                case "install":
                {
                    var safe = rest.Contains("--safe", StringComparer.OrdinalIgnoreCase);
                    var positional = rest.Skip(1).Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
                    if (positional.Length > 2) throw new ArgumentException("Формат: install [HOST] [PORT] [--safe]");
                    var host = positional.Length >= 1 ? positional[0] : Ask("SOCKS5 адрес", "127.0.0.1");
                    var port = positional.Length >= 2 ? positional[1] : Ask("SOCKS5 порт", "10808");
                    Install(FindDiscordApp(channel), host, port, safe);
                    return 0;
                }
                case "uninstall":
                    Uninstall(FindDiscordApp(channel));
                    return 0;
                case "status":
                    Status(FindDiscordApp(channel));
                    return 0;
                case "safe-on":
                    SetSafeMode(FindDiscordApp(channel), true);
                    return 0;
                case "safe-off":
                    SetSafeMode(FindDiscordApp(channel), false);
                    return 0;
                case "logs":
                    ShowLogs();
                    return 0;
                case "help":
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    throw new ArgumentException("Неизвестная команда. Используй: DVP-Safe.exe help");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Ошибка: " + e.Message);
            return 1;
        }
    }

    private static (string channel, string[] args) ParseChannel(string[] args)
    {
        var output = new List<string>();
        string channel = "stable";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--channel", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length) throw new ArgumentException("Укажи stable, ptb или canary после --channel");
                channel = args[i].ToLowerInvariant();
            }
            else output.Add(args[i]);
        }
        if (channel is not ("stable" or "ptb" or "canary")) throw new ArgumentException("Канал должен быть stable, ptb или canary");
        return (channel, output.ToArray());
    }

    private static int ShowMenu(string channel = "stable")
    {
        Console.WriteLine("Discord Voice Proxy Safe — экспериментальный установщик");
        Console.WriteLine("1 — Установить (с включённым проксированием)");
        Console.WriteLine("2 — Установить в диагностическом режиме (без сетевых перехватов)");
        Console.WriteLine("3 — Отключить сетевые перехваты");
        Console.WriteLine("4 — Включить сетевые перехваты");
        Console.WriteLine("5 — Проверить состояние");
        Console.WriteLine("6 — Удалить установленные файлы");
        Console.WriteLine("7 — Посмотреть лог");
        Console.Write("Выбор: ");
        var selection = Console.ReadLine()?.Trim();
        if (selection is null or "" or "0") return 0;
        if (selection == "7") { ShowLogs(); return 0; }
        var app = FindDiscordApp(channel);
        switch (selection)
        {
            case "1":
            case "2":
                Install(app, Ask("SOCKS5 адрес", "127.0.0.1"), Ask("SOCKS5 порт", "10808"), selection == "2");
                break;
            case "3": SetSafeMode(app, true); break;
            case "4": SetSafeMode(app, false); break;
            case "5": Status(app); break;
            case "6": Uninstall(app); break;
            default: Console.WriteLine("Неизвестный пункт меню"); break;
        }
        return 0;
    }

    private static string Ask(string caption, string fallback)
    {
        Console.Write($"{caption} [{fallback}]: ");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(input) ? fallback : input;
    }

    private static string FindDiscordApp(string channel)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Программа предназначена для Windows x64");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            channel switch { "stable" => "Discord", "ptb" => "DiscordPTB", "canary" => "DiscordCanary", _ => throw new ArgumentException("Канал неизвестен") });
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Папка Discord не найдена: {root}");
        var directories = Directory.GetDirectories(root, "app-*")
            .Where(x => File.Exists(Path.Combine(x, "Discord.exe")))
            .Select(x => new {
                Path = x,
                Version = Version.TryParse(Path.GetFileName(x)[4..], out var version) ? version : new Version(0, 0)
            })
            .OrderByDescending(x => x.Version)
            .ToList();
        if (directories.Count == 0) throw new DirectoryNotFoundException($"Не найдена рабочая папка app-* с Discord.exe в {root}");
        return directories[0].Path;
    }

    private static void CheckDiscordStopped()
    {
        foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            if (Process.GetProcessesByName(name).Any())
                throw new InvalidOperationException("Discord запущен. Полностью закрой его (включая трей) и повтори команду.");
        }
    }

    private static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Внутри EXE нет ресурса {name}; проверь GitHub Actions.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Sha256(string filename) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filename)));

    private static InstallRecord? ReadMarker(string app)
    {
        var file = Path.Combine(app, MarkerName);
        if (!File.Exists(file)) return null;
        return JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(file));
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        var temp = path + ".dvp-new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteAtomically(string path, string contents)
        => WriteAtomically(path, new UTF8Encoding(false).GetBytes(contents));

    private static void BackupIfNeeded(string target)
    {
        var backup = target + ".before-dvp-safe.bak";
        if (File.Exists(target) && !File.Exists(backup)) File.Copy(target, backup);
    }

    private static void Install(string app, string host, string portText, bool safe)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(c => char.IsControl(c) || c == '='))
            throw new ArgumentException("Недопустимый SOCKS5 адрес");
        if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
            throw new ArgumentException("Порт должен быть числом от 1 до 65535");
        CheckDiscordStopped();
        var loader = Resource("DVP_SAFE_LOADER");
        var network = Resource("DVP_SAFE_NETWORK");
        var loaderPath = Path.Combine(app, LoaderName);
        var networkPath = Path.Combine(app, NetworkName);
        var configPath = Path.Combine(app, ConfigName);
        var marker = ReadMarker(app);
        if (marker is not null)
        {
            if (File.Exists(loaderPath) && !Sha256(loaderPath).Equals(marker.LoaderSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("DWrite.dll изменён после установки. Не заменяю неизвестную DLL.");
            if (File.Exists(networkPath) && !Sha256(networkPath).Equals(marker.NetworkSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("force-proxy-safe.dll изменён после установки. Не заменяю неизвестную DLL.");
        }
        else
        {
            BackupIfNeeded(loaderPath);
            BackupIfNeeded(networkPath);
            BackupIfNeeded(configPath);
        }
        // Config uses the same options as the patched C++ loader. Never print credentials.
        var config = $"SOCKS5_PROXY_ADDRESS={host}\nSOCKS5_PROXY_PORT={port}\nDVP_SAFE_MODE={(safe ? "1" : "0")}\n";
        var markerPath = Path.Combine(app, MarkerName);
        var targets = new[] { networkPath, configPath, loaderPath, markerPath };
        var previous = targets.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        try
        {
            WriteAtomically(networkPath, network);
            WriteAtomically(configPath, config);
            WriteAtomically(loaderPath, loader);
            WriteAtomically(markerPath, JsonSerializer.Serialize(new InstallRecord(Sha256(loader), Sha256(network), DateTimeOffset.UtcNow)));
        }
        catch
        {
            Console.Error.WriteLine("Установка прервалась. Пытаюсь вернуть предыдущие файлы.");
            foreach (var path in targets.Reverse())
            {
                try
                {
                    if (previous[path] is byte[] original) WriteAtomically(path, original);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) { Console.Error.WriteLine($"Не удалось восстановить {path}: {ex.Message}"); }
            }
            throw;
        }
        Console.WriteLine($"Готово. Установлено: {app}");
        Console.WriteLine(safe ? "Диагностический режим: сетевой перехват отключён." : "Сетевой перехват включён (функциональность голоса ещё экспериментальная).");
        Console.WriteLine("Теперь можно запустить Discord. Для отката: DVP-Safe.exe uninstall");
    }

    private static void Uninstall(string app)
    {
        CheckDiscordStopped();
        var marker = ReadMarker(app);
        if (marker is null)
        {
            Console.WriteLine("Установка DVP Safe не обнаружена. Не удаляю неизвестные DLL.");
            return;
        }
        var loader = Path.Combine(app, LoaderName);
        var network = Path.Combine(app, NetworkName);
        if ((File.Exists(loader) && !Sha256(loader).Equals(marker.LoaderSha256, StringComparison.OrdinalIgnoreCase)) ||
            (File.Exists(network) && !Sha256(network).Equals(marker.NetworkSha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Один из установленных DLL изменился. Удаление остановлено, чтобы не удалить чужой файл.");
        DeleteIfSameHash(loader, marker.LoaderSha256);
        DeleteIfSameHash(network, marker.NetworkSha256);
        var config = Path.Combine(app, ConfigName);
        var configBackup = config + ".before-dvp-safe.bak";
        if (File.Exists(configBackup))
        {
            File.Copy(configBackup, config, overwrite: true);
            Console.WriteLine("Исходный proxy.txt восстановлен из резервной копии.");
        }
        else if (File.Exists(config))
        {
            Console.WriteLine("proxy.txt оставлен на месте (исходной резервной копии нет).");
        }
        File.Delete(Path.Combine(app, MarkerName));
        Console.WriteLine("DVP Safe отключён. Резервные копии DLL сохранены, старые DLL не восстанавливаются автоматически.");
    }

    private static void DeleteIfSameHash(string path, string hash)
    {
        if (!File.Exists(path)) return;
        if (Sha256(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        else Console.WriteLine($"Не удаляю изменённый файл: {path}");
    }

    private static void SetSafeMode(string app, bool enabled)
    {
        CheckDiscordStopped();
        if (ReadMarker(app) is null) throw new InvalidOperationException("Сначала установи DVP Safe");
        var path = Path.Combine(app, ConfigName);
        if (!File.Exists(path)) throw new FileNotFoundException("Не найден proxy.txt", path);
        var lines = File.ReadAllLines(path)
            .Where(x => !x.TrimStart().StartsWith("DVP_SAFE_MODE=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        lines.Add("DVP_SAFE_MODE=" + (enabled ? "1" : "0"));
        WriteAtomically(path, string.Join("\n", lines) + "\n");
        Console.WriteLine(enabled ? "Диагностический режим включён." : "Сетевой перехват включён. Перезапусти Discord.");
    }

    private static void Status(string app)
    {
        var marker = ReadMarker(app);
        if (marker is null) { Console.WriteLine("DVP Safe не установлен (маркер отсутствует)."); return; }
        var loader = Path.Combine(app, LoaderName);
        var network = Path.Combine(app, NetworkName);
        Console.WriteLine("Папка: " + app);
        Console.WriteLine("Loader: " + (File.Exists(loader) && Sha256(loader) == marker.LoaderSha256 ? "OK" : "отсутствует/изменён"));
        Console.WriteLine("Network: " + (File.Exists(network) && Sha256(network) == marker.NetworkSha256 ? "OK" : "отсутствует/изменён"));
        var configPath = Path.Combine(app, ConfigName);
        var safe = File.Exists(configPath) && File.ReadAllLines(configPath).Any(x => x.Trim().Equals("DVP_SAFE_MODE=1", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("Режим: " + (safe ? "диагностика (перехваты отключены)" : "проксирование включено"));
        Console.WriteLine("Установлен: " + marker.InstalledAt.ToLocalTime());
    }

    private static void ShowLogs()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DVP-Safe", "loader.log");
        Console.WriteLine("Лог: " + file);
        if (!File.Exists(file)) { Console.WriteLine("Лог пока не создан."); return; }
        foreach (var line in File.ReadLines(file).TakeLast(40)) Console.WriteLine(line);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Discord Voice Proxy Safe — установщик Windows x64 (экспериментальный)");
        Console.WriteLine("DVP-Safe.exe                                 интерактивное меню");
        Console.WriteLine("DVP-Safe.exe install [HOST] [PORT] [--safe] установить");
        Console.WriteLine("DVP-Safe.exe safe-on                         отключить сетевые перехваты");
        Console.WriteLine("DVP-Safe.exe safe-off                        включить сетевые перехваты");
        Console.WriteLine("DVP-Safe.exe status                          проверить установку");
        Console.WriteLine("DVP-Safe.exe logs                            показать последние 40 строк лога");
        Console.WriteLine("DVP-Safe.exe uninstall                       удалить только файлы DVP Safe");
        Console.WriteLine("Дополнительно: --channel stable|ptb|canary (по умолчанию stable)");
        Console.WriteLine("Исходники: https://github.com/Ishikawa-Tesuto/discord-voice-proxy-safe");
    }
}
