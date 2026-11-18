using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Checks;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Индекс путей библиотеки и подстановка ссылок.</summary>
public class PathIndexTheoryTests
{
    private static readonly Guid Part = Guid.NewGuid();
    private static readonly Guid Assembly = Guid.NewGuid();

    private static LibraryPathIndex Index() => new(
        [
            (Part, "Электротехника/Разъёмы/ABB/Разъём X1.ipt", "Разъём X1"),
            (Assembly, "Электротехника/Разъёмы/ABB/Разъём X2.iam", "Разъём X2"),
            (Guid.NewGuid(), "А/Гайка M8.ipt", "Гайка M8 ГОСТ 5915-70"),
            (Guid.NewGuid(), "Б/Гайка M8.ipt", "Гайка M8 ГОСТ 5927-70"),
        ], []);

    [Fact]
    public void ByDesignation_OnlySameExtension()
    {
        var index = Index();
        Assert.Equal("Электротехника/Разъёмы/ABB/Разъём X1.ipt", index.Find(@"D:\old\разъём  x1.ipt", missing: true));
        Assert.Null(index.Find(@"D:\old\Разъём X1.iam", missing: true)); // сборку деталью не подменяем
        Assert.Null(index.Find(@"D:\old\Разъём X2.ipt", missing: true));
    }

    [Fact]
    public void SameFileNameTwice_Ambiguous_NotGuessed() =>
        Assert.Null(Index().Find(@"D:\old\Гайка M8.ipt", missing: true));

    [Fact]
    public void NotMissing_NoGuessByName() =>
        Assert.Null(Index().Find(@"D:\old\Разъём X1.ipt", missing: false));

    [Fact]
    public void Empty_FindsNothing()
    {
        Assert.Null(LibraryPathIndex.Empty.Find("a.ipt", true));
        Assert.Equal(0, LibraryPathIndex.Empty.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(@"\\")]
    [InlineData("C:")]
    public void Degenerate_References_NoThrow(string reference) => Assert.Null(Index().Find(reference, true));

    [Fact]
    public void CurrentPath_UnderOtherRoot_Found()
    {
        Assert.Equal("Электротехника/Разъёмы/ABB/Разъём X2.iam",
            Index().Find(@"E:\Другая библиотека\Электротехника\Разъёмы\ABB\Разъём X2.iam", missing: true));
    }

    private static LibraryLogEntry Log(string action, Guid? id, string? details, int minute) =>
        new() { User = "u", Action = action, ItemId = id, Details = details, At = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc) };

    [Fact]
    public void MovesFromLog_SkipsBrokenAndIrrelevant_OrdersByTime()
    {
        var id = Guid.NewGuid();
        var moves = LibraryPathIndex.MovesFromLog(
        [
            Log("edit", id, """{"before":{"RelativePath":"b.ipt"},"after":{"RelativePath":"c.ipt"}}""", 2),
            Log("edit", id, """{"before":{"RelativePath":"a.ipt"},"after":{"RelativePath":"b.ipt"}}""", 1),
            Log("edit", id, """{"before":{"RelativePath":"same.ipt"},"after":{"RelativePath":"SAME.ipt"}}""", 3), // регистр — не переезд
            Log("edit", id, "не json", 4),
            Log("edit", id, """{"before":{"RelativePath":5}}""", 5),
            Log("edit", null, """{"before":{"RelativePath":"x.ipt"}}""", 6),
            Log("add", id, """{"RelativePath":"y.ipt"}""", 7),
            Log("merge", id, """{"RelativePath":"dup.ipt"}""", 8),
            Log("merge", id, """{"RelativePath":null}""", 9),
            Log("edit", id, null, 10),
        ]).ToList();

        Assert.Equal([("a.ipt", id), ("b.ipt", id), ("dup.ipt", id)], moves);
    }

    [Fact]
    public void Moves_ChainResolvesToCurrent_OldPathTakenByOther_Ignored()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var index = new LibraryPathIndex(
            [(a, "new/a.ipt", "A 1"), (b, "old/a.ipt", "B 1")],
            [("old/a.ipt", a), ("older/a.ipt", a)]);
        Assert.Equal("new/a.ipt", index.Find("older/a.ipt", false));
        Assert.Null(index.Find("other/q.ipt", false));
        Assert.Equal("old/a.ipt", index.Find(@"X:\lib\old\a.ipt", true)); // на старом пути теперь другой компонент
    }

    [Fact]
    public void Resolver_OtherProjectLibrary_NotTouched()
    {
        var index = new LibraryPathIndex([(Guid.NewGuid(), "Крепёж/a.ipt", "A 1")], [("old/a.ipt", Guid.NewGuid())]);
        var root = Path.Combine(Path.GetTempPath(), "lib");
        Assert.Null(LibraryReferenceResolver.Resolve(index, root, "Крепёж/a.ipt", "Чужая", "PdmLite", _ => true));
    }

    [Fact]
    public void Resolver_ExistingFile_NotReplaced()
    {
        var index = new LibraryPathIndex([(Guid.NewGuid(), "Крепёж/a.ipt", "A 1")], []);
        var root = Path.Combine(Path.GetTempPath(), "lib");
        var reference = Path.Combine(Path.GetTempPath(), "proj", "a.ipt");
        Assert.Null(LibraryReferenceResolver.Resolve(index, root, reference, null, "PdmLite", _ => true));
    }

    [Fact]
    public void Resolver_TargetFileGone_Null()
    {
        var index = new LibraryPathIndex([(Guid.NewGuid(), "Крепёж/a.ipt", "A 1")], []);
        var root = Path.Combine(Path.GetTempPath(), "lib");
        var reference = Path.Combine(Path.GetTempPath(), "old", "a.ipt");
        Assert.Null(LibraryReferenceResolver.Resolve(index, root, reference, null, "PdmLite", _ => false));
    }

    [Fact]
    public void Resolver_MissingRooted_ResolvedToFull()
    {
        var index = new LibraryPathIndex([(Guid.NewGuid(), "Крепёж/a.ipt", "A 1")], []);
        var root = Path.Combine(Path.GetTempPath(), "lib");
        var full = Path.Combine(root, "Крепёж", "a.ipt");
        var reference = Path.Combine(Path.GetTempPath(), "old", "a.ipt");
        Assert.Equal(full, LibraryReferenceResolver.Resolve(index, root, reference, null, "PdmLite", p => p == full));
    }

    [Theory]
    [InlineData("a.ipt", null, false)]
    [InlineData("a.ipt", "PdmLite", true)]
    [InlineData("a.ipt", "pdmlite", true)]
    [InlineData("a.ipt", "Чужая", false)]
    public void ShouldReload(string reference, string? library, bool expected) =>
        Assert.Equal(expected, LibraryReferenceResolver.ShouldReload(reference, library, "PdmLite"));

    [Fact]
    public void ShouldReload_Rooted() =>
        Assert.True(LibraryReferenceResolver.ShouldReload(Path.Combine(Path.GetTempPath(), "a.ipt"), null, "PdmLite"));
}
