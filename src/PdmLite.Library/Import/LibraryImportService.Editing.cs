using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Rules;

namespace PdmLite.Library.Import;

/// <summary>Правка списка импорта: источник обозначений, правила, замена, сохранение и загрузка списка.</summary>
public sealed partial class LibraryImportService
{
    /// <summary>
    /// Берёт обозначение выбранных строк из другого источника (свойство или имя файла).
    /// Строки, где источник пуст, не меняются. Возвращает число изменённых строк.
    /// </summary>
    public int SetSource(ImportSession session, IEnumerable<ImportCandidate> candidates, DesignationSource source)
    {
        if (source == DesignationSource.Manual)
            throw new ArgumentException("Ручной ввод выбирается правкой обозначения.", nameof(source));
        var changed = 0;
        foreach (var c in candidates.Where(c => c.SourceText(source) is not null))
        {
            Prepare(c, session, source, resetKind: true);
            changed++;
        }
        Validate(session);
        return changed;
    }

    /// <summary>
    /// Перечитывает правила из базы и заново готовит обозначения всех строк, кроме исправленных
    /// вручную (после изменения правил).
    /// </summary>
    public async Task ReloadRulesAsync(ImportSession session, CancellationToken ct = default)
    {
        await using (var db = database.CreateContext())
            session.Rules = new DesignationRuleSet(await db.DesignationRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false));
        foreach (var c in session.Candidates.Where(c => c.Source != DesignationSource.Manual))
            Prepare(c, session, c.Source, resetKind: true);
        Validate(session);
    }

    /// <summary>
    /// Поиск и замена в обозначениях выбранных строк (обычный текст или регулярное выражение).
    /// Изменённые строки считаются исправленными вручную. Возвращает число изменённых строк.
    /// </summary>
    public int ReplaceInDesignations(ImportSession session, IEnumerable<ImportCandidate> candidates, string find, string replace, bool isRegex)
    {
        if (string.IsNullOrEmpty(find))
            throw new ArgumentException("Не указано, что искать.", nameof(find));
        var regex = isRegex ? new Regex(find, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) : null;

        var changed = 0;
        foreach (var c in candidates)
        {
            string replaced;
            try
            {
                replaced = regex is null ? c.Designation.Replace(find, replace, StringComparison.Ordinal) : regex.Replace(c.Designation, replace);
            }
            catch (RegexMatchTimeoutException)
            {
                continue; // выражение «зависло» на этой строке — её не меняем
            }

            var next = DesignationNormalizer.ToCanonical(replaced);
            if (next == c.Designation)
                continue;
            c.Designation = next;
            c.Source = DesignationSource.Manual;
            c.AppliedRules = [];
            changed++;
        }
        Validate(session);
        return changed;
    }

    /// <summary>
    /// Исключает строки, файлы которых уже внесены в библиотеку (то же содержимое): например,
    /// компоненты подсборки, добавленные вместе с ней. Возвращает их источники → файл в библиотеке.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExcludeAlreadyInLibrary(ImportSession session)
    {
        var storage = storageFactory();
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in session.Candidates.Where(c => c.Include && c.FileHash.Length > 0))
        {
            if (session.ExistingPaths.TryGetValue(c.FileHash, out var relative))
            {
                c.Include = false;
                known[c.SourcePath] = storage.GetFullPath(relative);
            }
        }
        Validate(session);
        return known;
    }

    /// <summary>Сохраняет список подготовки с правками пользователя.</summary>
    public void SaveList(ImportSession session, string path) => ImportList.From(session).Save(path);

    /// <summary>
    /// Открывает сохранённый список: файлы анализируются заново (они могли измениться),
    /// затем восстанавливаются правки. Пропавшие файлы попадают в «Пропущенные».
    /// </summary>
    public async Task<ImportSession> LoadListAsync(string path, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var list = ImportList.Load(path);
        var session = await CreateSessionAsync(ct).ConfigureAwait(false);

        var present = new List<ImportListEntry>();
        foreach (var entry in list.Items)
        {
            if (File.Exists(entry.Path))
                present.Add(entry);
            else
                session.Skipped.Add(new SkippedFile(entry.Path, "Файл из списка не найден"));
        }

        await AddAsync(session, present.Select(e => e.Path), progress, ct).ConfigureAwait(false);

        var byPath = session.Candidates.ToDictionary(c => c.SourcePath, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in present)
        {
            if (!byPath.TryGetValue(Path.GetFullPath(entry.Path), out var c))
                continue; // файл теперь в папке библиотеки и т.п. — причина уже в «Пропущенных»

            c.Include = entry.Include;
            // Ручной стандарт — до подготовки обозначения: от стандарта зависит предлагаемый раздел.
            c.ManualStandard = LibraryRules.NormalizeStandard(entry.ManualStandard);
            if (entry.Source == DesignationSource.Manual)
            {
                c.Designation = DesignationNormalizer.ToCanonical(entry.Designation);
                c.Source = DesignationSource.Manual;
                c.AppliedRules = [];
                Suggest(c, session);
            }
            else if (c.SourceText(entry.Source) is not null && (entry.Source != c.Source || c.ManualStandard is not null))
            {
                Prepare(c, session, entry.Source, resetKind: true);
            }

            if (!c.KindFromFile)
                c.Kind = entry.Kind;
            c.Manufacturer = entry.Manufacturer;
            if (entry.CategoryChosen && entry.CategoryPath is not null && session.Catalog.FindByPath(entry.CategoryPath) is { } category)
                c.CategoryId = category.Id;
        }

        Validate(session);
        return session;
    }
}
