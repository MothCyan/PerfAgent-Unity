using System;

namespace PerfAgent.Utils
{
    /// <summary>
    /// LLM Endpoint 的 URL 规范化。
    ///
    /// 刻意不引用任何 UnityEngine 类型：这样它既能被编辑器代码使用，
    /// 也能被 Tests~ 下的独立回归工程直接编译测试（回归工程不加载 Unity）。
    /// </summary>
    public static class EndpointUrl
    {
        /// <summary>
        /// 把用户填的地址补全成能直接 POST 的 OpenAI 兼容端点。
        ///
        /// 起因：用户习惯只填 base 地址（如 https://api.deepseek.com）。
        /// 直接 POST 会打到根路径 —— 服务端返回 404 且响应体为空，
        /// 报错信息完全看不出是 URL 拼错了，极难自查。
        /// </summary>
        public static string Normalize(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return endpoint;

            string u = endpoint.Trim().TrimEnd('/');

            // 已经是完整端点，原样返回
            if (EndsWith(u, "/chat/completions")) return u;
            if (EndsWith(u, "/completions")) return u;

            // 末段已是版本号（.../v1、.../api/paas/v4）→ 只补 chat/completions
            int slash = u.LastIndexOf('/');
            string last = slash >= 0 ? u.Substring(slash + 1) : u;
            if (last.Length > 1 && (last[0] == 'v' || last[0] == 'V') && IsAllDigits(last, 1))
                return u + "/chat/completions";

            // 裸域名 / 根路径 → 补 /v1/chat/completions
            return u + "/v1/chat/completions";
        }

        static bool EndsWith(string s, string suffix)
        {
            if (s.Length < suffix.Length) return false;
            return string.Compare(s, s.Length - suffix.Length, suffix, 0, suffix.Length,
                StringComparison.OrdinalIgnoreCase) == 0;
        }

        static bool IsAllDigits(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }
    }
}
