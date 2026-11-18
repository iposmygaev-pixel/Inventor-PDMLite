using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Checks;
using PdmLite.Library.Import;
using PdmLite.Library.Rules;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Список подготовки и хранилище.</summary>
public sealed class ImportListAndStorageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-list3-").FullName;

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("[]")]
    [InlineData("""{"Version":1,"Items":[{"Include":true}]}""")] // нет обязательного Path
    [InlineData("""{"Version":1,"Items":[{"Path":"a","Kind":"Неизвестно"}]}""")]
    public void Load_Broken_InvalidData(string content) =>
        Assert.Throws<InvalidDataException>(() => ImportList.Load(Write("a.pdmlist", content)));

    [Fact]
    public void Load_Null_Empty() =>
        Assert.Contains("пуст", Assert.Throws<InvalidDataException>(() => ImportList.Load(Write("a.pdmlist", "null"))).Message);

    [Fact]
    public void Load_OtherVersion_Rejected() =>
        Assert.Contains("Версия", Assert.Throws<InvalidDataException>(() => ImportList.Load(Write("a.pdmlist", """{"Version":2}"""))).Message);

    [Fact]
    public void Load_NullItemsOrEntries_Tolerated()
    {
        Assert.Empty(ImportList.Load(Write("a.pdmlist", """{"Version":1,"Items":null}""")).Items);
        var list = ImportList.Load(Write("b.pdmlist", """{"Version":1,"Items":[null,{"Path":" "},{"Path":"x.ipt"}]}"""));
        Assert.Equal(["x.ipt"], list.Items.Select(i => i.Path));
    }

    [Fact]
    public void Load_DefaultsForMissingFields()
    {
        var entry = Assert.Single(ImportList.Load(Write("a.pdmlist", """{"Items":[{"Path":"x.ipt"}]}""")).Items);
        Assert.True(entry.Include);
        Assert.Equal("", entry.Designation);
        Assert.False(entry.CategoryChosen);
    }

    [Fact]
    public void Save_Overwrites_NoTempLeft()
    {
        var path = Path.Combine(_root, "a.pdmlist");
        new ImportList { Items = [new ImportListEntry { Path = "1.ipt" }] }.Save(path);
        new ImportList { Items = [new ImportListEntry { Path = "2.ipt", Kind = LibraryItemKind.Purchased }] }.Save(path);
        var loaded = ImportList.Load(path);
        Assert.Equal("2.ipt", Assert.Single(loaded.Items).Path);
        Assert.Equal(LibraryItemKind.Purchased, loaded.Items[0].Kind);
        Assert.Contains("\"Purchased\"", File.ReadAllText(path)); // перечисления — словами
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Storage_Contains_PrefixOfOtherFolder_False()
    {
        var storage = new LibraryStorage(Path.Combine(_root, "Lib"));
        Assert.True(storage.Contains(Path.Combine(_root, "Lib", "a.ipt")));
        Assert.True(storage.Contains(Path.Combine(_root, "LIB", "a.ipt")));
        Assert.False(storage.Contains(Path.Combine(_root, "Lib2", "a.ipt")));
        Assert.False(storage.Contains(Path.Combine(_root, "Lib")));
        Assert.False(storage.Contains(Path.Combine(_root, "Lib", "..", "a.ipt")));
    }

    [Fact]
    public void Storage_RootTrailingSeparator_Trimmed() =>
        Assert.Equal(Path.Combine(_root, "Lib"), new LibraryStorage(Path.Combine(_root, "Lib") + Path.DirectorySeparatorChar).Root);

    [Fact]
    public void Storage_Delete_Missing_NoThrow_ReadOnly_Deleted()
    {
        LibraryStorage.Delete(Path.Combine(_root, "нет.ipt"));
        var file = Write("a.ipt", "x");
        LibraryStorage.MakeReadOnly(file);
        LibraryStorage.Delete(file);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Storage_BackupRestore_KeepsContent_RestoredReadOnly()
    {
        var file = Write("a.ipt", "1");
        LibraryStorage.MakeReadOnly(file);
        var backup = LibraryStorage.Backup(file);
        try
        {
            Assert.False(File.GetAttributes(backup).HasFlag(FileAttributes.ReadOnly));
            LibraryStorage.ClearReadOnly(file);
            File.WriteAllText(file, "2");
            LibraryStorage.Restore(backup, file);
            Assert.Equal("1", File.ReadAllText(file));
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));

            var elsewhere = Path.Combine(_root, "new", "deep", "b.ipt");
            LibraryStorage.Restore(backup, elsewhere); // папки создаются
            Assert.Equal("1", File.ReadAllText(elsewhere));
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public void Storage_RemoveEmptyFolders_StopsAtRootAndAtNonEmpty()
    {
        var storage = new LibraryStorage(Path.Combine(_root, "Lib"));
        var deep = Path.Combine(storage.Root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(storage.Root, "a", "keep.txt"), "x");

        storage.RemoveEmptyFolders(deep);

        Assert.False(Directory.Exists(Path.Combine(storage.Root, "a", "b")));
        Assert.True(Directory.Exists(Path.Combine(storage.Root, "a")));

        File.Delete(Path.Combine(storage.Root, "a", "keep.txt"));
        storage.RemoveEmptyFolders(Path.Combine(storage.Root, "a"));
        Assert.True(Directory.Exists(storage.Root)); // корень остаётся
        storage.RemoveEmptyFolders(Path.Combine(_root, "вне")); // вне библиотеки — ничего
    }

    [Fact]
    public async Task Storage_Hash_StableAndSized()
    {
        var file = Write("a.bin", "abc");
        var (hash, size) = await LibraryStorage.HashAsync(file);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", hash);
        Assert.Equal(3, size);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
