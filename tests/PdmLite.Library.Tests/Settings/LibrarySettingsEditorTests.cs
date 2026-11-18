using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data;
using PdmLite.Hosting;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;
using PdmLite.Modules.Library.Settings;

namespace PdmLite.Library.Tests;

public sealed class LibrarySettingsEditorTests
{
    [Theory]
    [InlineData("database.json")]
    [InlineData("library.json")]
    public void SettingsEditor_FailedSaveDoesNotChangeActiveConnectionOrLibrary(string blocked)
    {
        using var folders = new TempFolders(); var config = Path.Combine(folders.Root,"settings");
        var databaseSettings = new JsonSettings<DatabaseSettings>("database",config); databaseSettings.Value.Host="old-host"; databaseSettings.Save();
        var librarySettings = new JsonSettings<LibrarySettings>("library",config); librarySettings.Value.RootFolder="old-library"; librarySettings.Save();
        var database = new PdmDatabase(databaseSettings.Value,NullLoggerFactory.Instance);
        File.Delete(Path.Combine(config,blocked)); Directory.CreateDirectory(Path.Combine(config,blocked));
        var viewModel = new LibrarySettingsViewModel(database,databaseSettings,librarySettings)
        { Host="new-host", RootFolder=folders.Library, WarnOnContentCenter=false };
        Assert.NotNull(viewModel.Save());
        Assert.Equal("old-host",database.Settings.Host); Assert.Equal("old-host",databaseSettings.Value.Host);
        Assert.Equal("old-library",librarySettings.Value.RootFolder); Assert.True(librarySettings.Value.WarnOnContentCenter);
    }
    [Fact]
    public void SettingsEditor_SuccessPublishesBothSettingsAndKeepsOtherLibraryOptions()
    {
        using var folders = new TempFolders(); var config=Path.Combine(folders.Root,"settings");
        var dbSettings=new JsonSettings<DatabaseSettings>("database",config);
        var librarySettings=new JsonSettings<LibrarySettings>("library",config); librarySettings.Value.BackupKeep=27;
        var database=new PdmDatabase(dbSettings.Value,NullLoggerFactory.Instance);
        var editor=new LibrarySettingsViewModel(database,dbSettings,librarySettings) { Host="new-host",RootFolder=folders.Library,WarnOnContentCenter=false };
        Assert.Null(editor.Save()); Assert.Equal("new-host",database.Settings.Host);
        Assert.Equal(folders.Library,new JsonSettings<LibrarySettings>("library",config).Value.RootFolder);
        Assert.False(librarySettings.Value.WarnOnContentCenter); Assert.Equal(27,librarySettings.Value.BackupKeep);
    }
}
