using System;
using System.DirectoryServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace rdpManager.Helpers
{
    public static class AccountHelper
    {
        /// <summary>
        /// 一键创建本地机器人账户，并配置管理员与远程桌面权限，同时应用首登优化
        /// </summary>
        public static bool CreateRobotAccount(string username, string password, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                // 1. 创建本地用户
                using (DirectoryEntry localMachine = new DirectoryEntry("WinNT://" + Environment.MachineName + ",computer"))
                {
                    // 检查账户是否已存在
                    bool userExists = false;
                    foreach (DirectoryEntry child in localMachine.Children)
                    {
                        if (child.SchemaClassName == "User" && string.Equals(child.Name, username, StringComparison.OrdinalIgnoreCase))
                        {
                            userExists = true;
                            break;
                        }
                    }

                    if (userExists)
                    {
                        errorMessage = $"用户 '{username}' 已存在。";
                        return false;
                    }

                    using (DirectoryEntry newUser = localMachine.Children.Add(username, "user"))
                    {
                        newUser.Invoke("SetPassword", new object[] { password });
                        newUser.Invoke("Put", new object[] { "Description", "RPA 自动化隔离运行账号" });
                        newUser.CommitChanges();

                        // 设置密码永不过期 (UF_DONT_EXPIRE_PASSWORD = 0x10000)
                        int flags = (int)newUser.Properties["UserFlags"].Value;
                        newUser.Properties["UserFlags"].Value = flags | 0x10000;
                        newUser.CommitChanges();

                        // 2. 将用户加入管理员组和远程桌面用户组
                        AddUserToGroup(newUser.Path, WellKnownSidType.BuiltinAdministratorsSid);
                        AddUserToGroup(newUser.Path, WellKnownSidType.BuiltinRemoteDesktopUsersSid);
                    }
                }

                // 3. 应用系统级首登优化（关闭欢迎动画、Edge引导等）
                ApplySystemLogonOptimizations();

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 根据 Well-Known SID 安全地将用户加入本地组，自适应系统语言（中文/英文）
        /// </summary>
        private static void AddUserToGroup(string userPath, WellKnownSidType sidType)
        {
            try
            {
                // 根据 SID 获取本地化组名（如：Administrators / 管理员组）
                SecurityIdentifier sid = new SecurityIdentifier(sidType, null);
                NTAccount ntAccount = (NTAccount)sid.Translate(typeof(NTAccount));
                
                // ntAccount.Value 格式为 "BUILTIN\Administrators" 或 "BUILTIN\Administrators"
                string[] parts = ntAccount.Value.Split('\\');
                string groupName = parts.Length > 1 ? parts[1] : parts[0];

                using (DirectoryEntry group = new DirectoryEntry($"WinNT://{Environment.MachineName}/{groupName},group"))
                {
                    group.Invoke("Add", new object[] { userPath });
                    group.CommitChanges();
                }
            }
            catch (Exception ex)
            {
                // 记录错误或抛出，但为了稳定性，如果是组已包含该用户导致的异常可忽略
                System.Diagnostics.Debug.WriteLine($"添加用户到组时发生异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 应用系统注册表配置优化，跳过机器人首次登录时的欢迎动画、OneDrive和浏览器引导
        /// </summary>
        private static void ApplySystemLogonOptimizations()
        {
            try
            {
                // 1. 禁用首次登录欢迎动画
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true))
                {
                    key?.SetValue("EnableFirstLogonAnimation", 0, RegistryValueKind.DWord);
                }

                // 2. 禁用 Edge 浏览器首次运行向导（防卡死）
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Edge", true))
                {
                    key.SetValue("HideFirstRunExperience", 1, RegistryValueKind.DWord);
                }

                // 3. 禁用 Windows 消费体验与推荐广告（防弹窗）
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", true))
                {
                    key.SetValue("DisableWindowsConsumerFeatures", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"应用首登优化注册表时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前系统中非内置的本地账户列表
        /// </summary>
        public static System.Collections.Generic.List<LocalAccountInfo> GetLocalAccounts()
        {
            var accounts = new System.Collections.Generic.List<LocalAccountInfo>();
            try
            {
                using (DirectoryEntry localMachine = new DirectoryEntry("WinNT://" + Environment.MachineName + ",computer"))
                {
                    foreach (DirectoryEntry child in localMachine.Children)
                    {
                        if (child.SchemaClassName == "User")
                        {
                            string name = child.Name;

                            // 过滤系统内置账户
                            if (name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("Guest", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("DefaultAccount", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("UtilityVM", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string type = "标准用户";
                            try
                            {
                                SecurityIdentifier adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                                NTAccount adminAccount = (NTAccount)adminSid.Translate(typeof(NTAccount));
                                string[] parts = adminAccount.Value.Split('\\');
                                string adminGroupName = parts.Length > 1 ? parts[1] : parts[0];

                                using (DirectoryEntry group = new DirectoryEntry($"WinNT://{Environment.MachineName}/{adminGroupName},group"))
                                {
                                    if ((bool)group.Invoke("IsMember", new object[] { child.Path }))
                                    {
                                        type = "管理员";
                                    }
                                }
                            }
                            catch { }

                            string passwordExpires = "永不过期";
                            try
                            {
                                int flags = (int)(child.Properties["UserFlags"].Value ?? 0);
                                if ((flags & 0x10000) == 0)
                                {
                                    passwordExpires = "有期限";
                                }
                            }
                            catch { }

                            accounts.Add(new LocalAccountInfo
                            {
                                Name = name,
                                Type = type,
                                PasswordExpires = passwordExpires
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取本地账户列表时出错: {ex.Message}");
            }
            return accounts;
        }

        /// <summary>
        /// 删除本地账户，并清理其安全凭据
        /// </summary>
        public static bool DeleteRobotAccount(string username, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                using (DirectoryEntry localMachine = new DirectoryEntry("WinNT://" + Environment.MachineName + ",computer"))
                {
                    DirectoryEntry? userToDelete = null;
                    foreach (DirectoryEntry child in localMachine.Children)
                    {
                        if (child.SchemaClassName == "User" && string.Equals(child.Name, username, StringComparison.OrdinalIgnoreCase))
                        {
                            userToDelete = child;
                            break;
                        }
                    }

                    if (userToDelete != null)
                    {
                        localMachine.Children.Remove(userToDelete);
                        localMachine.CommitChanges();

                        // 删除凭据
                        CredentialHelper.DeleteCredential($"RDPManager:{username}");
                        return true;
                    }
                    else
                    {
                        errorMessage = $"未找到账户 '{username}'。";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }

    public class LocalAccountInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string PasswordExpires { get; set; } = string.Empty;
    }
}
