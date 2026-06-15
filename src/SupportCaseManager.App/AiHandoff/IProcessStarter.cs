using System.Diagnostics;

namespace SupportCaseManager.App.AiHandoff;

public interface IProcessStarter
{
    void Start(ProcessStartInfo startInfo);
}
