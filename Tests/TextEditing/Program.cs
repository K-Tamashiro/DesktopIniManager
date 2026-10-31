using System;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "viewer") return DiffEditingTests.RunViewer();
            if (args.Length > 0 && args[0] == "regression") return DiffRegressionTests.Run();
            return DiffEditingTests.Run();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
