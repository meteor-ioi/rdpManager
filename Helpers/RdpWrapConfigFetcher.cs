using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace rdpManager.Helpers
{
    public static class RdpWrapConfigFetcher
    {
        // 预设的代理池源，按优先级排序。目前主要使用 sebaxakerhtc 源。
        private static readonly string[] ConfigSources = new string[]
        {
            // ghproxy 国内代理
            "https://ghproxy.net/https://raw.githubusercontent.com/sebaxakerhtc/rdpwrap.ini/master/rdpwrap.ini",
            // fastly jsdelivr CDN 加速
            "https://fastly.jsdelivr.net/gh/sebaxakerhtc/rdpwrap.ini@master/rdpwrap.ini",
            // kkgithub 镜像
            "https://raw.kkgithub.com/sebaxakerhtc/rdpwrap.ini/master/rdpwrap.ini",
            // 原生 GitHub 直连 (备选)
            "https://raw.githubusercontent.com/sebaxakerhtc/rdpwrap.ini/master/rdpwrap.ini"
        };

        /// <summary>
        /// 从网络轮询代理源下载最新的 rdpwrap.ini 并覆盖保存
        /// </summary>
        /// <param name="savePath">本地保存路径</param>
        /// <returns>是否下载并保存成功</returns>
        public static async Task<bool> FetchLatestConfigAsync(string savePath)
        {
            using (HttpClient client = new HttpClient())
            {
                // 每个源的请求超时设置为 6 秒
                client.Timeout = TimeSpan.FromSeconds(6);
                
                foreach (var url in ConfigSources)
                {
                    try
                    {
                        Logger.LogInfo($"尝试从 {url} 下载 rdpwrap.ini ...");
                        HttpResponseMessage response = await client.GetAsync(url);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            string content = await response.Content.ReadAsStringAsync();
                            
                            // 基本的内容校验：确保下载下来的文件真的是 ini，而不是某些代理抛出的 502 HTML 页面
                            if (!string.IsNullOrWhiteSpace(content) && content.Contains("[Main]") && content.Contains("10.0."))
                            {
                                File.WriteAllText(savePath, content);
                                Logger.LogInfo($"成功从 {url} 更新了 rdpwrap.ini！");
                                return true;
                            }
                            else
                            {
                                Logger.LogWarning($"从 {url} 下载的文件内容不合法，跳过。");
                            }
                        }
                        else
                        {
                            Logger.LogWarning($"从 {url} 下载失败，状态码: {response.StatusCode}。");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"请求 {url} 发生异常: {ex.Message}，尝试下一个源...");
                    }
                }
            }

            Logger.LogError("所有 rdpwrap.ini 代理源均下载失败，请检查网络或手动更新。");
            return false;
        }
    }
}
