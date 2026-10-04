using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SnowFlyer2
{
    partial class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address,
            UIntPtr size, uint allocationType, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr process, IntPtr address,
            UIntPtr size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes,
            UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string module);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedure);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file,
            uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        private const uint DontResolveDllReferences = 0x00000001;

        private static bool LoadSpectatorUi(Process game)
        {
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "SpectatorUI.dll"));
            if (IntPtr.Size != 8 || !File.Exists(path))
            {
                Console.WriteLine("In-game UI needs an x64 helper and SpectatorUI.dll beside it.");
                return false;
            }
            game.Refresh();
            if (!string.Equals(game.ProcessName, "SnowRunner", StringComparison.OrdinalIgnoreCase))
                return false;
            if (game.Modules.Cast<ProcessModule>().Any(module => string.Equals(
                module.ModuleName, "SpectatorUI.dll", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("In-game UI is already loaded; reconnecting the helper bridge.");
                return true;
            }
            // LoadLibraryW is resolved relative to its actual module (a forwarded
            // export may live in KernelBase), matched in the remote process,
            // rather than assuming the two processes have identical module bases.
            IntPtr localKernel = GetModuleHandle("kernel32.dll");
            IntPtr localLoadLibrary = GetProcAddress(localKernel, "LoadLibraryW");
            if (localLoadLibrary == IntPtr.Zero) return false;
            var functionModule = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().First(module =>
                localLoadLibrary.ToInt64() >= module.BaseAddress.ToInt64() &&
                localLoadLibrary.ToInt64() < module.BaseAddress.ToInt64() + module.ModuleMemorySize);
            var remoteFunctionModule = game.Modules.Cast<ProcessModule>().First(module =>
                string.Equals(module.ModuleName, functionModule.ModuleName, StringComparison.OrdinalIgnoreCase));
            IntPtr start = new IntPtr(remoteFunctionModule.BaseAddress.ToInt64() +
                localLoadLibrary.ToInt64() - functionModule.BaseAddress.ToInt64());
            IntPtr process = OpenProcess(0x0002 | ProcessQueryInformation | ProcessVmRead |
                ProcessVmWrite | ProcessVmOperation, false, game.Id);
            if (process == IntPtr.Zero)
            {
                Console.WriteLine("Could not open SnowRunner to load the in-game UI.");
                return false;
            }
            IntPtr argument = IntPtr.Zero;
            IntPtr thread = IntPtr.Zero;
            bool completed = true;
            try
            {
                byte[] bytes = Encoding.Unicode.GetBytes(path + '\0');
                argument = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)bytes.Length,
                    0x3000, 0x04);
                if (argument == IntPtr.Zero || !TryWriteBytes(process, argument, bytes))
                    return false;
                thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, start,
                    argument, 0, out uint threadId);
                if (thread == IntPtr.Zero) return false;
                completed = WaitForSingleObject(thread, 10000) == 0;
                if (!completed)
                {
                    Console.WriteLine("In-game UI loader is still running. Its argument remains allocated until game exit.");
                    return false;
                }
                game.Refresh();
                ProcessModule remoteUi = game.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
                    string.Equals(module.FileName, path, StringComparison.OrdinalIgnoreCase));
                if (remoteUi == null)
                {
                    Console.WriteLine("SnowRunner did not load SpectatorUI.dll; see the native UI log.");
                    return false;
                }

                // DllMain intentionally does no startup work.  Resolve the
                // exported initializer's RVA from a local image loaded with
                // DONT_RESOLVE_DLL_REFERENCES, then call that RVA in the
                // already-loaded remote module after LoadLibraryW returned.
                IntPtr localImage = LoadLibraryEx(path, IntPtr.Zero,
                    DontResolveDllReferences);
                if (localImage == IntPtr.Zero)
                {
                    Console.WriteLine("Loaded SpectatorUI.dll, but its initializer image could not be inspected.");
                    return false;
                }
                try
                {
                    IntPtr localInitializer = GetProcAddress(localImage,
                        "InitializeSpectatorUI");
                    if (localInitializer == IntPtr.Zero)
                    {
                        Console.WriteLine("SpectatorUI.dll does not export InitializeSpectatorUI.");
                        return false;
                    }
                    IntPtr remoteInitializer = new IntPtr(remoteUi.BaseAddress.ToInt64() +
                        localInitializer.ToInt64() - localImage.ToInt64());
                    IntPtr initThread = CreateRemoteThread(process, IntPtr.Zero,
                        UIntPtr.Zero, remoteInitializer, IntPtr.Zero, 0,
                        out uint initThreadId);
                    if (initThread == IntPtr.Zero ||
                        WaitForSingleObject(initThread, 10000) != 0)
                    {
                        Console.WriteLine("SpectatorUI.dll loaded, but its initializer did not return successfully.");
                        if (initThread != IntPtr.Zero) CloseHandle(initThread);
                        return false;
                    }
                    CloseHandle(initThread);
                }
                finally
                {
                    FreeLibrary(localImage);
                }
                Console.WriteLine("In-game spectator UI loaded. Delete opens/closes its menu.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("In-game UI load failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (argument != IntPtr.Zero && completed)
                    VirtualFreeEx(process, argument, UIntPtr.Zero, 0x8000);
                if (thread != IntPtr.Zero) CloseHandle(thread);
                CloseHandle(process);
            }
        }
    }
}
