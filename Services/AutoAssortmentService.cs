using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ImageFolderManager.Services
{
    public sealed class AutoAssortmentService
    {
        private const int MinimumAuthorFragmentLength = 5;

        public AutoAssortmentPlan BuildPlan(string rootDirectory, string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("Root directory cannot be empty.", nameof(rootDirectory));
            if (string.IsNullOrWhiteSpace(sourceDirectory))
                throw new ArgumentException("Source directory cannot be empty.", nameof(sourceDirectory));

            string normalizedRoot = PathService.NormalizePath(rootDirectory);
            string normalizedSource = PathService.NormalizePath(sourceDirectory);

            if (!Directory.Exists(normalizedRoot))
                throw new DirectoryNotFoundException($"Root directory not found: {normalizedRoot}");
            if (!Directory.Exists(normalizedSource))
                throw new DirectoryNotFoundException($"Source directory not found: {normalizedSource}");
            if (!PathService.IsPathWithin(normalizedRoot, normalizedSource))
                throw new InvalidOperationException("The selected source folder must be inside the current root directory.");
            if (PathService.PathsEqual(normalizedRoot, normalizedSource))
                throw new InvalidOperationException("Please select a subfolder under the root directory, not the root itself.");

            var authorTargets = DiscoverAuthorTargets(normalizedRoot, normalizedSource);
            var authorTargetsByName = authorTargets
                .GroupBy(target => target.AuthorName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            var foldersToClassify = Directory.GetDirectories(normalizedSource, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = new List<AutoAssortmentPlanItem>(foldersToClassify.Count);
            foreach (string folderPath in foldersToClassify)
            {
                items.Add(BuildPlanItem(normalizedRoot, folderPath, authorTargetsByName));
            }

            return new AutoAssortmentPlan(normalizedRoot, normalizedSource, authorTargets, items);
        }

        private static List<AutoAssortmentAuthorTarget> DiscoverAuthorTargets(string rootDirectory, string sourceDirectory)
        {
            var authorTargets = new List<AutoAssortmentAuthorTarget>();
            foreach (string directoryPath in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories))
            {
                if (PathService.IsPathWithin(sourceDirectory, directoryPath))
                    continue;

                if (!SmartFolderClassificationService.TryParseAuthorDirectoryName(
                        Path.GetFileName(directoryPath),
                        out string authorName))
                {
                    continue;
                }

                authorTargets.Add(new AutoAssortmentAuthorTarget(
                    authorName,
                    PathService.NormalizePath(directoryPath),
                    BuildRelativePath(rootDirectory, directoryPath)));
            }

            return authorTargets
                .OrderBy(target => target.AuthorName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => GetPathDepth(target.DirectoryPath))
                .ThenBy(target => target.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static AutoAssortmentPlanItem BuildPlanItem(
            string rootDirectory,
            string sourceFolderPath,
            IReadOnlyDictionary<string, List<AutoAssortmentAuthorTarget>> authorTargetsByName)
        {
            var fileNames = Directory.EnumerateFiles(sourceFolderPath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateMatches = new List<AutoAssortmentCandidateTarget>();
            foreach (var kvp in authorTargetsByName)
            {
                AutoAssortmentAuthorMatch bestMatch = null;
                foreach (string fileName in fileNames)
                {
                    var match = EvaluateMatch(kvp.Key, fileName);
                    if (match == null)
                        continue;

                    if (bestMatch == null ||
                        match.Score > bestMatch.Score ||
                        (match.Score == bestMatch.Score && match.FragmentLength > bestMatch.FragmentLength))
                    {
                        bestMatch = match;
                    }
                }

                if (bestMatch == null)
                    continue;

                foreach (var target in kvp.Value)
                {
                    candidateMatches.Add(new AutoAssortmentCandidateTarget(
                        target,
                        bestMatch.Score,
                        bestMatch.Reason,
                        bestMatch.MatchedFileName,
                        bestMatch.MatchedFragment));
                }
            }

            candidateMatches = candidateMatches
                .OrderByDescending(match => match.Score)
                .ThenBy(target => GetPathDepth(target.Target.DirectoryPath))
                .ThenBy(target => target.Target.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string sourceFolderName = Path.GetFileName(sourceFolderPath);
            string relativeSourcePath = BuildRelativePath(rootDirectory, sourceFolderPath);
            string summary = candidateMatches.Count == 0
                ? "No author match found in file names"
                : candidateMatches[0].BuildSummary();

            return new AutoAssortmentPlanItem(
                PathService.NormalizePath(sourceFolderPath),
                sourceFolderName,
                relativeSourcePath,
                fileNames.Count,
                candidateMatches,
                summary);
        }

        private static AutoAssortmentAuthorMatch EvaluateMatch(string authorName, string fileName)
        {
            if (string.IsNullOrWhiteSpace(authorName) || string.IsNullOrWhiteSpace(fileName))
                return null;

            string normalizedAuthor = NormalizeForMatch(authorName);
            string normalizedFile = NormalizeForMatch(fileName);
            if (string.IsNullOrWhiteSpace(normalizedAuthor) || string.IsNullOrWhiteSpace(normalizedFile))
                return null;

            if (normalizedFile.Contains(normalizedAuthor, StringComparison.OrdinalIgnoreCase))
            {
                return new AutoAssortmentAuthorMatch(
                    1000 + normalizedAuthor.Length,
                    $"Full author name match in file name: {fileName}",
                    fileName,
                    authorName,
                    normalizedAuthor.Length);
            }

            if (normalizedAuthor.Length < MinimumAuthorFragmentLength)
                return null;

            for (int fragmentLength = normalizedAuthor.Length; fragmentLength >= MinimumAuthorFragmentLength; fragmentLength--)
            {
                for (int start = 0; start <= normalizedAuthor.Length - fragmentLength; start++)
                {
                    string fragment = normalizedAuthor.Substring(start, fragmentLength);
                    if (!normalizedFile.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return new AutoAssortmentAuthorMatch(
                        500 + fragmentLength,
                        $"Author fragment match ({fragmentLength} chars) in file name: {fileName}",
                        fileName,
                        fragment,
                        fragmentLength);
                }
            }

            return null;
        }

        private static string NormalizeForMatch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        private static int GetPathDepth(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return int.MaxValue;

            return path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string BuildRelativePath(string rootDirectory, string childPath)
        {
            try
            {
                return Path.GetRelativePath(rootDirectory, childPath);
            }
            catch
            {
                return childPath;
            }
        }

        private sealed class AutoAssortmentAuthorMatch
        {
            public AutoAssortmentAuthorMatch(int score, string reason, string matchedFileName, string matchedFragment, int fragmentLength)
            {
                Score = score;
                Reason = reason;
                MatchedFileName = matchedFileName;
                MatchedFragment = matchedFragment;
                FragmentLength = fragmentLength;
            }

            public int Score { get; }
            public string Reason { get; }
            public string MatchedFileName { get; }
            public string MatchedFragment { get; }
            public int FragmentLength { get; }
        }
    }

    public sealed class AutoAssortmentPlan
    {
        public AutoAssortmentPlan(
            string rootDirectory,
            string sourceDirectory,
            IReadOnlyList<AutoAssortmentAuthorTarget> authorTargets,
            IReadOnlyList<AutoAssortmentPlanItem> items)
        {
            RootDirectory = rootDirectory;
            SourceDirectory = sourceDirectory;
            AuthorTargets = authorTargets ?? Array.Empty<AutoAssortmentAuthorTarget>();
            Items = items ?? Array.Empty<AutoAssortmentPlanItem>();
        }

        public string RootDirectory { get; }
        public string SourceDirectory { get; }
        public IReadOnlyList<AutoAssortmentAuthorTarget> AuthorTargets { get; }
        public IReadOnlyList<AutoAssortmentPlanItem> Items { get; }
    }

    public sealed class AutoAssortmentPlanItem
    {
        public AutoAssortmentPlanItem(
            string sourcePath,
            string sourceFolderName,
            string relativeSourcePath,
            int scannedFileCount,
            IReadOnlyList<AutoAssortmentCandidateTarget> candidateTargets,
            string matchSummary)
        {
            SourcePath = sourcePath;
            SourceFolderName = sourceFolderName;
            RelativeSourcePath = relativeSourcePath;
            ScannedFileCount = scannedFileCount;
            CandidateTargets = candidateTargets ?? Array.Empty<AutoAssortmentCandidateTarget>();
            MatchSummary = matchSummary ?? string.Empty;
        }

        public string SourcePath { get; }
        public string SourceFolderName { get; }
        public string RelativeSourcePath { get; }
        public int ScannedFileCount { get; }
        public IReadOnlyList<AutoAssortmentCandidateTarget> CandidateTargets { get; }
        public string MatchSummary { get; }
    }

    public sealed class AutoAssortmentCandidateTarget
    {
        public AutoAssortmentCandidateTarget(
            AutoAssortmentAuthorTarget target,
            int score,
            string reason,
            string matchedFileName,
            string matchedFragment)
        {
            Target = target;
            Score = score;
            Reason = reason;
            MatchedFileName = matchedFileName;
            MatchedFragment = matchedFragment;
        }

        public AutoAssortmentAuthorTarget Target { get; }
        public int Score { get; }
        public string Reason { get; }
        public string MatchedFileName { get; }
        public string MatchedFragment { get; }

        public string BuildSummary()
        {
            return $"{Target.AuthorName} -> {Target.RelativePath} ({Reason})";
        }
    }

    public sealed class AutoAssortmentAuthorTarget
    {
        public AutoAssortmentAuthorTarget(string authorName, string directoryPath, string relativePath)
        {
            AuthorName = authorName;
            DirectoryPath = directoryPath;
            RelativePath = relativePath;
        }

        public string AuthorName { get; }
        public string DirectoryPath { get; }
        public string RelativePath { get; }
    }

    public sealed class AutoAssortmentExecutionItem
    {
        public AutoAssortmentExecutionItem(string sourcePath, string targetDirectoryPath)
        {
            SourcePath = sourcePath;
            TargetDirectoryPath = targetDirectoryPath;
        }

        public string SourcePath { get; }
        public string TargetDirectoryPath { get; }
    }
}
