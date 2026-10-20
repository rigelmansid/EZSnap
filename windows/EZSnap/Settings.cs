using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EZSnap
{
    /// <summary>设置，存在 %APPDATA%\EZSnap\settings.txt，每行“键=值”。</summary>
    internal sealed class Settings
    {
        private readonly string path;
        private readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public Settings(string path = null)
        {
            this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EZSnap", "settings.txt");
            try
            {
                if (!File.Exists(this.path)) return;
                foreach (var line in File.ReadAllLines(this.path, Encoding.UTF8))
                {
                    var i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public string Get(string key) => values.TryGetValue(key, out var v) ? v : null;

        public void Set(string key, string value)
        {
            if (value == null) values.Remove(key); else values[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>();
                foreach (var kv in values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;
        public bool GetBool(string key) => Get(key) == "1";
    }
}
