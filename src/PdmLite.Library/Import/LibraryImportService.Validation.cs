using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Import;

/// <summary>Проверка кандидатов импорта перед внесением в библиотеку.</summary>
public sealed partial class LibraryImportService
{
    /// <summary>Проверяет все кандидаты. Вызывается после анализа и после каждой правки пользователя.</summary>
    public void Validate(ImportSession session)
    {
        var storage = storageFactory();
        var included = session.Candidates.Where(c => c.Include).ToList();
        var bySource = included.Select(c => c.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Размещение и обозначение — общими правилами (как при изменении карточки).
        var placements = new Dictionary<ImportCandidate, LibraryPlacement>();
        foreach (var c in session.Candidates)
        {
            c.ManualStandard = LibraryRules.NormalizeStandard(c.ManualStandard);
            var placement = LibraryRules.Place(session.Catalog, storage, c.Designation, c.Kind, c.Manufacturer,
                c.CategoryId, Path.GetExtension(c.SourcePath), c.ManualStandard);
            c.Designation = placement.Designation;
            c.Standard = placement.Standard;
            c.Issues.Clear();
            c.TargetRelativePath = placement.RelativePath;
            c.LibraryMatch = null;
            placements[c] = placement;
        }

        var byKey = included.Where(c => c.Designation.Length > 0)
            .GroupBy(c => DesignationNormalizer.ToKey(c.Designation)).ToDictionary(g => g.Key, g => g.ToList());
        var byHash = included.Where(c => c.FileHash.Length > 0)
            .GroupBy(c => c.FileHash).ToDictionary(g => g.Key, g => g.ToList());
        var byTarget = included.Where(c => c.TargetRelativePath is not null)
            .GroupBy(c => c.TargetRelativePath!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Проверяются и исключённые строки: видно, есть ли компонент в библиотеке и что с ним не так.
        // Совпадения внутри списка — только между включёнными.
        foreach (var c in session.Candidates)
        {
            var inList = c.Include;
            void Error(string m) => c.Issues.Add(new ImportIssue(ImportSeverity.Error, m));
            void Warn(string m) => c.Issues.Add(new ImportIssue(ImportSeverity.Warning, m));
            void Info(string m) => c.Issues.Add(new ImportIssue(ImportSeverity.Info, m));

            // Чтение файла и свойств
            if (c.ReadError is not null)
                Error($"Файл не прочитан (занят другой программой или нет доступа): {c.ReadError}");
            else if (!session.CanWriteProperties)
                Error($"Нет доступа к свойствам файлов Inventor: {session.PropertiesUnavailableReason}");
            else if (c.Properties is null)
                Error($"Не удалось прочитать файл: {c.PropertiesError}");

            c.Issues.AddRange(placements[c].Issues);
            if (c.Designation.Length == 0)
                continue;

            var key = DesignationNormalizer.ToKey(c.Designation);
            if (c.OriginalDesignation.Length > 0 && c.OriginalDesignation != c.Designation)
                Info($"Обозначение приведено к единому виду: «{c.OriginalDesignation}» → «{c.Designation}».");

            if (c.Properties is { } p)
            {
                var fields = new[] { ("Обозначение", p.PartNumber), ("Описание", p.Description), ("Наименование", p.Title) }
                    .Where(f => !string.IsNullOrWhiteSpace(f.Item2)).ToList();
                if (fields.Any(f => DesignationNormalizer.ToKey(f.Item2!) != key))
                    Warn("Свойства файла расходятся с обозначением: "
                         + string.Join(", ", fields.Select(f => $"{f.Item1} «{f.Item2}»"))
                         + ". При внесении во все три свойства запишется обозначение.");
                if (p.IsContentCenter)
                    Info("Файл создан из Content Center — при внесении метка Content Center будет снята, компонент станет самостоятельным.");
            }

            if (c.KindFromFile)
                Info($"Тип «{(c.Kind == LibraryItemKind.Standard ? "стандартное" : "покупное")}» взят из свойства {LibraryProperties.SpecSection} в файле.");
            if (c.AppliedRules.Count > 0)
                Info("Применены правила: " + string.Join("; ", c.AppliedRules) + ".");

            if (DesignationNormalizer.ToKey(Path.GetFileNameWithoutExtension(c.SourcePath)) != key)
                Info("Имя файла не совпадает с обозначением — в библиотеке файл будет назван по обозначению.");

            // Дубликаты
            if (c.Properties is { } geometry && LibraryGeometry.Known(geometry.Volume, geometry.Area))
            {
                var sameShape = session.ExistingGeometry
                    .Where(g => g.Type == c.DocumentType && DesignationNormalizer.ToKey(g.Designation) != key
                                && LibraryGeometry.Same(g.Volume, g.Area, geometry.Volume, geometry.Area))
                    .Select(g => $"«{g.Designation}» из библиотеки")
                    .Concat(included.Where(o => inList && o != c && o.DocumentType == c.DocumentType && o.Properties is { } p2
                                                && LibraryGeometry.Same(p2.Volume, p2.Area, geometry.Volume, geometry.Area))
                        .Select(o => $"«{o.Designation}» ({o.FileName}) в списке"))
                    .ToList();
                var libraryShape = session.ExistingGeometry.FirstOrDefault(g => g.Type == c.DocumentType
                    && DesignationNormalizer.ToKey(g.Designation) != key && LibraryGeometry.Same(g.Volume, g.Area, geometry.Volume, geometry.Area));
                if (libraryShape.Designation is not null)
                    c.LibraryMatch = $"Похожий (та же геометрия): «{libraryShape.Designation}»";
                if (sameShape.Count > 0)
                    Warn("Такая же геометрия (объём и площадь поверхности), как у " + string.Join(", ", sameShape.Take(5))
                         + (sameShape.Count > 5 ? $" и ещё {sameShape.Count - 5}" : "")
                         + ". Проверьте, не дубликат ли это. Если отличается только обозначение (класс прочности, покрытие) — всё в порядке.");
            }
            string? sameFile = null;
            if (c.FileHash.Length > 0 && session.ExistingHashes.TryGetValue(c.FileHash, out var fileOwner))
            {
                sameFile = fileOwner;
                c.LibraryMatch = $"Этот файл уже внесён: «{sameFile}»";
            }
            if (session.ExistingKeys.TryGetValue(key, out var existing))
            {
                Error($"В библиотеке уже есть «{existing}».");
                c.LibraryMatch = sameFile is not null ? $"Есть: «{existing}» (тот же файл)" : $"Есть: «{existing}»";
            }
            if (inList && byKey.TryGetValue(key, out var sameKey) && sameKey.Count > 1)
                Error("Обозначение повторяется в списке: " + string.Join(", ", sameKey.Where(x => x != c).Select(x => x.FileName)) + ".");
            if (sameFile is not null)
                Error($"Этот файл уже добавлен в библиотеку как «{sameFile}».");
            if (inList && c.FileHash.Length > 0 && byHash.TryGetValue(c.FileHash, out var sameHash) && sameHash.Count > 1)
                Error("Такой же файл в списке: " + string.Join(", ", sameHash.Where(x => x != c).Select(x => x.FileName)) + ".");

            // Компоненты сборки: в папке библиотеки, в этом же списке или уже внесены из того же файла.
            if (c.DocumentType == LibraryDocumentType.Assembly && c.Properties is { } asm)
            {
                var missing = asm.References.Where(r => !storage.Contains(r) && !bySource.Contains(r)
                    && !(c.ReferenceHashes.TryGetValue(r, out var h) && session.ExistingHashes.ContainsKey(h))).ToList();
                if (missing.Count > 0)
                    Error("Компоненты сборки не в библиотеке и не в списке: "
                          + string.Join(", ", missing.Select(Path.GetFileName)) + ". Добавьте их вместе со сборкой.");
            }

            // Путь в библиотеке
            if (c.TargetRelativePath is { } target)
            {
                if (File.Exists(storage.GetFullPath(target)))
                    Error($"В папке библиотеки уже есть файл {target}.");
                if (inList && byTarget.TryGetValue(target, out var sameTarget) && sameTarget > 1)
                    Error($"Совпадает путь в библиотеке с другим файлом списка: {target}.");
            }
        }
    }
}
