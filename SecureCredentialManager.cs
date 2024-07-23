using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Security;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class SecureCredentialManager
    {
        private const string CredentialType = "BitwardenFlowPlugin";

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredReadW(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredFree(IntPtr cred);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredDeleteW(string target, uint type, int flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        public static void SaveCredential(string username, SecureString password)
        {
            var passwordPtr = IntPtr.Zero;
            try
            {
                passwordPtr = Marshal.SecureStringToGlobalAllocUnicode(password);
                var credential = new CREDENTIAL
                {
                    Type = 1, // CRED_TYPE_GENERIC
                    TargetName = CredentialType,
                    CredentialBlobSize = (uint)password.Length * 2,
                    CredentialBlob = passwordPtr,
                    Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
                    UserName = username
                };

                if (!CredWriteW(ref credential, 0))
                {
                    throw new Exception($"Failed to write credential. Error code: {Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                if (passwordPtr != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(passwordPtr);
                }
            }
        }

        public static SecureString? RetrieveCredential(string username)
        {
            IntPtr credentialPtr = IntPtr.Zero;
            try
            {
                if (!CredReadW(CredentialType, 1, 0, out credentialPtr))
                {
                    return null;
                }

                var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                if (credential.UserName != username)
                {
                    return null;
                }

                var passwordBytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, passwordBytes, 0, (int)credential.CredentialBlobSize);
                var password = Encoding.Unicode.GetString(passwordBytes);

                var securePassword = new SecureString();
                foreach (char c in password)
                {
                    securePassword.AppendChar(c);
                }
                securePassword.MakeReadOnly();
                return securePassword;
            }
            finally
            {
                if (credentialPtr != IntPtr.Zero)
                {
                    CredFree(credentialPtr);
                }
            }
        }

        public static void DeleteCredential()
        {
            if (!CredDeleteW(CredentialType, 1, 0))
            {
                throw new Exception($"Failed to delete credential. Error code: {Marshal.GetLastWin32Error()}");
            }
        }
    }
}