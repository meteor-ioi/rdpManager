using System;
using System.Runtime.InteropServices;
using System.Text;

namespace rdpManager.Helpers
{
    public static class CredentialHelper
    {
        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

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

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr userCredential);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string targetName, uint type, uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        /// <summary>
        /// 安全保存凭据至 Windows 凭据管理器
        /// </summary>
        public static bool SaveCredential(string targetName, string username, string password)
        {
            var credential = new CREDENTIAL();
            credential.Type = CRED_TYPE_GENERIC;
            credential.TargetName = targetName;
            credential.UserName = username;
            credential.Persist = CRED_PERSIST_LOCAL_MACHINE;

            byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
            credential.CredentialBlobSize = (uint)passwordBytes.Length;
            credential.CredentialBlob = Marshal.AllocCoTaskMem(passwordBytes.Length);

            try
            {
                Marshal.Copy(passwordBytes, 0, credential.CredentialBlob, passwordBytes.Length);
                return CredWrite(ref credential, 0);
            }
            finally
            {
                if (credential.CredentialBlob != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(credential.CredentialBlob);
                }
            }
        }

        /// <summary>
        /// 从 Windows 凭据管理器读取凭据
        /// </summary>
        public static bool GetCredential(string targetName, out string username, out string password)
        {
            username = string.Empty;
            password = string.Empty;

            if (CredRead(targetName, CRED_TYPE_GENERIC, 0, out IntPtr credentialPtr))
            {
                try
                {
                    var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                    username = credential.UserName;

                    if (credential.CredentialBlobSize > 0 && credential.CredentialBlob != IntPtr.Zero)
                    {
                        byte[] passwordBytes = new byte[credential.CredentialBlobSize];
                        Marshal.Copy(credential.CredentialBlob, passwordBytes, 0, (int)credential.CredentialBlobSize);
                        password = Encoding.Unicode.GetString(passwordBytes);
                    }
                    return true;
                }
                finally
                {
                    CredFree(credentialPtr);
                }
            }
            return false;
        }

        /// <summary>
        /// 从 Windows 凭据管理器中删除凭据
        /// </summary>
        public static bool DeleteCredential(string targetName)
        {
            return CredDelete(targetName, CRED_TYPE_GENERIC, 0);
        }
    }
}
