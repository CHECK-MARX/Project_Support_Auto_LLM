namespace SupportCaseManager.AiAssistant.App.Launch;

public interface ICommandLineArgsParser
{
    CommandLineOptions Parse(IReadOnlyList<string> args);
}
