using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;
using Xunit;

namespace ImageFolderManager.IntegrationTests;

public sealed class TagConcurrencyIntegrationTests : IDisposable
{
    private readonly string _testRoot;

    public TagConcurrencyIntegrationTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "ImageFolderManager.TagConcurrencyTests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_testRoot);
    }

    [Fact]
    public void MetadataRequestOrdering_ShouldRejectOutdatedRequest()
    {
        string configDir = CreateDirectory("metadata_config");
        var categoryService = new TagCategoryService(configDir);
        var tagService = new FolderTagService(categoryService);
        var tagCloud = new TagCloudViewModel(categoryService);
        var viewModel = new TagManagementViewModel(tagService, tagCloud, dialogService: new TestDialogService());

        var folderA = new FolderInfo(CreateDirectory("metadata_folder_a"));
        var folderB = new FolderInfo(CreateDirectory("metadata_folder_b"));

        long requestA = viewModel.BeginMetadataLoadRequest(folderA);
        long requestB = viewModel.BeginMetadataLoadRequest(folderB);

        Assert.False(viewModel.IsMetadataLoadRequestCurrent(requestA, folderA));
        Assert.True(viewModel.IsMetadataLoadRequestCurrent(requestB, folderB));
    }

    [Fact]
    public async Task TagCategoryService_ShouldRemainConsistentUnderConcurrentWrites()
    {
        string configDir = CreateDirectory("category_service");
        var categoryService = new TagCategoryService(configDir);
        var exceptions = new ConcurrentQueue<Exception>();

        var workers = Enumerable.Range(0, 12).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 250; i++)
            {
                try
                {
                    string tagName = $"tag_{worker}_{i % 40}";
                    string categoryName = $"category_{(worker + i) % 10}";

                    switch (i % 4)
                    {
                        case 0:
                            categoryService.SetTagCategory(tagName, categoryName);
                            break;
                        case 1:
                            categoryService.MoveTagsToCategory(
                                new[] { tagName, $"shared_{i % 20}" },
                                $"batch_{worker % 4}");
                            break;
                        case 2:
                            categoryService.AddCategory($"extra_{i % 6}");
                            break;
                        default:
                            categoryService.CleanupUnusedTagMappings(new[]
                            {
                                tagName,
                                $"shared_{i % 20}",
                                $"tag_{worker}_{(i + 1) % 40}"
                            });
                            break;
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.True(exceptions.IsEmpty, BuildExceptionSummary(exceptions));

        string mappingsPath = Path.Combine(configDir, "tagCategories.json");
        string categoriesPath = Path.Combine(configDir, "categories.json");

        Assert.True(File.Exists(mappingsPath));
        Assert.True(File.Exists(categoriesPath));

        var mappings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mappingsPath));
        var categories = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(categoriesPath));

        Assert.NotNull(mappings);
        Assert.NotNull(categories);
        Assert.Contains("Uncategorized", categories, StringComparer.OrdinalIgnoreCase);

        // Additional sanity check: read APIs should remain stable after concurrent writes.
        var allCategories = categoryService.GetAllCategories();
        Assert.Contains("Uncategorized", allCategories, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FolderTagService_ShouldKeepTagFilesValidUnderConcurrentMutations()
    {
        string configDir = CreateDirectory("folder_tag_service_config");
        var categoryService = new TagCategoryService(configDir);
        var tagService = new FolderTagService(categoryService);

        var folderPaths = Enumerable.Range(0, 5)
            .Select(i => CreateDirectory(Path.Combine("folder_tag_service", $"Folder_{i}")))
            .ToList();

        for (int i = 0; i < folderPaths.Count; i++)
        {
            await tagService.SetTagsAndRatingForFolderAsync(
                folderPaths[i],
                new List<string> { "hot", "obsolete", $"seed_{i}" },
                i % 6);
        }

        var exceptions = new ConcurrentQueue<Exception>();

        var workers = Enumerable.Range(0, 10).Select(worker => Task.Run(async () =>
        {
            for (int i = 0; i < 80; i++)
            {
                try
                {
                    int operation = (worker + i) % 3;
                    string targetFolder = folderPaths[(worker + i) % folderPaths.Count];

                    if (operation == 0)
                    {
                        var tags = new List<string>
                        {
                            $"worker_{worker}",
                            $"round_{i % 12}",
                            "hot",
                            $"Team{worker % 3}::topic_{i % 7}"
                        };

                        await tagService.SetTagsAndRatingForFolderAsync(
                            targetFolder,
                            tags,
                            (worker + i) % 6);
                    }
                    else if (operation == 1)
                    {
                        await tagService.RenameTagAsync(
                            "hot",
                            $"hot_{worker}_{i % 5}",
                            folderPaths,
                            "People");
                    }
                    else
                    {
                        await tagService.DeleteTagFromAllFoldersAsync("obsolete", folderPaths);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);
        Assert.True(exceptions.IsEmpty, BuildExceptionSummary(exceptions));

        foreach (var folderPath in folderPaths)
        {
            string tagFilePath = Path.Combine(folderPath, ".folderTags");
            if (File.Exists(tagFilePath))
            {
                string content = File.ReadAllText(tagFilePath);
                Assert.Equal(2, content.Split('|').Length);
            }

            var tags = await tagService.GetTagsWithCategoriesForFolderAsync(folderPath);
            int rating = await tagService.GetRatingForFolderAsync(folderPath);

            Assert.InRange(rating, 0, 5);
            Assert.All(tags, t => Assert.False(string.IsNullOrWhiteSpace(t.TagName)));
            Assert.Equal(
                tags.Select(t => t.TagName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                tags.Count);
        }
    }

    [Fact]
    public async Task TagCloudViewModel_ShouldApplyLatestUpdateOnly()
    {
        string configDir = CreateDirectory("tag_cloud_latest_wins");
        await RunOnDispatcherThreadAsync(async dispatcher =>
        {
            var categoryService = new TagCategoryService(configDir);
            var viewModel = new TagCloudViewModel(categoryService);

            var slowFolders = Enumerable.Range(0, 180)
                .Select(i => new FolderInfo(CreateDirectory(Path.Combine("cloud_slow", $"Folder_{i}")))
                {
                    Tags = new System.Collections.ObjectModel.ObservableCollection<string> { "OldTag" }
                })
                .ToList();

            var fastFolders = new List<FolderInfo>
            {
                new FolderInfo(CreateDirectory(Path.Combine("cloud_fast", "Folder_A")))
                {
                    Tags = new System.Collections.ObjectModel.ObservableCollection<string> { "NewTag" }
                }
            };

            var slowUpdate = viewModel.UpdateTagCloudAsync(new DelayedFolderEnumerable(slowFolders, delayMs: 2));
            await Task.Delay(15);
            var fastUpdate = viewModel.UpdateTagCloudAsync(fastFolders);

            await Task.WhenAll(slowUpdate, fastUpdate);

            var finalTags = viewModel.GetTagsInCategory(TagCloudViewModel.DEFAULT_CATEGORY)
                .Select(t => t.Tag)
                .ToList();

            Assert.Contains("NewTag", finalTags, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(finalTags, t => t.Equals("OldTag", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void TagCategoryService_DefaultCategoryDeletion_ShouldBeCaseInsensitiveNoOp()
    {
        string configDir = CreateDirectory("default_category_case");
        var categoryService = new TagCategoryService(configDir);

        categoryService.SetTagCategory("sample", "TeamA");
        categoryService.RemoveCategory("uNcAtEgOrIzEd");

        Assert.Equal("TeamA", categoryService.GetTagCategory("sample"));
        Assert.Contains("Uncategorized", categoryService.GetAllCategories(), StringComparer.OrdinalIgnoreCase);
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
            // Best-effort cleanup for CI/local lock contention.
        }
    }

    private string CreateDirectory(string relativePath)
    {
        string fullPath = Path.Combine(_testRoot, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static string BuildExceptionSummary(ConcurrentQueue<Exception> exceptions)
    {
        if (exceptions.IsEmpty)
            return string.Empty;

        return string.Join(
            Environment.NewLine,
            exceptions.Select(e => $"{e.GetType().Name}: {e.Message}").Distinct());
    }

    private static async Task RunOnDispatcherThreadAsync(Func<Dispatcher, Task> action)
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            ready.SetResult(dispatcher);

            // Start the message loop for Dispatcher.InvokeAsync calls used by ViewModels.
            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        var uiDispatcher = await ready.Task;

        try
        {
            var operation = uiDispatcher.InvokeAsync(() => action(uiDispatcher));
            await operation.Task.Unwrap();
            completion.SetResult(new object());
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
        finally
        {
            uiDispatcher.InvokeShutdown();
            thread.Join(TimeSpan.FromSeconds(5));
        }

        await completion.Task;
    }

    private sealed class DelayedFolderEnumerable : IEnumerable<FolderInfo>
    {
        private readonly IReadOnlyCollection<FolderInfo> _folders;
        private readonly int _delayMs;

        public DelayedFolderEnumerable(IReadOnlyCollection<FolderInfo> folders, int delayMs)
        {
            _folders = folders ?? Array.Empty<FolderInfo>();
            _delayMs = Math.Max(0, delayMs);
        }

        public IEnumerator<FolderInfo> GetEnumerator()
        {
            foreach (var folder in _folders)
            {
                if (_delayMs > 0)
                {
                    Thread.Sleep(_delayMs);
                }

                yield return folder;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TestDialogService : IDialogService
    {
        public MessageBoxResult Show(
            string message,
            string title,
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None)
        {
            return MessageBoxResult.OK;
        }
    }
}
