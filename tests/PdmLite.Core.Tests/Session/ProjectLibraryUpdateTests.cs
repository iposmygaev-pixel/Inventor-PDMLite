using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;

namespace PdmLite.Core.Tests;

public class ProjectLibraryUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Binding_AddOrVerificationFailureRestoresPreviousPath(bool wrongPath)
    {
        var old = Path.GetFullPath("old-library"); var next = Path.GetFullPath("new-library"); string? current = old;
        Assert.Throws<InvalidOperationException>(() => ProjectLibraryUpdate.Apply(next, () => current, () => current = null, path =>
        {
            if (path == next) { if (!wrongPath) throw new InvalidOperationException("Add failed"); current = Path.GetFullPath("wrong-library"); }
            else current = path;
        }));
        Assert.Equal(old, current);
    }

    [Fact]
    public void Binding_FailedRestoreReportsBothFailuresAndOldPath()
    {
        var old = Path.GetFullPath("old-library"); string? current = old;
        var error = Assert.Throws<AggregateException>(() => ProjectLibraryUpdate.Apply(Path.GetFullPath("new-library"),
            () => current, () => current = null, _ => throw new IOException("Add failed")));
        Assert.Equal(2, error.InnerExceptions.Count); Assert.Contains(old, error.Message); Assert.Null(current);
    }

    [Fact]
    public void Binding_SamePathDoesNotDeleteExistingEntry()
    {
        var path = Path.GetFullPath("library");
        ProjectLibraryUpdate.Apply(path, () => path, () => throw new Exception("Must not delete"), _ => throw new Exception("Must not add"));
    }

    [Fact]
    public void Binding_NoPreviousPathCleansPartiallyAddedEntry()
    {
        string? current = null; var path = Path.GetFullPath("new-library");
        Assert.Throws<IOException>(() => ProjectLibraryUpdate.Apply(path, () => current, () => current = null,
            value => { current = value; throw new IOException("Partial add failed"); }));
        Assert.Null(current);
    }
}
