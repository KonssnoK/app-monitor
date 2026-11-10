using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppMonitor
{
    /// <summary>
    /// Turns generic host processes into something meaningful:
    /// dllhost.exe /Processid:{AppID}  ->  the COM component it hosts (looked up in the registry),
    /// rundll32.exe some.dll,Entry      ->  the DLL it runs.
    /// (svchost.exe is handled separately via the service list.)
    /// Runs on the collector thread; results are cached.
    /// </summary>
    static class ProcessNames
    {
        static readonly Regex AppIdArg = new Regex(@"/Processid:(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase);
        static readonly Regex RundllArg = new Regex(@"rundll32(?:\.exe)?""?\s+""?([^,""]+\.dll)", RegexOptions.IgnoreCase);
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> appIdToClass; // built once, lazily

        /// <summary>Plain-language hints for components that commonly cause load.</summary>
        static readonly Dictionary<string, string> Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Plan9FileSystem"] = "WSL/Docker file sharing",
            ["Thumbnail Cache Out of Proc Server"] = "Explorer thumbnails",
            ["PhotoThumbnailProvider"] = "Explorer thumbnails",
        };

        public static string Describe(string name, string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return "";
            try
            {
                if (name.Equals("dllhost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var m = AppIdArg.Match(commandLine);
                    return m.Success ? ComName(m.Groups[1].Value) : "";
                }
                if (name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var m = RundllArg.Match(commandLine);
                    return m.Success ? System.IO.Path.GetFileName(m.Groups[1].Value) : "";
                }
            }
            catch { }
            return "";
        }

        static string ComName(string appId)
        {
            if (cache.TryGetValue(appId, out var cached)) return cached;
            string name = null;

            // 1) the AppID's own description
            using (var k = Registry.ClassesRoot.OpenSubKey(@"AppID\" + appId))
                name = (k?.GetValue("") as string)?.Trim();

            // 2) otherwise the class registered under that AppID (often the AppID itself is a CLSID)
            if (string.IsNullOrEmpty(name))
                using (var k = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + appId))
                    name = (k?.GetValue("") as string)?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                if (appIdToClass == null) appIdToClass = BuildAppIdMap();
                appIdToClass.TryGetValue(appId, out name);
            }

            if (string.IsNullOrEmpty(name)) name = "COM " + appId;
            else if (Hints.TryGetValue(name, out var hint)) name += ": " + hint;
            cache[appId] = name;
            return name;
        }

        /// <summary>One-time scan of HKCR\CLSID for classes that declare an AppID (a second or two, background thread).</summary>
        static Dictionary<string, string> BuildAppIdMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var clsid = Registry.ClassesRoot.OpenSubKey("CLSID");
            if (clsid == null) return map;
            foreach (var sub in clsid.GetSubKeyNames())
            {
                try
                {
                    using var k = clsid.OpenSubKey(sub);
                    if (k?.GetValue("AppID") is string app && !map.ContainsKey(app) && k.GetValue("") is string desc && desc.Trim().Length > 0)
                        map[app] = desc.Trim();
                }
                catch { }
            }
            return map;
        }
    }
}
