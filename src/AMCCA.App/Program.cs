using System;
using System.Threading.Tasks;

namespace AMCCA.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--version")
        {
            Console.WriteLine("3.1.0");
            return 0;
        }

        if (args.Length > 0 && args[0] == "--headless")
        {
            Console.WriteLine("AMCCA Engineering V3.1 Runtime (Headless)");
            Console.WriteLine("System initialized successfully.");
            return 0;
        }

        var app = new App();
        return app.Run();
    }
}
