namespace PerfAgent.Utils
{
    /// <summary>
    /// 「复制」这件事对用户说的话（纯逻辑，离线有回归）。
    ///
    /// 为什么要单独抽出来（实测反馈：「我现在复制结论，结论不会被复制」）：
    /// 复制本身是**静默**的 —— 内容为空、生成报告抛异常、剪贴板写入被系统拒掉，
    /// 用户看到的都是「什么都没发生」，于是只会以为按钮坏了、工具坏了。
    /// 所以每一次复制都必须有一句人看得懂的话：成功了给字符数，失败了给原因。
    ///
    /// 另外**回读校验**（写完立刻读回来对字符数）是唯一能确认「真的写进去了」的手段 ——
    /// 但读回不一致不一定是我们写失败（别的程序可能占着剪贴板），
    /// 所以措辞是「已写入，但回读校验不一致，粘贴为空请再点一次」，而不是断言失败。
    /// </summary>
    internal static class CopyFeedback
    {
        /// <summary>
        /// 生成给用户看的结论句。<paramref name="length"/> 为 0 表示没有内容可复制。
        /// </summary>
        public static string Describe(string what, int length, bool verified)
        {
            if (length <= 0)
                return "没有可复制的内容（" + what + "）：这次没有产出正文，原因见 Console。";

            return verified
                ? ("已复制 " + what + "（" + length + " 字符，回读校验通过）。")
                : ("已写入剪贴板（" + what + "，" + length + " 字符），但回读校验不一致 —— 粘贴时若为空请再点一次。");
        }

        /// <summary>回读校验是否算通过：写入后读回来，字符数一致即认为成功。</summary>
        public static bool Verified(string text, string readBack)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return !string.IsNullOrEmpty(readBack) && readBack.Length == text.Length;
        }
    }
}
