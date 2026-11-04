// Bridge Warden's Fairlead client key, read from Windows Credential Manager. C# 5, framework assemblies only.
//
// Fairlead's rule (Fairlead docs/API.md, "How a tool gets its Fairlead key"): a tool reads its key from the generic
// credential Fairlead/<AppName>, saved by Fairlead's Clients page ("install for"), stored as UTF-16 text like
// cmdkey /generic:Fairlead/BridgeWarden /pass:... would. If it is missing the tool tells the user to open Fairlead
// and install a key; it never falls back to an environment variable.
//
// The only file allowed advapi32.dll (host\Build.ps1), and it reads exactly one target name. Nothing is written,
// listed or cached on disk; the key lives in memory only while a request is being sent.
using System;
using System.Runtime.InteropServices;

namespace BridgeWarden
{
    public static class FairleadKey
    {
        public const string APP_NAME = "BridgeWarden";
        public const string TARGET = "Fairlead/" + APP_NAME;
        const int CRED_TYPE_GENERIC = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern void CredFree(IntPtr buffer);

        // Where the key comes from. Tests swap this so they never touch the real Credential Manager.
        public static Func<string> Reader = ReadCredential;

        // The key, or null when the credential does not exist (or is empty).
        public static string Read()
        {
            string k = Reader();
            return string.IsNullOrEmpty(k) || k.Trim().Length == 0 ? null : k.Trim();
        }

        static string ReadCredential()
        {
            IntPtr p;
            if (!CredRead(TARGET, CRED_TYPE_GENERIC, 0, out p)) return null;
            try
            {
                CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
                if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize <= 0) return null;
                string key = Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2).Trim('\0', ' ', '\r', '\n');
                return key.Length > 0 ? key : null;
            }
            finally { CredFree(p); }
        }

        public const string MISSING = "No Fairlead key for " + APP_NAME + ": open Fairlead, Clients, and install a key for " + APP_NAME +
                                      " (it is saved in Windows Credential Manager as " + TARGET + ")";
    }
}
