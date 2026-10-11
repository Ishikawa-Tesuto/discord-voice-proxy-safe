using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DvpSafeInstaller;

internal static class Program
{
    private const string Loader = "DWrite.dll";
    private const string Network = "force-proxy-safe.dll";
    private const string Config = "proxy.txt";
    private const string Marker = ".dvp-safe-install.json";
    private const string BackupSuffix = ".before-dvp-safe.bak";

    // Формат маркера оставляем прежним: старые установки можно удалить этой версией.
    private sealed record InstallRecord(string LoaderSha256, string NetworkSha256, DateTimeOffset InstalledAt);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Этот EXE предназначен для Windows.");

            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "install":
                        Install(args.Length > 1 ? args[1] : "127.0.0.1:10808");
                        return 0;
                    case "uninstall":
                        Uninstall();
                        return 0;
                    default:
                        Console.WriteLine("Команды: install [адрес:порт], uninstall");
                        return 1;
                }
            }

            Console.WriteLine("DVP Safe\n");
            Console.WriteLine("1. Установить");
            Console.WriteLine("2. Удалить");
            Console.WriteLine("0. Выход\n");
            Console.Write("Выбор: ");
            switch (Console.ReadLine()?.Trim())
            {
                case "1":
                    Console.Write("SOCKS5 [127.0.0.1:10808]: ");
                    var address = Console.ReadLine();
                    Install(string.IsNullOrWhiteSpace(address) ? "127.0.0.1:10808" : address.Trim());
                    break;
                case "2":
                    Uninstall();
                    break;
                case "0":
                case null:
                case "":
                    return 0;
                default:
                    Console.WriteLine("Неверный пункт.");
                    break;
            }
            Console.Write("\nEnter — закрыть...");
            Console.ReadLine();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Ошибка: " + error.Message);
            if (args.Length == 0)
            {
                Console.Write("Enter — закрыть...");
                Console.ReadLine();
            }
            return 1;
        }
    }

    private static string DiscordDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Discord не найден.");

        var latest = Directory.GetDirectories(root, "app-*")
            .Where(dir => File.Exists(Path.Combine(dir, "Discord.exe")))
            .OrderByDescending(dir =>
                Version.TryParse(Path.GetFileName(dir)[4..], out var version)
                    ? version : new Version(0, 0))
            .FirstOrDefault();
        return latest ?? throw new DirectoryNotFoundException("Папка актуальной версии Discord не найдена.");
    }

    private static void CheckDiscordClosed()
    {
        if (new[] { "Discord", "DiscordPTB", "DiscordCanary" }
            .Any(name => Process.GetProcessesByName(name).Length > 0))
            throw new InvalidOperationException("Сначала закрой Discord, включая трей.");
    }

    private static byte[] Embedded(string name)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("В EXE не найдена встроенная DLL: " + name);
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string Hash(string path) => Hash(File.ReadAllBytes(path));

    private static InstallRecord? ReadRecord(string directory)
    {
        var path = Path.Combine(directory, Marker);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(path))
            : null;
    }

    private static void EnsureUnmodified(string path, string expectedHash)
    {
        if (File.Exists(path) && !Hash(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Path.GetFileName(path) + " изменён другим приложением. Операция отменена.");
    }

    private static void Save(string path, byte[] data)
    {
        var temp = path + ".dvp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void Install(string endpoint)
    {
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || separator == endpoint.Length - 1)
            throw new ArgumentException("Адрес указывается как 127.0.0.1:10808");
        var host = endpoint[..separator];
        if (host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '='))
            throw new ArgumentException("Некорректный адрес SOCKS5.");
        if (!int.TryParse(endpoint[(separator + 1)..], out int port) || port is < 1 or > 65535)
            throw new ArgumentException("Некорректный порт SOCKS5.");

        CheckDiscordClosed();
        var dir = DiscordDirectory();
        var loaderBytes = Embedded("DVP_SAFE_LOADER");
        var networkBytes = Embedded("DVP_SAFE_NETWORK");
        var loader = Path.Combine(dir, Loader);
        var network = Path.Combine(dir, Network);
        var config = Path.Combine(dir, Config);
        var markerPath = Path.Combine(dir, Marker);
        var prior = ReadRecord(dir);

        if (prior is not null)
        {
            EnsureUnmodified(loader, prior.LoaderSha256);
            EnsureUnmodified(network, prior.NetworkSha256);
        }
        else
        {
            // Существующие файлы не теряем. Бэкап загрузчика при удалении не запускаем автоматически.
            foreach (var file in new[] { loader, network, config })
                if (File.Exists(file) && !File.Exists(file + BackupSuffix))
                    File.Copy(file, file + BackupSuffix);
        }

        var configBytes = new UTF8Encoding(false).GetBytes(
            $"SOCKS5_PROXY_ADDRESS={host}\nSOCKS5_PROXY_PORT={port}\nDVP_SAFE_MODE=0\n");
        var metadata = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(
            new InstallRecord(Hash(loaderBytes), Hash(networkBytes), DateTimeOffset.UtcNow)));
        var files = new[] { (Path: network, Data: networkBytes), (Path: config, Data: configBytes),
                            (Path: loader, Data: loaderBytes), (Path: markerPath, Data: metadata) };

        // Если запись оборвётся, возвращаем все файлы в прежнее состояние.
        var old = files.Select(f => File.Exists(f.Path) ? File.ReadAllBytes(f.Path) : null).ToArray();
        try
        {
            foreach (var file in files) Save(file.Path, file.Data);
        }
        catch
        {
            for (int i = files.Length - 1; i >= 0; i--)
            {
                try
                {
                    if (old[i] is byte[] contents) Save(files[i].Path, contents);
                    else if (File.Exists(files[i].Path)) File.Delete(files[i].Path);
                }
                catch { /* Ошибка отката не должна скрывать первоначальную ошибку. */ }
            }
            throw;
        }
        Console.WriteLine("Установлено. Теперь можно открыть Discord.");
    }

    private static void Uninstall()
    {
        CheckDiscordClosed();
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Discord не найден.");

        int removed = 0;
        foreach (var dir in Directory.GetDirectories(root, "app-*"))
        {
            var record = ReadRecord(dir);
            if (record is null) continue;
            var loader = Path.Combine(dir, Loader);
            var network = Path.Combine(dir, Network);
            EnsureUnmodified(loader, record.LoaderSha256);
            EnsureUnmodified(network, record.NetworkSha256);
            if (File.Exists(loader)) File.Delete(loader);
            if (File.Exists(network)) File.Delete(network);
            var config = Path.Combine(dir, Config);
            if (File.Exists(config + BackupSuffix))
                File.Copy(config + BackupSuffix, config, overwrite: true);
            else if (File.Exists(config))
            {
                // Без бэкапа конфигурацию не удаляем: её мог редактировать пользователь.
            }
            File.Delete(Path.Combine(dir, Marker));
            removed++;
        }
        Console.WriteLine(removed > 0 ? "Удалено. Discord можно запускать." : "DVP Safe не установлен.");
    }
}
