using System.Text;
using BSpeak.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
try
{
    return await Cli.RunAsync(args);
}
catch (UsageException u)
{
    Console.Error.WriteLine(u.Message);
    Console.Error.WriteLine("Run 'bspeak help' for the list of commands.");
    return ExitCodes.Usage;
}
catch (HostUnavailableException h)
{
    Console.Out.WriteLine(Json.Error("host-unavailable", h.Message));
    return ExitCodes.HostUnavailable;
}
