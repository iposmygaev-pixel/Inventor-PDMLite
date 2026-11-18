using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Index;
using PdmLite.Library.Backup;
using PdmLite.Library.Checks;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Имена вхождений компонентов со специальными символами.</summary>
public class OccurrenceNamingSpecialCharactersTests
{
    [Theory]
    [InlineData("Болт M8-6gx30.109(S13) ГОСТ 7798-70:1", "Болт M8-6gx30.109(S13) ГОСТ 7798-70", true)]
    [InlineData("Болт M8-6gx30.109(S13) ГОСТ 7798-70:1", "Болт M8-6gx30.109(S1) ГОСТ 7798-70", false)] // скобки — не шаблон
    [InlineData("Винт [c]+:2", "Винт [c]+", true)]
    public void OccurrenceNaming_SpecialCharacters(string name, string baseName, bool expected) =>
        Assert.Equal(expected, PdmLite.Core.Documents.OccurrenceNaming.IsDefault(name, baseName));
}
