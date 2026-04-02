using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Commands;
using Xunit;

namespace ImageFolderManager.IntegrationTests;

public sealed class BatchFolderOperationsIntegrationTests : IDisposable
{
    private readonly string _testRoot;

    public BatchFolderOperationsIntegrationTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "ImageFolderManager.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [Fact]
    public async Task BatchCopy_ShouldCopyAllFolders()
    {
        CreateDirectory("copy_src");
        string destinationRoot = CreateDirectory("copy_dest");

        string source1 = CreateDirectory(Path.Combine("copy_src", "FolderA"));
        string source2 = CreateDirectory(Path.Combine("copy_src", "FolderB"));

        CreateFile(source1, "a.txt", "alpha");
        CreateFile(source2, "b.txt", "beta");

        var commands = new List<IFolderCommand>
        {
            new CopyFolderCommand(source1, Path.Combine(destinationRoot, "FolderA")),
            new CopyFolderCommand(source2, Path.Combine(destinationRoot, "FolderB"))
        };

        var batch = new BatchOperationCommand(commands);
        CommandResult result = await batch.ExecuteAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(Path.Combine(destinationRoot, "FolderA")));
        Assert.True(Directory.Exists(Path.Combine(destinationRoot, "FolderB")));
        Assert.Equal("alpha", ReadFile(Path.Combine(destinationRoot, "FolderA", "a.txt")));
        Assert.Equal("beta", ReadFile(Path.Combine(destinationRoot, "FolderB", "b.txt")));
        Assert.True(Directory.Exists(source1));
        Assert.True(Directory.Exists(source2));
    }

    [Fact]
    public async Task BatchMove_ShouldMoveAllFolders()
    {
        CreateDirectory("move_src");
        string destinationRoot = CreateDirectory("move_dest");

        string source1 = CreateDirectory(Path.Combine("move_src", "FolderA"));
        string source2 = CreateDirectory(Path.Combine("move_src", "FolderB"));

        CreateFile(source1, "a.txt", "alpha");
        CreateFile(source2, "b.txt", "beta");

        string destination1 = Path.Combine(destinationRoot, "FolderA");
        string destination2 = Path.Combine(destinationRoot, "FolderB");

        var commands = new List<IFolderCommand>
        {
            new MoveFolderCommand(source1, destination1),
            new MoveFolderCommand(source2, destination2)
        };

        var batch = new BatchOperationCommand(commands);
        CommandResult result = await batch.ExecuteAsync();

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(source1));
        Assert.False(Directory.Exists(source2));
        Assert.True(Directory.Exists(destination1));
        Assert.True(Directory.Exists(destination2));
        Assert.Equal("alpha", ReadFile(Path.Combine(destination1, "a.txt")));
        Assert.Equal("beta", ReadFile(Path.Combine(destination2, "b.txt")));
    }

    [Fact]
    public async Task BatchDelete_ShouldDeleteAllFolders()
    {
        // BatchOperationCommand validates duplicate affected paths and treats them as conflicts.
        // To keep this integration test deterministic, we delete folders under different parents.
        string parent1 = CreateDirectory("delete_parent_1");
        string parent2 = CreateDirectory("delete_parent_2");
        string target1 = CreateDirectory(Path.Combine("delete_parent_1", "FolderA"));
        string target2 = CreateDirectory(Path.Combine("delete_parent_2", "FolderB"));

        CreateFile(target1, "a.txt", "alpha");
        CreateFile(target2, "b.txt", "beta");

        var commands = new List<IFolderCommand>
        {
            new DeleteFolderCommand(target1, useRecycleBin: false),
            new DeleteFolderCommand(target2, useRecycleBin: false)
        };

        var batch = new BatchOperationCommand(commands);
        CommandResult result = await batch.ExecuteAsync();

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(target1));
        Assert.False(Directory.Exists(target2));
    }

    [Fact]
    public async Task BatchCopy_WhenCancelled_ShouldNotCopy()
    {
        string source = CreateDirectory(Path.Combine("cancel_copy", "SourceFolder"));
        string destinationRoot = CreateDirectory(Path.Combine("cancel_copy", "DestRoot"));
        CreateFile(source, "x.txt", "payload");

        string destination = Path.Combine(destinationRoot, "SourceFolder");
        var batch = new BatchOperationCommand(new[]
        {
            (IFolderCommand)new CopyFolderCommand(source, destination)
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        CommandResult result = await batch.ExecuteAsync(cts.Token);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(destination));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public async Task BatchMove_WhenCancelled_ShouldNotMove()
    {
        string source = CreateDirectory(Path.Combine("cancel_move", "SourceFolder"));
        string destinationRoot = CreateDirectory(Path.Combine("cancel_move", "DestRoot"));
        CreateFile(source, "x.txt", "payload");

        string destination = Path.Combine(destinationRoot, "SourceFolder");
        var batch = new BatchOperationCommand(new[]
        {
            (IFolderCommand)new MoveFolderCommand(source, destination)
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        CommandResult result = await batch.ExecuteAsync(cts.Token);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(destination));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public async Task BatchDelete_WhenCancelled_ShouldNotDelete()
    {
        string targetParent = CreateDirectory(Path.Combine("cancel_delete", "Parent"));
        string target = CreateDirectory(Path.Combine("cancel_delete", "Parent", "Target"));
        CreateFile(target, "x.txt", "payload");

        var batch = new BatchOperationCommand(new[]
        {
            (IFolderCommand)new DeleteFolderCommand(target, useRecycleBin: false)
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        CommandResult result = await batch.ExecuteAsync(cts.Token);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(targetParent));
        Assert.True(Directory.Exists(target));
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
            // Ignore cleanup failures on CI/Windows file-lock races.
        }
    }

    private string CreateDirectory(string relativePath)
    {
        string fullPath = Path.Combine(_testRoot, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static void CreateFile(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    private static string ReadFile(string filePath)
    {
        return File.ReadAllText(filePath);
    }
}
