using System;
using System.IO;
using System.Text.Json;

namespace Agent.Service.Services
{
    public class AgentConfig
    {
        public string SiteId { get; set; } = "";
        public string SiteName { get; set; } = "";
    }

    public static class AgentConfigManager
    {
        private static readonly string FolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
            "BarisServiceSuite"
        );
        private static readonly string FilePath = Path.Combine(FolderPath, "agent_config.json");

        public static AgentConfig LoadOrCreate()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    Directory.CreateDirectory(FolderPath);
                }

                if (File.Exists(FilePath))
                {
                    string content = File.ReadAllText(FilePath);
                    var config = JsonSerializer.Deserialize<AgentConfig>(content);
                    if (config != null && !string.IsNullOrEmpty(config.SiteId))
                    {
                        return config;
                    }
                }
            }
            catch { }

            // Generate new persistent config using first 8 chars of Guid to prevent collisions
            string randomSuffix = Guid.NewGuid().ToString().Substring(0, 8).ToUpper();
            var newConfig = new AgentConfig
            {
                SiteId = $"Agent-{randomSuffix}",
                SiteName = $"Şantiye-{randomSuffix}"
            };

            try
            {
                string content = JsonSerializer.Serialize(newConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, content);
            }
            catch { }

            return newConfig;
        }
    }
}
