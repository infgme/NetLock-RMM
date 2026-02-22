using System;
using System.Runtime.InteropServices;

namespace NetLock_RMM_User_Process.Linux.Helper
{
    /// <summary>
    /// Linux libc bindings for shared memory operations (used for fast screen capture)
    /// </summary>
    internal static class LibC
    {
        private const string LibCName = "libc.so.6";

        // Shared memory functions
        [DllImport(LibCName, EntryPoint = "shmget")]
        public static extern int shmget(int key, IntPtr size, int shmflg);

        [DllImport(LibCName, EntryPoint = "shmat")]
        public static extern IntPtr shmat(int shmid, IntPtr shmaddr, int shmflg);

        [DllImport(LibCName, EntryPoint = "shmdt")]
        public static extern int shmdt(IntPtr shmaddr);

        [DllImport(LibCName, EntryPoint = "shmctl")]
        public static extern int shmctl(int shmid, int cmd, IntPtr buf);

        // Memory functions
        [DllImport(LibCName, EntryPoint = "memcpy")]
        public static extern IntPtr memcpy(IntPtr dest, IntPtr src, IntPtr n);

        [DllImport(LibCName, EntryPoint = "free")]
        public static extern void free(IntPtr ptr);

        // Shared memory constants
        public const int IPC_PRIVATE = 0;
        public const int IPC_CREAT = 0x0200;
        public const int IPC_EXCL = 0x0400;
        public const int IPC_RMID = 0;
        public const int SHM_RDONLY = 0x1000;

        // Permission bits
        public const int S_IRUSR = 0x0100;
        public const int S_IWUSR = 0x0080;
        public const int S_IRGRP = 0x0020;
        public const int S_IWGRP = 0x0010;
        public const int S_IROTH = 0x0004;
        public const int S_IWOTH = 0x0002;

        // Common permission combination (0777)
        public const int PERM_ALL = 0x01FF;
    }
}

