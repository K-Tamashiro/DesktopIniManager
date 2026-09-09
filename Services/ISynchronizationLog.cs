namespace DesktopIniManager.Services
{
    internal interface ISynchronizationLog
    {
        void AppendLine(string line);
        void Complete(int succeeded, int failed, int locked);
        void Activate();
    }
}
