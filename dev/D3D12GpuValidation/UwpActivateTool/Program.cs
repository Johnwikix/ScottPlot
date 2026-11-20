using System.Runtime.InteropServices;

// Activates a packaged UWP app with launch arguments and prints the process id.
// Usage: UwpActivateTool <AppUserModelId> <arguments...>
if (args.Length < 1) { Console.Error.WriteLine("usage: <AppUserModelId> <arguments...>"); return 2; }
string aumid = args[0];
string arguments = args.Length > 1 ? string.Join(' ', args.Skip(1)) : string.Empty;
var manager = (IApplicationActivationManager)new ApplicationActivationManager();
const uint noErrorUi = 0x2;
Marshal.ThrowExceptionForHR(manager.ActivateApplication(aumid, arguments, noErrorUi, out uint pid));
Console.WriteLine(pid);
return 0;

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), ClassInterface(ClassInterfaceType.None)]
internal class ApplicationActivationManager { }
