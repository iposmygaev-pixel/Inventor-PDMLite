using PdmLite.Core.Domain;
using PdmLite.Core.Projects;
using PdmLite.Explorer;

namespace PdmLite.Core.Tests;

public sealed class ExplorerProjectSwitchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdm-review-").FullName;
    private string Add(string relative, string contents)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); return path;
    }

    [Fact]
    public async Task Explorer_ExplicitDifferentIpjWithSameWorkspaceSwitchesProject()
    {
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        var a = Add("A.ipj", "<Project><WorkspacePath>workspace</WorkspacePath></Project>");
        var b = Add("B.ipj", "<Project><WorkspacePath>workspace</WorkspacePath></Project>");
        var session = new Session(ProjectInfo.FromPath(a));
        await ExplorerLinkServer.OpenProjectIfNeededAsync(session, b, CancellationToken.None);
        Assert.Equal(b, session.Project!.ProjectFile); Assert.Equal("B", session.Project.Name); Assert.Equal(1, session.Opens);
    }

    [Fact]
    public async Task Explorer_SameIpjOrFolderDoesNotDiscardMoreSpecificProject()
    {
        var a = Add("A.ipj", "<Project/>"); var current = ProjectInfo.FromPath(a); var session = new Session(current);
        await ExplorerLinkServer.OpenProjectIfNeededAsync(session, a, CancellationToken.None);
        await ExplorerLinkServer.OpenProjectIfNeededAsync(session, _root, CancellationToken.None);
        Assert.Same(current, session.Project); Assert.Equal(0, session.Opens);
    }
    private sealed class Session(ProjectInfo project) : IProjectSession
    {
        public ProjectInfo? Project { get; private set; } = project;
        public ProjectItem? Root => null;
        public int Opens { get; private set; }
        public Task OpenAsync(ProjectInfo value, CancellationToken cancellationToken = default)
        { Project = value; Opens++; return Task.CompletedTask; }
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ProjectItem? Find(string path) => null;
    }
    public void Dispose() => Directory.Delete(_root, true);
}
