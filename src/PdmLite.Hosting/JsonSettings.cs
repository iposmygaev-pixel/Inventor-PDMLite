using Microsoft.Extensions.Logging;
using PdmLite.Core.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PdmLite.Hosting;

/// <summary>
/// Хранение настроек пользователя в <c>%AppData%\PdmLite\{name}.json</c>.
/// </summary>
/// <remarks>
/// Повреждённый файл не мешает запуску: используются значения по умолчанию, а сам файл сначала
/// копируется рядом (<c>{name}.json.damaged-{время}.bak</c>), чтобы следующее сохранение не уничтожило
/// прежние настройки без следа. Причина — в <see cref="LoadProblem"/> и в журнале.
/// </remarks>
public sealed class JsonSettings<T> where T : class, new()
{
    private readonly string _path;
    private readonly ILogger? _logger;

    public JsonSettings(string name, string? folder = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _path = ContainedPath.Resolve(folder ?? PdmLitePaths.UserSettingsFolder, name + ".json");
        _logger = logger;
        Value = Load();
    }

    public T Value { get; private set; }

    /// <summary>
    /// Почему при последней загрузке использованы значения по умолчанию вместо файла; <c>null</c> — файл
    /// прочитан или его нет.
    /// </summary>
    public string? LoadProblem { get; private set; }

    /// <summary>Копия повреждённого файла, сделанная при последней загрузке, если она понадобилась.</summary>
    public string? DamagedCopyPath { get; private set; }

    public string FilePath => _path;

    public T Reload() => Value = Load();
    internal void Accept(T value) => Value = value;

    public void Save()
    {
        // Сериализация до записи: её ошибка не должна затронуть предыдущий файл.
        var text = JsonSerializer.Serialize(Value, JsonSettings.Options);
        using var gate = SettingsTransaction.IsGrouped(_path) ? SettingsTransaction.Enter(Path.GetDirectoryName(_path)!) : null;
        if (gate is not null) SettingsTransaction.Recover(Path.GetDirectoryName(_path)!);
        AtomicFile.WriteAllText(_path, text);
    }

    private T Load()
    {
        LoadProblem = null;
        DamagedCopyPath = null;
        try
        {
            using var gate = SettingsTransaction.IsGrouped(_path) ? SettingsTransaction.Enter(Path.GetDirectoryName(_path)!) : null;
            if (gate is not null) SettingsTransaction.Recover(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path)) return new T();
            T? value;
            using (var stream = AtomicFile.OpenRead(_path))
                value = JsonSerializer.Deserialize<T>(stream, JsonSettings.Options);
            if (value is not null) return value;
            return Damaged(new JsonException("Файл настроек содержит null вместо объекта."));
        }
        catch (JsonException ex)
        {
            // Повреждённый файл настроек не должен мешать запуску, но и пропадать молча не должен.
            return Damaged(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл недоступен (занят, нет прав) — он может быть цел, поэтому копию не делаем.
            LoadProblem = $"Файл настроек {_path} не прочитан: {ex.Message}. Используются значения по умолчанию.";
            _logger?.LogWarning(ex, "Файл настроек {Path} не прочитан, используются значения по умолчанию", _path);
            return new T();
        }
    }

    private T Damaged(JsonException error)
    {
        try
        {
            DamagedCopyPath = JsonSettings.KeepDamagedCopy(_path);
            LoadProblem = $"Файл настроек {_path} повреждён ({error.Message}). Используются значения по умолчанию; " +
                          $"прежний файл сохранён: {DamagedCopyPath}.";
            _logger?.LogWarning(error, "Файл настроек {Path} повреждён, копия сохранена в {Copy}; используются значения по умолчанию",
                _path, DamagedCopyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadProblem = $"Файл настроек {_path} повреждён ({error.Message}), копию сохранить не удалось: {ex.Message}. " +
                          "Используются значения по умолчанию.";
            _logger?.LogWarning(new AggregateException(error, ex),
                "Файл настроек {Path} повреждён, копию сохранить не удалось; используются значения по умолчанию", _path);
        }
        return new T();
    }
}

public static class JsonSettings
{
    /// <summary>
    /// Копирует повреждённый файл настроек рядом: <c>{файл}.damaged-{время}.bak</c>. Если такая же копия
    /// уже есть (например, при повторной загрузке того же файла), новая не создаётся. Возвращает путь копии.
    /// </summary>
    internal static string KeepDamagedCopy(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var prefix = Path.GetFileName(path) + ".damaged-";
        var content = File.ReadAllBytes(path);
        foreach (var existing in Directory.EnumerateFiles(folder, prefix + "*.bak"))
        {
            try
            {
                if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(content)) return existing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* сравним со следующей */ }
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (var attempt = 0; ; attempt++)
        {
            var copy = Path.Combine(folder, prefix + stamp + (attempt == 0 ? "" : "-" + attempt) + ".bak");
            try
            {
                using var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                target.Write(content);
                return copy;
            }
            catch (IOException) when (File.Exists(copy) && attempt < 100) { /* имя занято — следующее */ }
        }
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}
