using System;
using System.IO;
using System.Linq;
using ImageFolderManager.Services;
using Xunit;

namespace ImageFolderManager.IntegrationTests;

public sealed class SmartFolderClassificationServiceTests : IDisposable
{
    private readonly string _testRoot;

    public SmartFolderClassificationServiceTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "ImageFolderManager.SmartClassifyTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [Fact]
    public void BuildPlan_ShouldPrioritizeExistingAuthorDirectories()
    {
        CreateDirectory("[Alice]");
        CreateDirectory("[Bob]");
        CreateDirectory("Alice SummerTrip");
        CreateDirectory("Trip [Bob] Tokyo");
        CreateDirectory("LooseFolder");

        var service = new SmartFolderClassificationService();
        SmartFolderClassificationPlan plan = service.BuildPlan(_testRoot);

        Assert.Equal(5, plan.ScannedTopLevelDirectoryCount);
        Assert.Equal(2, plan.ExistingAuthorDirectoryCount);
        Assert.Equal(3, plan.Moves.Count);

        var aliceMove = plan.Moves.Single(m => m.SourceFolderName.Equals("Alice SummerTrip", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("[Alice]", aliceMove.TargetParentDirectoryName);
        Assert.StartsWith("[Alice]", aliceMove.TargetFolderName, StringComparison.OrdinalIgnoreCase);

        var bobMove = plan.Moves.Single(m => m.SourceFolderName.Equals("Trip [Bob] Tokyo", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("[Bob]", bobMove.TargetParentDirectoryName);
        Assert.StartsWith("[Bob]", bobMove.TargetFolderName, StringComparison.OrdinalIgnoreCase);

        var looseMove = plan.Moves.Single(m => m.SourceFolderName.Equals("LooseFolder", StringComparison.OrdinalIgnoreCase));
        Assert.True(looseMove.IsUnclassified);
        Assert.Equal(SmartFolderClassificationService.UnclassifiedDirectoryName, looseMove.TargetParentDirectoryName);
    }

    [Fact]
    public void BuildPlan_ShouldNotDuplicateBracketPrefixInTargetFolderName()
    {
        CreateDirectory("[Alice]");
        CreateDirectory("[Alice]Beach");

        var service = new SmartFolderClassificationService();
        SmartFolderClassificationPlan plan = service.BuildPlan(_testRoot);

        var move = plan.Moves.Single(m => m.SourceFolderName.Equals("[Alice]Beach", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("[Alice]", move.TargetParentDirectoryName);
        Assert.Equal("[Alice]Beach", move.TargetFolderName);
    }

    [Fact]
    public void BuildPlan_WithoutAuthorSignal_ShouldRouteToUnclassified()
    {
        CreateDirectory("NatureShots");

        var service = new SmartFolderClassificationService();
        SmartFolderClassificationPlan plan = service.BuildPlan(_testRoot);

        var move = Assert.Single(plan.Moves);
        Assert.True(move.IsUnclassified);
        Assert.Equal(SmartFolderClassificationService.UnclassifiedDirectoryName, move.TargetParentDirectoryName);
        Assert.Equal("NatureShots", move.TargetFolderName);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup races on CI/Windows file locks.
        }
    }

    private void CreateDirectory(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, relativePath));
    }
}
