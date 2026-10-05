using System;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>
    /// 敏感数据（API Key、导出包）的保护接口。
    ///
    /// <para><b>现在的状态：只留接口，不做真加密</b></para>
    /// 这是刻意的：加密算法与密钥生命周期（放哪、怎么轮换、绑不绑机器）是产品决策，
    /// 定错了比不做更糟。所以先把**唯一替换点**放在这里，默认实现是明文并**明确标注**，
    /// 后期接入真实实现时只需要：
    ///   <code>SecretProtection.Register(new DpapiSecretProtector());</code>
    /// 上层（配置读写、导出包）一行都不用改。
    ///
    /// <para><b>接入时建议的做法</b></para>
    /// 1. Windows 用 DPAPI（`ProtectedData`，按当前用户加密），macOS 用 Keychain，
    ///    Linux 用 libsecret；三者都拿不到时退回「不落盘，只存内存 + 环境变量」；
    /// 2. 导出包用一次性对称密钥（AES-GCM），密钥随包外带口令派生（PBKDF2/Argon2），
    ///    绝不用固定内置密钥 —— 那等于没加密；
    /// 3. **不加密时必须让用户看得见**（<see cref="Describe"/> 会显示在设置面板里），
    ///    不允许「以为加密了其实没加密」。
    /// </summary>
    public interface ISecretProtector
    {
        /// <summary>实现名（显示用，例如 "DPAPI（当前用户）"）。</summary>
        string Name { get; }

        /// <summary>是否真的加密了。设置面板会据此显示警告。</summary>
        bool IsEncrypting { get; }

        /// <summary>存盘/打包前调用。</summary>
        string Protect(string plaintext);

        /// <summary>读回时调用。无法解密应返回 null（不要返回乱码）。</summary>
        string Unprotect(string stored);
    }

    /// <summary>
    /// 默认实现：**不加密**，只做 Base64 混淆（防止顺手一眼看到 Key）。
    /// 名字与 <see cref="IsEncrypting"/> 都会如实告诉用户它不提供安全性。
    /// </summary>
    public sealed class PlaintextProtector : ISecretProtector
    {
        public string Name { get { return "明文（仅 Base64 混淆，未加密）"; } }
        public bool IsEncrypting { get { return false; } }

        public string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            try { return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)); }
            catch { return ""; }
        }

        public string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(stored)); }
            catch { return null; }
        }
    }

    /// <summary>保护实现的注册点（全局唯一）。</summary>
    public static class SecretProtection
    {
        static ISecretProtector _current = new PlaintextProtector();

        /// <summary>当前实现。默认明文 —— 后期在这里注册真实现即可，无需改调用方。</summary>
        public static ISecretProtector Current { get { return _current; } }

        public static void Register(ISecretProtector protector)
        {
            if (protector == null) return;
            _current = protector;
            Debug.Log("[PerfAgent] 敏感数据保护实现已切换为：" + protector.Name
                      + (protector.IsEncrypting ? "" : "（注意：它不提供加密保护）"));
        }

        public static string Describe()
        {
            return _current.Name + (_current.IsEncrypting ? "" : "　⚠ 当前不提供加密保护");
        }

        public static string Protect(string plaintext)
        {
            try { return _current.Protect(plaintext); } catch { return ""; }
        }

        public static string Unprotect(string stored)
        {
            try { return _current.Unprotect(stored); } catch { return null; }
        }
    }
}
