using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Hosting;
using PdmLite.Library.Catalog;
using PdmLite.Library.Storage;
using PdmLite.Modules.Library.Settings;

namespace PdmLite.Library.Tests;

public sealed class CatalogInitializationTests
{
    [PostgresFact]
    public async Task ConcurrentCatalogInitialization_IndependentConnectionsCreateExactlyOneCatalog()
    {
        await using var db = await TestDatabase.CreateAsync(); await db.Database.MigrateAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeds = Enumerable.Range(0,8).Select(async _ =>
        {
            await using var context = db.Database.CreateContext(); await start.Task;
            await LibraryCatalog.SeedAsync(context);
        }).ToArray();
        start.SetResult(); await Task.WhenAll(seeds);
        await using var verify = db.Database.CreateContext();
        var catalog = await LibraryCatalog.LoadAsync(verify); var expected = catalog.Categories.Select(c => c.Path).Distinct().Count();
        Assert.Equal(expected, catalog.Categories.Count); Assert.True(expected > 10);
        var rules = await verify.LibraryCategoryRules.Select(r => new {r.Kind,r.Pattern}).ToListAsync();
        Assert.Equal(rules.Distinct().Count(),rules.Count);
        await LibraryCatalog.SeedAsync(verify); Assert.Equal(expected,await verify.LibraryCategories.CountAsync());
    }
}
