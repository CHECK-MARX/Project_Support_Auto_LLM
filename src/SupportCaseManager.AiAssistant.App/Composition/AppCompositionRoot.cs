using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Cases;
using SupportCaseManager.Ai.Core.Diagnostics;
using SupportCaseManager.Ai.Core.Drafts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Launch;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Core.Settings;
using SupportCaseManager.AiAssistant.App.Appearance;
using SupportCaseManager.AiAssistant.App.Llm;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Composition;

public static class AppCompositionRoot
{
    public static MainWindow CreateMainWindow()
    {
        var settingsStore = new AiSettingsStore();
        var noteSnapshotReader = new NoteSnapshotReader();
        var caseContextBuilder = new CaseContextBuilder(noteSnapshotReader);
        var caseIndexBuilder = new AiCaseIndexBuilder(caseContextBuilder);
        var manualIndexBuilder = new AiManualIndexBuilder();
        var officialDocumentIndexBuilder = new AiOfficialDocumentIndexBuilder();
        var keywordSearcher = new AiCaseKeywordSearcher();
        var manualKeywordSearcher = new AiManualKeywordSearcher();
        var officialDocumentKeywordSearcher = new AiOfficialDocumentKeywordSearcher();
        var productScopedIndexService = new ProductScopedIndexService(caseIndexBuilder, manualIndexBuilder, officialDocumentIndexBuilder);
        var productScopedSearchService = new ProductScopedSearchService(keywordSearcher, manualKeywordSearcher, officialDocumentKeywordSearcher);
        var inquiryFocusExtractor = new InquiryFocusExtractor();
        var supportToolSettingsReader = new SupportToolSettingsReader();
        var productSettingsSynchronizer = new ProductKnowledgeSettingsSynchronizer();
        var launchContextReader = new AiAssistantLaunchContextReader();
        var promptBuilder = new PromptBuilder();
        var evidenceBuilder = new EvidenceBuilder();
        var safetyRedactionService = new SafetyRedactionService();
        var llmClientFactory = new LlmClientFactory();
        IOllamaConnectionChecker ollamaConnectionChecker = new OllamaConnectionChecker();
        var draftStore = new AiDraftStore();
        IAppAppearanceService appearanceService = new AppAppearanceService();

        IAiAnswerService CreateAnswerService(SupportCaseManager.Ai.Contracts.LlmProviderSettings settings)
        {
            return new AiAnswerService(
                promptBuilder,
                evidenceBuilder,
                safetyRedactionService,
                llmClientFactory.Create(settings));
        }

        IAiDiagnosticLogger CreateLogger(string aiDataFolder)
        {
            return new AiDiagnosticLogger(aiDataFolder, safetyRedactionService);
        }

        var viewModel = new MainViewModel(
            settingsStore,
            caseContextBuilder,
            noteSnapshotReader,
            caseIndexBuilder,
            manualIndexBuilder,
            productScopedIndexService,
            keywordSearcher,
            manualKeywordSearcher,
            productScopedSearchService,
            inquiryFocusExtractor,
            ollamaConnectionChecker,
            supportToolSettingsReader,
            productSettingsSynchronizer,
            launchContextReader,
            CreateAnswerService,
            draftStore,
            CreateLogger,
            appearanceService);

        return new MainWindow(viewModel);
    }
}
