using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Checks;
using PdmLite.Library.Items;

namespace PdmLite.Library.Tests;

/// <summary>Подбор компонента библиотеки по составным названиям стандартов.</summary>
public class LibraryMatcherStandardNameTests
{
    [Fact]
    public void LibraryMatcher_CompoundStandardNames()
    {
        static LibraryItemSummary Item(string designation) =>
            new(Guid.NewGuid(), designation, LibraryItemKind.Standard, LibraryDocumentType.Part, null, null, 1, "Крепёж/Винты",
                $"{designation}.ipt", 1, DateTime.UtcNow, "test");
        var screw = Item("Винт M6x20 ГОСТ Р ИСО 4762-2012");
        var matcher = new LibraryMatcher([screw, Item("Винт M6x20 ГОСТ 11738-84")]);

        var found = matcher.Find("Винт ГОСТ Р ИСО 4762-2012 M6x20");

        Assert.Same(screw, Assert.Single(found).Item); // стандарт с пробелами и годом — тот же; другой ГОСТ — нет
    }
}
