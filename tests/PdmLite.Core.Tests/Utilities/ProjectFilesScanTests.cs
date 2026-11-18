using System.Collections;
using ShortenNames;

namespace PdmLite.Core.Tests;

public class ProjectFilesScanTests
{
    public sealed class Collection(object[] values, int readable = int.MaxValue, bool failIndexer = false) : IEnumerable
    {
        public int Count => values.Length;
        [System.Runtime.CompilerServices.IndexerName("At")]
        public object this[int index] => failIndexer ? throw new IOException("indexer failed") : Read(index);
        public object Item(int index) => Read(index);
        private object Read(int index) => index <= readable ? values[index - 1] : throw new IOException("broken descriptor");
        public IEnumerator GetEnumerator()
        {
            for (var i = 1; i <= Count; i++) yield return this[i];
        }
    }
    public sealed class Descriptor(string? full, string? relative = null)
    {
        public string? FullFileName => full;
        public string? RelativeFileName => relative;
        public string? LogicalFileName => null;
    }
    public sealed class FileData(Collection descriptors) { public Collection ReferencedFileDescriptors => descriptors; }
    public sealed class Document(Collection descriptors, Collection fallback, bool failPrimary = false)
    {
        public FileData File => failPrimary ? throw new IOException("primary failure") : new(descriptors);
        public Collection ReferencedDocuments => fallback;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_ReportsInaccessibleDirectory_AndContinuesOtherDirectories(bool disappears)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var blocked = root.CreateSubdirectory("blocked").FullName;
            var good = root.CreateSubdirectory("good").FullName;
            var path = Path.Combine(good, "very-long-name.ipt");
            File.WriteAllText(path, "p");
            var scan = ProjectFiles.Scan(root.FullName, folder => folder == blocked
                ? throw (disappears ? new DirectoryNotFoundException("gone") : new UnauthorizedAccessException("denied"))
                : Directory.EnumerateFileSystemEntries(folder));
            Assert.Equal([path], scan.Files);
            Assert.Contains(blocked, Assert.Single(scan.Warnings));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Scan_PartialEnumeration_KeepsAlreadyFoundFilesAndReportsFolder()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var part = Path.Combine(root.FullName, "part.ipt");
            File.WriteAllText(part, "p");
            IEnumerable<string> Entries(string folder)
            {
                yield return part;
                throw new IOException("enumeration failed");
            }
            var scan = ProjectFiles.Scan(root.FullName, Entries);
            Assert.Equal([part], scan.Files);
            Assert.Contains(root.FullName, Assert.Single(scan.Warnings));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Scan_EmptyAccessibleFolder_HasNoWarnings()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var scan = ProjectFiles.Scan(root.FullName);
            Assert.Empty(scan.Files);
            Assert.Empty(scan.Warnings);
        }
        finally { root.Delete(true); }
    }
}
