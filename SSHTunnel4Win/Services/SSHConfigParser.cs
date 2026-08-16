using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SSHTunnel4Win.Models;

namespace SSHTunnel4Win.Services;

public static class SSHConfigParser
{
    private static string GetSshDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    private static string GetSshConfigPath() => Path.Combine(GetSshDir(), "config");
    private static string GetSshConfigDirPath() => Path.Combine(GetSshDir(), "config.d");

    /// Legacy parser for SSH config picker (simple host list)
    public static List<SSHConfigHost> Parse()
    {
        var hosts = new List<SSHConfigHost>();

        var mainConfig = GetSshConfigPath();
        if (File.Exists(mainConfig))
        {
            try
            {
                hosts.AddRange(ParseContent(File.ReadAllText(mainConfig)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse {mainConfig}: {ex.Message}");
            }
        }

        var configDir = GetSshConfigDirPath();
        if (Directory.Exists(configDir))
        {
            try
            {
                foreach (var file in Directory.GetFiles(configDir).OrderBy(f => Path.GetFileName(f)))
                {
                    if (Path.GetFileName(file).StartsWith(".")) continue;
                    try
                    {
                        hosts.AddRange(ParseContent(File.ReadAllText(file)));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to list {configDir}: {ex.Message}");
            }
        }

        return hosts;
    }

    /// Full-fidelity parser for SSH config editor
    public static List<SSHConfigEntry> ParseAll()
    {
        var entries = new List<SSHConfigEntry>();

        var mainConfig = GetSshConfigPath();
        if (File.Exists(mainConfig))
        {
            try
            {
                entries.AddRange(ParseFullContent(File.ReadAllText(mainConfig), mainConfig));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse {mainConfig}: {ex.Message}");
            }
        }

        var configDir = GetSshConfigDirPath();
        if (Directory.Exists(configDir))
        {
            try
            {
                foreach (var file in Directory.GetFiles(configDir).OrderBy(f => Path.GetFileName(f)))
                {
                    if (Path.GetFileName(file).StartsWith(".")) continue;
                    try
                    {
                        entries.AddRange(ParseFullContent(File.ReadAllText(file), file));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to list {configDir}: {ex.Message}");
            }
        }

        return entries;
    }

    public static List<string> ConfigFiles()
    {
        var files = new List<string>();
        try
        {
            var mainConfig = GetSshConfigPath();
            if (File.Exists(mainConfig)) files.Add(mainConfig);

            var configDir = GetSshConfigDirPath();
            if (Directory.Exists(configDir))
            {
                foreach (var f in Directory.GetFiles(configDir).OrderBy(f => Path.GetFileName(f)))
                {
                    if (!Path.GetFileName(f).StartsWith("."))
                        files.Add(f);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to enumerate SSH config files: {ex.Message}");
        }
        return files;
    }

    public static string Serialize(List<SSHConfigEntry> entries)
    {
        var lines = new List<string>();
        var first = true;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Host)) continue;
            if (!first) lines.Add("");
            first = false;
            if (!string.IsNullOrEmpty(entry.Comment))
                lines.Add(entry.Comment);
            var prefix = entry.Commented ? "# " : "";
            lines.Add($"{prefix}Host {entry.Host}");
            foreach (var directive in entry.Directives)
                lines.Add($"{prefix}    {directive.Key} {directive.Value}");
        }
        lines.Add("");
        return string.Join("\n", lines);
    }

    public static List<SSHConfigEntry> ParseFullContent(string content, string sourceFile)
    {
        var entries = new List<SSHConfigEntry>();
        string? currentHost = null;
        var currentCommented = false;
        var currentDirectives = new List<SSHConfigDirective>();
        var commentBuffer = new List<string>();

        void Flush()
        {
            if (currentHost != null && currentHost != "*")
            {
                entries.Add(new SSHConfigEntry
                {
                    Host = currentHost,
                    Directives = new List<SSHConfigDirective>(currentDirectives),
                    SourceFile = sourceFile,
                    Comment = string.Join("\n", commentBuffer),
                    Commented = currentCommented
                });
            }
            currentDirectives.Clear();
            commentBuffer.Clear();
            currentCommented = false;
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                if (currentHost == null) commentBuffer.Clear();
                continue;
            }

            if (trimmed.StartsWith("#"))
            {
                var uncommented = trimmed.TrimStart('#', ' ');
                var uParts = uncommented.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (uParts.Length == 2 && uParts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    currentHost = uParts[1];
                    currentCommented = true;
                    continue;
                }

                if (currentHost != null && currentCommented)
                {
                    var dParts = uncommented.Split(' ', 2, StringSplitOptions.TrimEntries);
                    if (dParts.Length == 2)
                    {
                        var key = dParts[0];
                        if (!key.Equals("Include", StringComparison.OrdinalIgnoreCase) &&
                            !key.Equals("Match", StringComparison.OrdinalIgnoreCase))
                        {
                            currentDirectives.Add(new SSHConfigDirective { Key = key, Value = dParts[1] });
                        }
                    }
                    continue;
                }

                if (currentHost == null) commentBuffer.Add(trimmed);
                continue;
            }

            if (currentCommented && currentHost != null)
                Flush();

            var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            var pKey = parts[0];
            var pValue = parts[1];

            if (pKey.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                currentHost = pValue;
            }
            else if (pKey.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
                     pKey.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            else if (currentHost != null)
            {
                currentDirectives.Add(new SSHConfigDirective { Key = pKey, Value = pValue });
            }
        }
        Flush();

        return entries;
    }

    private static List<SSHConfigHost> ParseContent(string content)
    {
        var hosts = new List<SSHConfigHost>();
        string? currentName = null;
        var hostname = "";
        ushort port = 22;
        var user = "";
        var identityFile = "";

        void Flush()
        {
            if (currentName != null && currentName != "*")
            {
                hosts.Add(new SSHConfigHost
                {
                    Name = currentName,
                    Hostname = string.IsNullOrEmpty(hostname) ? currentName : hostname,
                    Port = port,
                    User = user,
                    IdentityFile = identityFile
                });
            }
            hostname = "";
            port = 22;
            user = "";
            identityFile = "";
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

            var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            var key = parts[0].ToLowerInvariant();
            var value = parts[1];

            switch (key)
            {
                case "host":
                    Flush();
                    currentName = value;
                    break;
                case "hostname":
                    hostname = value;
                    break;
                case "port":
                    port = ushort.TryParse(value, out var p) ? p : (ushort)22;
                    break;
                case "user":
                    user = value;
                    break;
                case "identityfile":
                    identityFile = value.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    break;
            }
        }
        Flush();

        return hosts;
    }
}
