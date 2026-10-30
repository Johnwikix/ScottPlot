using System.Runtime.InteropServices;

// Activates a packaged UWP app with launch arguments and prints the process id.
// Usage: UwpActivateTool <AppUserModelId> <arguments...>
if (args.Length < 1) { Console.Error.WriteLine("usage: <AppUserModelId> <arguments...>"); return 2; }
string aumid = args[0];
string arguments = args.Length > 1 ? string.Join(' ', args.Skip(1)) : string.Empty;
uint pid = 0;
var manager = (IApplicationActivationManager)new ApplicationActivationManager();
manager.ActivateApplication(aumid, arguments, "NONE", out pid);
Console.WriteLine(pid);
return 0;

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    IntPtr ActivateApplication([In] string appUserModelId, [In] string arguments, [In] string options, [Out] out uint processId);
    IntPtr ActivateForFile([In] string appUserModelId, [In] IntPtr itemArray, [In] string verbEvent, [Out] out uint processId);
    IntPtr ActivateForProtocol([In] string appUserModelId, [In] IntPtr itemArray, [Out] out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), ClassInterface(ClassInterfaceType.None)]
internal class ApplicationActivationManager : IApplicationActivationManager
{
    public extern IntPtr ActivateApplication(string appUserModelId, string arguments, string options, out uint processId);
    public extern IntPtr ActivateForFile(string appUserModelId, IntPtr itemArray, string verbEvent, out uint processId);
    public extern IntPtr ActivateForProtocol(string appUserModelId, IntPtr itemArray, out uint processId);
}
