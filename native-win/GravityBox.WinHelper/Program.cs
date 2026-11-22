using System.Text;

namespace GravityBox.WinHelper;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(GravityBox.Win32.Win32BridgeInfo.BridgeVersion);
            return 0;
        }
        return 0;
    }
}
