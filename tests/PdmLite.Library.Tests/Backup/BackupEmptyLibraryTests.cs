using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Index;
using PdmLite.Library.Backup;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Резервные копии: экранирование имён SQL и копия пустой библиотеки.</summary>
public class BackupEmptyLibraryTests
{
    [Theory]
    [InlineData("items", "\"items\"")]
    [InlineData("we\"ird", "\"we\"\"ird\"")]
    public void Backup_QuoteIdentifier(string name, string expected) =>
        Assert.Equal(expected, LibraryRestoreService.QuoteIdentifier(name));

    [PostgresFact]
    public async Task Backup_EmptyLibrary_CreateAndRestore()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var backups = new LibraryBackupService(db.Database, () => new LibraryStorage(folders.Library),
            () => new BackupOptions(Path.Combine(folders.Root, "Backups"), 10, 7), NullLogger<LibraryBackupService>.Instance);
        Assert.False(Directory.Exists(folders.Library)); // папки библиотеки ещё нет

        var empty = await backups.CreateAsync(BackupReason.Manual);
        Assert.Equal(0, empty.Manifest.Items);

        await backups.Restorer().RestoreAsync(empty.Path);
        Assert.True(Directory.Exists(folders.Library));
        Assert.Equal(2, backups.List().Count); // ручная и «перед восстановлением»
    }
}
