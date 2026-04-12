using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ImageFolderManager.Services
{
    public sealed class SmartFolderClassificationService
    {
        public const string UnclassifiedDirectoryName = "(Unclassified)";

        private static readonly Regex AuthorDirectoryPattern =
            new Regex(@"^\[(?<author>.+)\]$", RegexOptions.Compiled);

        private static readonly Regex BracketAuthorPattern =
            new Regex(@"\[(?<author>[^\[\]]+)\]", RegexOptions.Compiled);

        private static readonly char[] NameSeparators =
            { ' ', '_', '-', '.', ',', '(', ')', '[', ']', '{', '}', '~' };

        public SmartFolderClassificationPlan BuildPlan(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("Root directory cannot be empty.", nameof(rootDirectory));
            if (!Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException($"Root directory not found: {rootDirectory}");

            string normalizedRoot = Path.GetFullPath(rootDirectory);
            var topLevelDirectories = Directory.GetDirectories(normalizedRoot, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var knownAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int authorDirectoryCount = 0;

            foreach (var directoryPath in topLevelDirectories)
            {
                if (TryParseAuthorDirectoryName(Path.GetFileName(directoryPath), out string authorName))
                {
                    if (!string.IsNullOrWhiteSpace(authorName))
                    {
                        knownAuthors.Add(authorName);
                    }
                    authorDirectoryCount++;
                }
            }

            var moves = new List<SmartFolderClassificationMove>();
            foreach (var directoryPath in topLevelDirectories)
            {
                string directoryName = Path.GetFileName(directoryPath);

                if (TryParseAuthorDirectoryName(directoryName, out _))
                    continue;

                if (directoryName.Equals(UnclassifiedDirectoryName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string authorName = string.Empty;
                string reason = string.Empty;

                if (TryExtractBracketAuthor(directoryName, out string bracketAuthor))
                {
                    authorName = NormalizeAuthorName(bracketAuthor);
                    reason = "Bracket token in folder name";
                }
                else if (TryMatchKnownAuthor(directoryName, knownAuthors, out string matchedAuthor))
                {
                    authorName = NormalizeAuthorName(matchedAuthor);
                    reason = "Matched existing author directory";
                }
                else
                {
                    reason = "No author detected";
                }

                if (!string.IsNullOrWhiteSpace(authorName))
                {
                    knownAuthors.Add(authorName);
                }

                string targetParentName = string.IsNullOrWhiteSpace(authorName)
                    ? UnclassifiedDirectoryName
                    : CreateAuthorDirectoryName(authorName);

                string targetFolderName = BuildTargetFolderName(directoryName, authorName);

                moves.Add(new SmartFolderClassificationMove(
                    directoryPath,
                    directoryName,
                    authorName,
                    targetParentName,
                    targetFolderName,
                    reason));
            }

            return new SmartFolderClassificationPlan(
                normalizedRoot,
                topLevelDirectories.Count,
                authorDirectoryCount,
                moves);
        }

        public static bool TryParseAuthorDirectoryName(string folderName, out string authorName)
        {
            authorName = string.Empty;

            if (string.IsNullOrWhiteSpace(folderName))
                return false;

            var match = AuthorDirectoryPattern.Match(folderName.Trim());
            if (!match.Success)
                return false;

            authorName = NormalizeAuthorName(match.Groups["author"].Value);
            return !string.IsNullOrWhiteSpace(authorName);
        }

        public static string CreateAuthorDirectoryName(string authorName)
        {
            string normalizedAuthor = NormalizeAuthorName(authorName);
            return string.IsNullOrWhiteSpace(normalizedAuthor)
                ? UnclassifiedDirectoryName
                : $"[{normalizedAuthor}]";
        }

        private static string BuildTargetFolderName(string sourceFolderName, string authorName)
        {
            string cleanSourceName = SanitizePathSegment((sourceFolderName ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(cleanSourceName))
                cleanSourceName = "Untitled";

            if (string.IsNullOrWhiteSpace(authorName))
            {
                return cleanSourceName;
            }

            string bracketAuthor = $"[{authorName}]";
            string remaining = cleanSourceName;

            if (remaining.StartsWith(bracketAuthor, StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining.Substring(bracketAuthor.Length).TrimStart(NameSeparators);
            }
            else
            {
                remaining = StripLeadingAuthorToken(remaining, authorName);
            }

            if (string.IsNullOrWhiteSpace(remaining))
                remaining = "Untitled";

            return SanitizePathSegment($"{bracketAuthor}{remaining}");
        }

        private static bool TryExtractBracketAuthor(string folderName, out string authorName)
        {
            authorName = string.Empty;

            if (string.IsNullOrWhiteSpace(folderName))
                return false;

            var match = BracketAuthorPattern.Match(folderName);
            if (!match.Success)
                return false;

            authorName = NormalizeAuthorName(match.Groups["author"].Value);
            return !string.IsNullOrWhiteSpace(authorName);
        }

        private static bool TryMatchKnownAuthor(
            string folderName,
            IEnumerable<string> knownAuthors,
            out string matchedAuthor)
        {
            matchedAuthor = string.Empty;

            if (string.IsNullOrWhiteSpace(folderName) || knownAuthors == null)
                return false;

            string name = folderName.Trim();
            string normalizedName = NormalizeForMatch(name);

            int bestScore = 0;
            string bestAuthor = string.Empty;

            foreach (var author in knownAuthors.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                string candidate = author.Trim();
                string normalizedCandidate = NormalizeForMatch(candidate);
                if (string.IsNullOrWhiteSpace(normalizedCandidate))
                    continue;

                int score = 0;
                if (name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    score = 100;
                }
                else if (name.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    score = 95;
                }
                else if (ContainsAuthorAsToken(name, candidate))
                {
                    score = 85;
                }
                else if (normalizedName.StartsWith(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    score = 80;
                }
                else if (normalizedName.Contains(normalizedCandidate))
                {
                    score = 70;
                }

                if (score > bestScore ||
                    (score == bestScore && candidate.Length > bestAuthor.Length))
                {
                    bestScore = score;
                    bestAuthor = candidate;
                }
            }

            if (bestScore == 0 || string.IsNullOrWhiteSpace(bestAuthor))
                return false;

            matchedAuthor = bestAuthor;
            return true;
        }

        private static bool ContainsAuthorAsToken(string folderName, string authorName)
        {
            var tokens = folderName
                .Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (tokens.Count == 0)
                return false;

            return tokens.Any(token => token.Equals(authorName, StringComparison.OrdinalIgnoreCase));
        }

        private static string StripLeadingAuthorToken(string sourceName, string authorName)
        {
            if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(authorName))
                return sourceName;

            string trimmed = sourceName.Trim();
            string normalizedAuthor = NormalizeForMatch(authorName);
            string normalizedName = NormalizeForMatch(trimmed);

            if (!normalizedName.StartsWith(normalizedAuthor, StringComparison.OrdinalIgnoreCase))
                return trimmed;

            int consumedChars = 0;
            int normalizedConsumed = 0;
            foreach (char ch in trimmed)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    normalizedConsumed++;
                }

                consumedChars++;
                if (normalizedConsumed >= normalizedAuthor.Length)
                    break;
            }

            string remainder = trimmed.Substring(Math.Min(consumedChars, trimmed.Length));
            remainder = remainder.TrimStart(NameSeparators);
            return string.IsNullOrWhiteSpace(remainder) ? "Untitled" : remainder;
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

        private static string NormalizeAuthorName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = value.Trim();
            normalized = normalized.Trim('[', ']', '(', ')');
            return SanitizePathSegment(normalized);
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(value
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray())
                .Trim();

            if (string.IsNullOrWhiteSpace(sanitized))
                return string.Empty;

            return sanitized.TrimEnd('.');
        }
    }

    public sealed class SmartFolderClassificationPlan
    {
        public SmartFolderClassificationPlan(
            string rootDirectory,
            int scannedTopLevelDirectoryCount,
            int existingAuthorDirectoryCount,
            IReadOnlyList<SmartFolderClassificationMove> moves)
        {
            RootDirectory = rootDirectory;
            ScannedTopLevelDirectoryCount = scannedTopLevelDirectoryCount;
            ExistingAuthorDirectoryCount = existingAuthorDirectoryCount;
            Moves = moves ?? Array.Empty<SmartFolderClassificationMove>();
        }

        public string RootDirectory { get; }
        public int ScannedTopLevelDirectoryCount { get; }
        public int ExistingAuthorDirectoryCount { get; }
        public IReadOnlyList<SmartFolderClassificationMove> Moves { get; }
        public int RecognizedAuthorCount => Moves.Count(m => !m.IsUnclassified);
        public int UnclassifiedCount => Moves.Count(m => m.IsUnclassified);
    }

    public sealed class SmartFolderClassificationMove
    {
        public SmartFolderClassificationMove(
            string sourcePath,
            string sourceFolderName,
            string authorName,
            string targetParentDirectoryName,
            string targetFolderName,
            string reason)
        {
            SourcePath = sourcePath;
            SourceFolderName = sourceFolderName;
            AuthorName = authorName;
            TargetParentDirectoryName = targetParentDirectoryName;
            TargetFolderName = targetFolderName;
            Reason = reason;
        }

        public string SourcePath { get; }
        public string SourceFolderName { get; }
        public string AuthorName { get; }
        public string TargetParentDirectoryName { get; }
        public string TargetFolderName { get; }
        public string Reason { get; }

        public bool IsUnclassified => string.IsNullOrWhiteSpace(AuthorName);
    }
}
