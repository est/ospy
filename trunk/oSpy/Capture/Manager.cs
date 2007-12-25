//
// Copyright (c) 2007 Ole André Vadla Ravnås <oleavr@gmail.com>
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace oSpy.Capture
{
    public class Error : Exception
    {
        public Error (string msg)
            : base (msg)
        {
        }

        public Error (string msg, params object[] args)
            : base (String.Format (msg, args))
        {
        }
    }

    public class Manager
    {
        protected const int MAX_PATH = 260;
        protected const int ERROR_ALREADY_EXISTS = 183;

        protected const int PROCESS_ALL_ACCESS = (STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0xFFF);
        protected const int STANDARD_RIGHTS_REQUIRED = 0xF0000;
        protected const int SYNCHRONIZE = 0x100000;

        protected const int MEM_COMMIT = 0x1000;
        protected const int PAGE_READWRITE = 0x4;

        protected const int STILL_ACTIVE = STATUS_PENDING;
        protected const int STATUS_PENDING = 0x103;

        protected enum enumProtect : uint
        {
            PAGE_NOACCESS = 0x1,
            PAGE_READONLY = 0x2,
            PAGE_READWRITE = 0x4,
            PAGE_WRITECOPY = 0x8,
            PAGE_EXECUTE = 0x10
        };
        protected enum enumFileMap : uint
        {
            FILE_MAP_READ = 0x4,
            FILE_MAP_WRITE = 0x2,
            FILE_MAP_COPY = 0x1,
            FILE_MAP_ALL_ACCESS = 0x1 + 0x2 + 0x4 + 0x8 + 0x10 + 0xF0000
        };

        [DllImport("Kernel32.dll", EntryPoint = "CreateFileMapping",
            SetLastError = true, CharSet = CharSet.Unicode)]
        protected static extern IntPtr CreateFileMapping(uint hFile,
          IntPtr lpAttributes, enumProtect flProtect,
          uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);
        [DllImport("Kernel32.dll", EntryPoint = "MapViewOfFile",
            SetLastError = true, CharSet = CharSet.Unicode)]
        protected static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject,
          enumFileMap dwDesiredAccess, uint dwFileOffsetHigh,
          uint dwFileOffsetLow, uint dwNumberOfBytesToMap);
        [DllImport("Kernel32.dll", EntryPoint = "UnmapViewOfFile",
            SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.VariantBool)]
        protected static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        protected static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        protected static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        protected static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        protected static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle,
           uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        protected static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        protected static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        protected static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
           byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        protected static extern IntPtr CreateRemoteThread(IntPtr hProcess,
            IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress,
            IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern SafeWaitHandle CreateEvent(IntPtr lpEventAttributes, bool bManualReset,
                                                 bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        protected static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        public const int MAX_SOFTWALL_RULES = 128;

        [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
        public struct Capture
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            public string LogPath;
            public volatile UInt32 LogIndex;
            public volatile UInt32 LogSize;

            public UInt32 NumSoftwallRules;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_SOFTWALL_RULES)]
            public Softwall.Rule[] SoftwallRules;
        }

        private Process[] processes = null;
        private Softwall.Rule[] softwallRules = null;
        private Device[] devices = null;

        private IntPtr[] handles = null;
        private IProgressFeedback progress = null;

        private IntPtr fileMapping, cfgPtr;
        private IntPtr logIndexPtr, logSizePtr;

        private string capturePath;
        public string CapturePath
        {
            get { return capturePath; }
        }

        private int eventCount;
        public int EventCount
        {
            get { return eventCount; }
        }

        private int captureSize;
        public int CaptureSize
        {
            get { return captureSize; }
        }

        public Manager()
        {
        }

        public void StartCapture(Process[] processes, Softwall.Rule[] softwallRules, Device[] devices, IProgressFeedback progress)
        {
            this.processes = processes;
            this.softwallRules = softwallRules;
            this.devices = devices;
            this.progress = progress;

            Thread th = new Thread(StartCaptureThread);
            th.Start();
        }

        public void StopCapture(IProgressFeedback progress)
        {
            this.progress = progress;

            Thread th = new Thread(StopCaptureThread);
            th.Start();
        }

        public void CloseCapture()
        {
            Directory.Delete(capturePath, true);
        }

        public void UpdateCaptureStatistics()
        {
            eventCount = Marshal.ReadInt32(logIndexPtr);
            captureSize = Marshal.ReadInt32(logSizePtr);
        }

        private void StartCaptureThread()
        {
            try
            {
                if (devices.Length > 0)
                    EnsureUsbAgentService ();

                foreach (Device device in devices)
                    device.AddLowerFilter (Constants.UsbAgentName);

                PrepareCapture (processes, softwallRules);

                DoInjection ();
            }
            catch (Error e)
            {
                // TODO: roll back

                progress.OperationFailed (e.Message);
                return;
            }

            progress.OperationComplete();
        }

        private void StopCaptureThread()
        {
            try
            {
                DoUnInjection();

                UpdateCaptureStatistics();

                UnmapViewOfFile(cfgPtr);
                CloseHandle(fileMapping);

                StopUsbAgentService ();
            }
            catch (Error e)
            {
                progress.OperationFailed(e.Message);
                return;
            }

            progress.OperationComplete();
        }

        private void EnsureUsbAgentService ()
        {
            IntPtr manager = WinApi.OpenSCManager (null, null, WinApi.SC_MANAGER_ALL_ACCESS);
            if (manager == IntPtr.Zero)
                throw new Error ("OpenSCManager failed");

            IntPtr service = IntPtr.Zero;

            try
            {
                service = WinApi.OpenService (manager, Constants.UsbAgentName, WinApi.SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero && Marshal.GetLastWin32Error () == WinApi.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    service = WinApi.CreateService (manager, Constants.UsbAgentName, Constants.UsbAgentDescription, WinApi.SERVICE_ALL_ACCESS, WinApi.SERVICE_KERNEL_DRIVER, WinApi.SERVICE_DEMAND_START, WinApi.SERVICE_ERROR_NORMAL, Constants.UsbAgentPath, null, 0, null, null, null);
                    if (service == IntPtr.Zero)
                        throw new Error ("CreateService failed");
                }

                if (!WinApi.StartService (service, 0, null) && Marshal.GetLastWin32Error () != WinApi.ERROR_SERVICE_ALREADY_RUNNING)
                    throw new Error ("Failed to start service: 0x{0:x8}", Marshal.GetLastWin32Error ());
            }
            finally
            {
                if (service != IntPtr.Zero)
                    WinApi.CloseServiceHandle (service);

                WinApi.CloseServiceHandle (manager);
            }
        }

        private void StopUsbAgentService ()
        {
            IntPtr manager = WinApi.OpenSCManager (null, null, WinApi.SC_MANAGER_ALL_ACCESS);
            if (manager == IntPtr.Zero)
                throw new Error ("OpenSCManager failed");

            IntPtr service = IntPtr.Zero;

            try
            {
                service = WinApi.OpenService (manager, Constants.UsbAgentName, WinApi.SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero)
                    throw new Error ("OpenService failed");

                WinApi.SERVICE_STATUS status = new WinApi.SERVICE_STATUS ();
                if (!WinApi.ControlService (service, WinApi.SERVICE_CONTROL_STOP, ref status))
                    throw new Error ("Failed to stop service: 0x{0:x8}", Marshal.GetLastWin32Error ());
            }
            finally
            {
                if (service != IntPtr.Zero)
                    WinApi.CloseServiceHandle (service);
            }
        }

        private void PrepareCapture(Process[] processes, Softwall.Rule[] softwallRules)
        {
            progress.ProgressUpdate("Preparing capture", 100);

            fileMapping = CreateFileMapping(0xFFFFFFFFu, IntPtr.Zero,
                                            enumProtect.PAGE_READWRITE,
                                            0, (uint)Marshal.SizeOf(typeof(Capture)),
                                            "oSpyCapture");
            if (Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS)
                throw new Error("Is another instance of oSpy or one or more processes previously monitored still alive?");

            cfgPtr = MapViewOfFile(fileMapping, enumFileMap.FILE_MAP_WRITE, 0, 0, (uint)Marshal.SizeOf(typeof(Capture)));

            // Create a temporary directory for the capture
            do
            {
                capturePath = String.Format("{0}{1}", Path.GetTempPath(), Path.GetRandomFileName());
            }
            while (Directory.Exists(capturePath));

            Directory.CreateDirectory(capturePath);

            // Write the temporary directory to shared memory
            char[] tmpDirChars = capturePath.ToCharArray();
            IntPtr ptr = (IntPtr)(cfgPtr.ToInt64() + Marshal.OffsetOf(typeof(Capture), "LogPath").ToInt64());
            Marshal.Copy(tmpDirChars, 0, ptr, tmpDirChars.Length);

            // And make it NUL-terminated
            Marshal.WriteInt16(ptr, tmpDirChars.Length * Marshal.SizeOf(typeof(UInt16)), 0);

            // Initialize LogIndex and LogSize
            logIndexPtr = (IntPtr)(cfgPtr.ToInt64() + Marshal.OffsetOf(typeof(Capture), "LogIndex").ToInt64());
            logSizePtr = (IntPtr)(cfgPtr.ToInt64() + Marshal.OffsetOf(typeof(Capture), "LogSize").ToInt64());

            Marshal.WriteInt32(logIndexPtr, 0);
            Marshal.WriteInt32(logSizePtr, 0);

            // Initialize softwall rules
            Marshal.WriteInt32(cfgPtr, Marshal.OffsetOf(typeof(Capture), "NumSoftwallRules").ToInt32(), softwallRules.Length);

            ptr = (IntPtr)(cfgPtr.ToInt64() + Marshal.OffsetOf(typeof(Capture), "SoftwallRules").ToInt64());
            foreach (Softwall.Rule rule in softwallRules)
            {
                Marshal.StructureToPtr(rule, ptr, false);

                ptr = (IntPtr)(ptr.ToInt64() + Marshal.SizeOf(typeof(Softwall.Rule)));
            }

            // Copy configuration XML
            string configPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) + "\\config.xml";
            File.Copy(configPath, String.Format("{0}\\config.xml", capturePath));
        }

        private void DoInjection()
        {
            // Inject into the desired process IDs
            List<IntPtr> threadHandles = new List<IntPtr>(processes.Length);
            for (int i = 0; i < processes.Length; i++)
            {
                int percentComplete = (int)(((float)(i + 1) / (float)processes.Length) * 100.0f);
                progress.ProgressUpdate("Injecting logging agents", percentComplete);
                threadHandles.Add(InjectDll(processes[i].Id));
            }

            // Wait for the threads to exit
            handles = GetThreadExitCodes(threadHandles.ToArray(), "Waiting for agents to initialize");

            // Check if any of them failed
            for (int i = 0; i < processes.Length; i++)
            {
                if (handles[i] == IntPtr.Zero)
                {
                    throw new Error(String.Format("Failed to inject logging agent into process {0} with pid={1}",
                                                  processes[i].ProcessName, processes[i].Id));
                }
            }
        }

        private void DoUnInjection()
        {
            // Uninject the DLLs previously injected
            List<IntPtr> threadHandles = new List<IntPtr>(processes.Length);
            for (int i = 0; i < processes.Length; i++)
            {
                int percentComplete = (int)(((float)(i + 1) / (float)processes.Length) * 100.0f);
                progress.ProgressUpdate("Uninjecting logging agents", percentComplete);

                if (!processes[i].HasExited)
                    threadHandles.Add(UnInjectDll(processes[i].Id, handles[i]));
            }

            if (threadHandles.Count == 0)
                return;

            // Wait for the threads to exit
            handles = GetThreadExitCodes(threadHandles.ToArray(), "Waiting for agents to finish uninjecting");

            // Check if any of them failed
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] == IntPtr.Zero)
                {
                    throw new Error(String.Format("Failed to uninject logging agent from process {0} with pid={1}",
                                                  processes[i].ProcessName, processes[i].Id));
                }
            }
        }

        private IntPtr[] GetThreadExitCodes(IntPtr[] handles, string progressMsg)
        {
            Dictionary<IntPtr, IntPtr> exitCodes = new Dictionary<IntPtr, IntPtr>(handles.Length);
            List<IntPtr> pendingHandles = new List<IntPtr>(handles);
            List<IntPtr> completedHandles = new List<IntPtr>(handles.Length);

            while (pendingHandles.Count > 0)
            {
                int completionCount = processes.Length - pendingHandles.Count + 1;
                int percentComplete = (int)(((float)completionCount / (float)processes.Length) * 100.0f);

                progress.ProgressUpdate(progressMsg, percentComplete);

                completedHandles.Clear();

                for (int i = 0; i < pendingHandles.Count; i++)
                {
                    uint exitCode;

                    if (!GetExitCodeThread(pendingHandles[i], out exitCode))
                        throw new Error("GetExitCodeThread failed");

                    if (exitCode != STILL_ACTIVE)
                    {
                        exitCodes[pendingHandles[i]] = (IntPtr)exitCode;
                        completedHandles.Add(pendingHandles[i]);
                    }
                }

                foreach (IntPtr handle in completedHandles)
                {
                    pendingHandles.Remove(handle);
                }

                Thread.Sleep(200);
            }

            IntPtr[] result = new IntPtr[handles.Length];
            for (int i = 0; i < handles.Length; i++)
            {
                result[i] = exitCodes[handles[i]];
            }
            return result;
        }

        private IntPtr InjectDll(int processId)
        {
            string agentDLLPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) + "\\oSpyAgent.dll";
            if (!File.Exists(agentDLLPath))
                throw new Error(agentDLLPath + " not found");

            return InjectDll(processId, agentDLLPath);
        }

        private IntPtr InjectDll(int processId, string dllPath)
        {
            // Create a unicode (UTF-16) C-string
            UnicodeEncoding enc = new UnicodeEncoding();
            byte[] rawdllStr = enc.GetBytes(dllPath);
            byte[] dllStr = new byte[rawdllStr.Length + 2];
            rawdllStr.CopyTo(dllStr, 0);
            dllStr[dllStr.Length - 2] = 0;
            dllStr[dllStr.Length - 1] = 0;

            // Get offset of LoadLibraryW in kernel32
            IntPtr kernelMod = LoadLibrary("kernel32.dll");
            if (kernelMod == IntPtr.Zero)
                throw new Error("LoadLibrary of kernel32.dll failed");

            try
            {
                IntPtr loadLibraryAddr = GetProcAddress(kernelMod, "LoadLibraryW");
                if (loadLibraryAddr == IntPtr.Zero)
                    throw new Error("GetProcAddress of LoadLibraryW failed");

                // Open the target process
                IntPtr proc = OpenProcess(PROCESS_ALL_ACCESS, true, (uint) processId);
                if (proc == IntPtr.Zero)
                    throw new Error("OpenProcess failed");

                try
                {
                    // Allocate memory for the string in the target process
                    IntPtr remoteDllStr = VirtualAllocEx(proc, IntPtr.Zero,
                        (uint) dllStr.Length, MEM_COMMIT, PAGE_READWRITE);
                    if (remoteDllStr == IntPtr.Zero)
                        throw new Error("VirtualAllocEx failed");

                    // Write the string to the allocated buffer
                    IntPtr bytesWritten;
                    if (!WriteProcessMemory(proc, remoteDllStr, dllStr, (uint)dllStr.Length, out bytesWritten))
                        throw new Error("WriteProcessMemory failed");

                    // Launch the thread, being LoadLibraryW
                    IntPtr remoteThreadHandle = CreateRemoteThread(proc, IntPtr.Zero, 0, loadLibraryAddr, remoteDllStr, 0, IntPtr.Zero);
                    if (remoteThreadHandle == IntPtr.Zero)
                        throw new Error("CreateRemoteThread failed");

                    return remoteThreadHandle;
                }
                finally
                {
                    CloseHandle(proc);
                }
            }
            finally
            {
                FreeLibrary(kernelMod);
            }
        }

        private IntPtr UnInjectDll(int processId, IntPtr handle)
        {
            // Get offset of FreeLibrary in kernel32
            IntPtr kernelMod = LoadLibrary("kernel32.dll");
            if (kernelMod == IntPtr.Zero)
                throw new Error("LoadLibrary of kernel32.dll failed");

            try
            {
                IntPtr freeLibraryAddr = GetProcAddress(kernelMod, "FreeLibrary");
                if (freeLibraryAddr == IntPtr.Zero)
                    throw new Error("GetProcAddress of FreeLibrary failed");

                // Open the target process
                IntPtr proc = OpenProcess(PROCESS_ALL_ACCESS, true, (uint)processId);
                if (proc == IntPtr.Zero)
                    throw new Error("OpenProcess failed");

                try
                {
                    // Launch the thread, being FreeLibrary
                    IntPtr remoteThreadHandle = CreateRemoteThread(proc, IntPtr.Zero, 0, freeLibraryAddr, handle, 0, IntPtr.Zero);
                    if (remoteThreadHandle == IntPtr.Zero)
                        throw new Error("CreateRemoteThread failed");

                    return remoteThreadHandle;
                }
                finally
                {
                    CloseHandle(proc);
                }
            }
            finally
            {
                FreeLibrary(kernelMod);
            }
        }
    }
}