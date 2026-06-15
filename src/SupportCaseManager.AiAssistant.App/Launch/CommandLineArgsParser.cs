namespace SupportCaseManager.AiAssistant.App.Launch;

public sealed class CommandLineArgsParser : ICommandLineArgsParser
{
    public CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        args ??= [];

        string? contextFilePath = null;
        var warnings = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (string.Equals(arg, "--context-file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-c", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count || IsOption(args[index + 1]))
                {
                    warnings.Add($"{arg} requires a file path.");
                    continue;
                }

                contextFilePath = args[index + 1];
                index += 1;
                continue;
            }

            warnings.Add($"Ignored unsupported argument at index {index}.");
        }

        return new CommandLineOptions
        {
            ContextFilePath = contextFilePath,
            Warnings = warnings,
        };
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("-", StringComparison.Ordinal);
    }
}
