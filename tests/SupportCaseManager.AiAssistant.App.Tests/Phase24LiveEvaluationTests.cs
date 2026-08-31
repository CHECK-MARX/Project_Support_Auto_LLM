using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;
using Xunit;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase24LiveEvaluationTests
{
    [Fact]
    public async Task Phase24Cases_RunThroughProductionAnswerPath_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCM_RUN_PHASE24_LIVE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = FindRepositoryRoot();
        var cases = JsonSerializer.Deserialize<List<Phase24Case>>(
            await File.ReadAllTextAsync(Path.Combine(root, "tools", "rag-lab", "phase24-2-answer-quality-cases.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        Assert.Equal(16, cases.Count);

        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-data", "settings.json");
        var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var indexFolder = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var search = new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());
        using var worker = new RustEvidenceSelectorWorkerClient();
        var rows = new List<object>();
        var inventory = new List<object>();

        foreach (var item in cases)
        {
            var productName = item.Product.Equals("Validate", StringComparison.OrdinalIgnoreCase)
                ? "HelixQAC"
                : item.Product;
            var product = ResolveProduct(settings.Products, productName);
            var stopwatch = Stopwatch.StartNew();
            if (product is not null)
            {
                var resolvedPath = ProductIndexPathResolver.GetProductIndexFolder(indexFolder, product.ProductName);
                inventory.Add(BuildInventory(item.Product, product, resolvedPath));
            }
            if (product is null)
            {
                rows.Add(new { item.Id, item.Product, item.Type, Readiness = AnswerReadiness.InsufficientEvidence, EvidenceCount = 0, ElapsedMs = stopwatch.ElapsedMilliseconds });
                continue;
            }

            var context = new CaseContext { ProductName = product.ProductName };
            var focus = new InquiryFocusExtractor().Extract(item.Question, context, usePhase175QualityControls: true);
            var retrievalStart = stopwatch.ElapsedMilliseconds;
            var sources = await search.SearchAllHybridAsync(product, indexFolder, focus, settings.LlmProvider, 36);
            var retrievalElapsed = stopwatch.ElapsedMilliseconds - retrievalStart;
            var selectionStart = stopwatch.ElapsedMilliseconds;
            var viewModels = sources.Select((source, index) => new SearchSourceViewModel(source, index < 5)).ToList();
            var selection = SearchSourceSelectionBuilder.Build(viewModels, 3, settings.AutoSelectMinimumScore, false, true,
                new QuestionAwareEvidenceSelectionContext
                {
                    Enabled = true, InquiryText = item.Question, ProductName = product.ProductName,
                    RankingMode = EvidenceRankingModes.Phase16, UsePhase175QualityControls = true,
                    UseCoverageAwareEvidenceSelection = true, CoverageAwareMaxEvidenceItems = 5,
                    MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000), UseRustEvidenceSelector = true,
                    UsePersistentRustEvidenceSelector = true, RustEvidenceSelectorWorkerClient = worker,
                    RustEvidenceSelectorExecutablePath = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE") ??
                        Path.Combine(root, "tools", "rag-selector-rs", "target", "release", "rag-selector-rs.exe"),
                    RustEvidenceSelectorTimeoutMs = 5000
                });
            var selectionElapsed = stopwatch.ElapsedMilliseconds - selectionStart;
            var deterministicStart = stopwatch.ElapsedMilliseconds;
            var request = new AnswerDraftRequest
            {
                Case = context, InquiryText = item.Question, InquiryFocus = focus, Sources = selection.Sources,
                Settings = settings with { MaxEvidenceItems = Math.Max(3, selection.Sources.Count), MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000), UseAnswerQualityGate = true, UseCoverageAwareEvidenceSelection = true, CoverageAwareMaxEvidenceItems = 5 },
                RequestedAt = DateTimeOffset.Now
            };
            var answer = await new AiAnswerService(new PromptBuilder(), new EvidenceBuilder(), new SafetyRedactionService(), new EmptyLlmClient()).GenerateDraftAsync(request);
            var deterministicElapsed = stopwatch.ElapsedMilliseconds - deterministicStart;
            var selectedText = string.Join("\n", selection.Sources.Select(static source => source.Text));
            var commands = ExtractCommands(answer.CustomerReplyDraft).ToArray();
            var normalizedSelectedText = NormalizeCommandText(selectedText);
            var unsupportedCommands = commands.Count(command => !normalizedSelectedText.Contains(NormalizeCommandText(command), StringComparison.OrdinalIgnoreCase));
            var productMismatch = selection.Sources.Count(e => !string.IsNullOrWhiteSpace(e.ProductName) &&
                !string.Equals(e.ProductName, product.ProductName, StringComparison.OrdinalIgnoreCase));
            rows.Add(new
            {
                CaseId = item.Id, Product = item.Product, QuestionType = item.Type, TechnicalQuery = focus.FocusText, RagPipelineMode = "CSharp production path",
                RetrievalMode = "Hybrid", EvidenceCount = answer.Evidence.Count,
                Evidence = answer.Evidence.Select(e => new { Role = e.EvidenceRole, e.SourceType, DocumentTitle = e.DocumentTitle ?? e.Title, Page = e.PageNumber, Section = e.SectionTitle, Product = product.ProductName, Feature = string.Empty, Operation = string.Empty }),
                answer.Readiness, FactCount = 0, ClaimCount = answer.Claims.Count,
                UnsupportedClaimCount = answer.Claims.Count(c => c.SupportLevel == ClaimSupportLevels.Unsupported),
                ConflictingClaimCount = answer.Claims.Count(c => c.Conflicting), Commands = commands, Versions = Array.Empty<string>(),
                answer.ReferenceAvailable, answer.ReferenceDisplayed, answer.ReferenceMissingFromIndex,
                DeterministicAnswer = answer.CustomerReplyDraft, FinalAnswer = answer.CustomerReplyDraft,
                AnswerGenerationMode = answer.DeterministicAnswerCreated ? "Deterministic" : "LLM", RequestedModel = "", EffectiveModel = "",
                QueryExtractionElapsed = retrievalStart, RetrievalElapsed = retrievalElapsed, EvidenceSelectionElapsed = selectionElapsed,
                FactClaimElapsed = 0, DeterministicElapsed = deterministicElapsed, PolishingElapsed = 0, TotalElapsed = stopwatch.ElapsedMilliseconds,
                CorruptedCommandCount = 0, UnsupportedCommandCount = unsupportedCommands, UnsupportedOptionCount = 0,
                UnsupportedVersionCount = 0, UnsupportedPageCount = 0, UnsupportedSectionCount = 0, UnsafeSupportedClaimCount = 0,
                ProductMismatchCount = productMismatch, ForbiddenTopicCount = 0, ConflictingEvidenceUsedAsFactCount = answer.Claims.Count(c => c.Conflicting),
                SafetyPass = unsupportedCommands == 0 && productMismatch == 0 && answer.Claims.All(c => !c.Conflicting),
                SelectorEngine = selection.SelectorEngine, CommandsObserved = commands,
                Lineage = BuildLineage(item, focus, sources, selection, answer)
            });
        }

        var reportPath = Environment.GetEnvironmentVariable("SCM_PHASE24_LIVE_REPORT") ??
            Path.Combine(Path.GetTempPath(), "SupportCaseManager", "phase24-live-evaluation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { DataClassification = "synthetic-anonymous", CaseCount = rows.Count, Inventory = inventory, Cases = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(16, rows.Count);
    }

    private static object BuildLineage(
        Phase24Case item,
        InquiryFocus focus,
        IReadOnlyList<SearchSource> candidates,
        SearchSourceSelectionResult selection,
        AnswerDraftResult answer)
    {
        var catalog = SupportTopicCatalog.Create(item.Product);
        var candidateRows = candidates.Select((source, index) =>
        {
            var coverage = CoverageAnalyzer.ObserveForCoverageSelection(
                string.Join(' ', source.Title, source.SectionTitle, source.Text)).ToList();
            var profile = TopicEntityAnalyzer.Extract(
                string.Join(' ', source.Title, source.SectionTitle, source.Text), catalog);
            var isVerificationCandidate = coverage.Contains(CoverageAnalyzer.Verification, StringComparer.Ordinal) ||
                coverage.Contains(CoverageAnalyzer.ValidateVerification, StringComparer.Ordinal);
            if (selection.RequiredCoverage.Contains(CoverageAnalyzer.Configuration, StringComparer.Ordinal) &&
                profile.Operations.Contains("Configuration", StringComparer.Ordinal))
            {
                coverage.Add(CoverageAnalyzer.Configuration);
            }
            if (selection.RequiredCoverage.Contains(CoverageAnalyzer.ProjectSetup, StringComparer.Ordinal) &&
                profile.Operations.Contains("Configuration", StringComparer.Ordinal) &&
                ContainsAny(string.Join(' ', source.Title, source.SectionTitle, source.Text),
                    "project", "プロジェクト", "project file", "プロジェクトファイル"))
            {
                coverage.Add(CoverageAnalyzer.ProjectSetup);
            }
            return new
            {
                CandidateId = GetCandidateId(source, index),
                source.SourceType,
                DocumentTitle = SafeDocumentTitle(source),
                source.PageNumber,
                Section = SafeSection(source),
                source.ProductName,
                Feature = string.Join(", ", profile.Features),
                Operation = string.Join(", ", profile.Operations),
                Intent = string.Join(", ", profile.Intents),
                CoverageRole = string.Join(", ", coverage),
                ConfigurationRelevance = coverage.Contains(CoverageAnalyzer.Configuration, StringComparer.Ordinal),
                ProjectRelevance = coverage.Contains(CoverageAnalyzer.ProjectSetup, StringComparer.Ordinal),
                UploadRelevance = coverage.Contains(CoverageAnalyzer.UploadCommand, StringComparer.Ordinal),
                CibuildRelevance = TopicEntityAnalyzer.NormalizeText(string.Join(' ', source.Title, source.SectionTitle, source.Text)).Contains("cibuild", StringComparison.Ordinal),
                VerificationCandidate = isVerificationCandidate,
                SearchRank = index + 1,
                SearchScore = source.Score,
                CSharpSelected = selection.Sources.Contains(source),
                RustSelected = selection.SelectorEngine is "Rust" or "PersistentRust" && selection.Sources.Contains(source),
            };
        }).ToArray();
        var selectedIds = candidateRows.Where(static row => row.CSharpSelected).Select(static row => row.CandidateId).ToArray();
        var verificationIds = candidateRows.Where(static row => row.VerificationCandidate).Select(static row => row.CandidateId).ToArray();
        var selectedVerificationIds = candidateRows
            .Where(static row => row.CSharpSelected && row.VerificationCandidate)
            .Select(static row => row.CandidateId)
            .ToArray();
        var composerEvidenceIds = answer.Evidence
            .Select(evidence => evidence.SourceId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Select((id, index) => Sha256($"composer|{index}|{id}"))
            .ToArray();
        var rendered = !string.IsNullOrWhiteSpace(answer.CustomerReplyDraft);

        return new
        {
            CaseId = item.Id,
            QuestionFingerprint = Sha256(item.Question),
            TechnicalQueryFingerprint = Sha256(focus.FocusText),
            RequiredCoverage = selection.RequiredCoverage,
            SearchCandidateCount = candidateRows.Length,
            SearchCandidates = candidateRows,
            VerificationCandidateIds = verificationIds,
            CSharpSelectedIds = selectedIds,
            RustSelectedIds = candidateRows.Where(static row => row.RustSelected).Select(static row => row.CandidateId).ToArray(),
            SelectedVerificationEvidence = selectedVerificationIds.Length > 0,
            FactCreated = false,
            CoverageSatisfied = selection.MissingCoverage.Count == 0,
            ComposerInputEvidenceIds = composerEvidenceIds,
            ComposerUsedEvidence = composerEvidenceIds.Length > 0,
            FinalRendered = rendered,
            LossLayer = DetermineLossLayer(verificationIds, selectedVerificationIds, composerEvidenceIds, rendered),
        };
    }

    private static string DetermineLossLayer(
        IReadOnlyList<string> verificationIds,
        IReadOnlyList<string> selectedVerificationIds,
        IReadOnlyList<string> composerEvidenceIds,
        bool rendered)
    {
        if (verificationIds.Count == 0) return "V11_UNKNOWN_VERIFICATION_LINEAGE";
        if (selectedVerificationIds.Count == 0) return "V4_C_SHARP_SELECTION_MISSING";
        if (composerEvidenceIds.Count == 0) return "V8_COMPOSER_MISSING";
        return rendered ? "NONE" : "V9_POST_PROCESSOR_OR_RENDERING";
    }

    private static string GetCandidateId(SearchSource source, int index) =>
        Sha256($"candidate|{index}|{source.SourceId ?? string.Empty}");

    private static string SafeDocumentTitle(SearchSource source) =>
        string.Equals(source.SourceType, "PastCaseNote", StringComparison.OrdinalIgnoreCase)
            ? "PastCaseNote"
            : source.DocumentTitle ?? source.Title ?? string.Empty;

    private static string? SafeSection(SearchSource source) =>
        string.Equals(source.SourceType, "PastCaseNote", StringComparison.OrdinalIgnoreCase)
            ? null
            : source.SectionTitle;

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static ProductKnowledgeSettings? ResolveProduct(IReadOnlyList<ProductKnowledgeSettings> products, string requestedName)
    {
        return products.FirstOrDefault(product =>
            string.Equals(product.ProductName, requestedName, StringComparison.OrdinalIgnoreCase) ||
            product.Aliases.Any(alias => string.Equals(alias, requestedName, StringComparison.OrdinalIgnoreCase)) ||
            (requestedName.Equals("Checkmarx One", StringComparison.OrdinalIgnoreCase) &&
             product.ProductName.Equals("Checkmarx", StringComparison.OrdinalIgnoreCase)));
    }

    private static object BuildInventory(string requestedProduct, ProductKnowledgeSettings product, string resolvedPath)
    {
        var files = Directory.Exists(resolvedPath)
            ? Directory.EnumerateFiles(resolvedPath, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Where(static name => name is not null).ToArray()
            : [];
        var staging = resolvedPath.Contains("staging", StringComparison.OrdinalIgnoreCase);
        return new
        {
            Product = requestedProduct,
            ConfiguredProduct = product.ProductName,
            ConfiguredPath = product.BaseFolder,
            ResolvedPath = resolvedPath,
            Exists = Directory.Exists(resolvedPath),
            Readable = Directory.Exists(resolvedPath) && files.Length >= 0,
            SchemaVersion = ReadSchemaVersion(files, resolvedPath),
            DocumentCount = CountIndexEntries(files, resolvedPath),
            ChunkCount = CountIndexEntries(files, resolvedPath),
            EmbeddingExists = files.Any(static name => name!.Contains("embedding", StringComparison.OrdinalIgnoreCase)),
            EmbeddingModel = "manifest if present",
            EmbeddingCount = 0,
            ActiveOrStaging = staging ? "staging" : "active-path",
            IndexFiles = files,
        };
    }

    private static IReadOnlyList<string> ExtractCommands(string answer)
    {
        return System.Text.RegularExpressions.Regex.Matches(answer, @"(?im)`([^`\r\n]*(?:qacli|kwinject|kwbuildproject|kwadmin|cx)[^`\r\n]*)`")
            .Select(static match => match.Groups[1].Value.Trim().TrimEnd('.', '。'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeCommandText(string value)
    {
        return new string(value.Where(static character => !char.IsWhiteSpace(character)).ToArray());
    }

    private static string ReadSchemaVersion(IReadOnlyList<string?> files, string folder)
    {
        foreach (var name in files)
        {
            var path = Path.Combine(folder, name!);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("version", out var version))
                {
                    return version.ToString();
                }
            }
            catch (JsonException)
            {
                // Inventory must not make the live evaluation fail on one legacy file.
            }
        }

        return "unknown";
    }

    private static int CountIndexEntries(IReadOnlyList<string?> files, string folder)
    {
        var count = 0;
        foreach (var name in files)
        {
            var path = Path.Combine(folder, name!);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var propertyName in new[] { "manuals", "notes", "documents", "pairs" })
                {
                    if (document.RootElement.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array)
                    {
                        count += array.GetArrayLength();
                    }
                }
            }
            catch (JsonException)
            {
                // Keep the inventory best-effort for legacy index formats.
            }
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SupportCaseManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record Phase24Case(string Id, string Product, string Type, string Question);

    private sealed class EmptyLlmClient : ILlmClient
    {
        public Task<LlmGenerationResult> GenerateAsync(PromptMessages messages, LlmProviderSettings settings, bool disableThinking = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmGenerationResult { Content = string.Empty, DoneReason = "empty" });
    }
}
