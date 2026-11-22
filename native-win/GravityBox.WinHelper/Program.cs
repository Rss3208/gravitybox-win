using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GravityBox.Win32;

namespace GravityBox.WinHelper;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly Regex HostPathRegex = new(
        @"[A-Za-z]:(?:\\+|/+)(?:Users|Program Files(?: \(x86\))?|ProgramData|Windows|Temp)(?:\\+|/+)[^\s""'`<>|)+,;]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(Win32BridgeInfo.BridgeVersion);
            return 0;
        }

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string responseJson = ProcessLine(line);
            Console.Out.WriteLine(responseJson);
            Console.Out.Flush();
        }

        return 0;
    }

    public static string ProcessLine(string rawLine)
    {
        string? requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idProp))
            {
                requestId = idProp.ValueKind switch
                {
                    JsonValueKind.String => idProp.GetString(),
                    JsonValueKind.Number => idProp.GetRawText(),
                    _ => null,
                };
            }

            string method = root.TryGetProperty("method", out var mProp) && mProp.ValueKind == JsonValueKind.String
                ? (mProp.GetString() ?? string.Empty).Trim()
                : string.Empty;

            JsonElement paramsEl = root.TryGetProperty("params", out var pProp) && pProp.ValueKind == JsonValueKind.Object
                ? pProp
                : default;

            object resultPayload = DispatchMethod(method, paramsEl);
            return BuildSuccessJson(requestId, resultPayload);
        }
        catch (Exception ex)
        {
            return BuildErrorJson(requestId, SanitizeErrorMessage(ex.Message));
        }
    }

    private static object DispatchMethod(string method, JsonElement p)
    {
        switch (method)
        {
            case "ping":
                return new { ok = true, version = Win32BridgeInfo.BridgeVersion, pid = Environment.ProcessId };

            case "list_processes":
            {
                string? nameFilter = GetOptionalString(p, "nameFilter");
                var processes = ProcessInspector.ListProcesses(nameFilter)
                    .Select(proc => new
                    {
                        pid = proc.Pid,
                        ppid = proc.ParentPid,
                        parentPid = proc.ParentPid,
                        name = proc.Name,
                        executablePath = proc.ExecutablePath,
                        commandLine = proc.CommandLine,
                        cpuPercent = proc.CpuPercent,
                        creationTimeMs = proc.CreationTimeMs,
                    })
                    .ToArray();
                return new { ok = true, processes };
            }

            case "is_process_alive":
            {
                int pid = GetRequiredInt(p, "pid");
                long? creationTimeMs = GetOptionalLong(p, "creationTimeMs");
                var info = ProcessInspector.CheckProcessAlive(pid, creationTimeMs);
                return new { ok = true, alive = info.Alive, creationTimeMs = info.CreationTimeMs };
            }

            case "read_process_env":
            {
                int pid = GetRequiredInt(p, "pid");
                string varName = GetRequiredString(p, "name");
                string? val = ProcessInspector.ReadProcessEnvVar(pid, varName);
                return new { ok = true, value = val };
            }

            case "tcp_listeners":
            {
                int? port = GetOptionalInt(p, "port");
                int? pid = GetOptionalInt(p, "pid");
                var listeners = NetworkInspector.FindTcpListeners(port, pid)
                    .Select(l => new
                    {
                        pid = l.Pid,
                        localAddress = l.LocalAddress,
                        localPort = l.LocalPort,
                        executablePath = l.ExecutablePath,
                    })
                    .ToArray();
                return new { ok = true, listeners };
            }

            case "verify_authenticode":
            {
                string targetPath = GetRequiredString(p, "path");
                var info = AuthenticodeVerifier.Verify(targetPath);
                return new
                {
                    ok = true,
                    valid = info.Valid,
                    status = info.Status,
                    subject = info.Subject,
                    issuer = info.Issuer,
                    thumbprint = info.Thumbprint,
                    fileVersion = info.FileVersion,
                    productName = info.ProductName,
                    companyName = info.CompanyName,
                };
            }

            case "cred_read":
            {
                string target = GetRequiredString(p, "target");
                var record = CredentialStore.ReadGeneric(target);
                return new
                {
                    ok = true,
                    secret = record?.Secret,
                    userName = record?.UserName,
                };
            }

            case "cred_write":
            {
                string target = GetRequiredString(p, "target");
                string? userName = GetOptionalString(p, "userName");
                string secret = GetRequiredString(p, "secret");
                CredentialStore.WriteGeneric(target, userName, secret);
                return new { ok = true };
            }

            case "cred_delete":
            {
                string target = GetRequiredString(p, "target");
                bool existed = CredentialStore.DeleteGeneric(target);
                return new { ok = true, existed };
            }

            case "acl_harden":
            {
                string targetPath = GetRequiredString(p, "path");
                bool isDir = GetOptionalBool(p, "isDir") ?? Directory.Exists(targetPath);
                SecurityAclHelper.HardenPathAcl(targetPath, isDir);
                return new { ok = true };
            }

            case "acl_verify_owner":
            {
                string targetPath = GetRequiredString(p, "path");
                var verify = SecurityAclHelper.VerifyOwnerAndReparsePoint(targetPath);
                return new
                {
                    ok = true,
                    exists = verify.Exists,
                    isReparsePoint = verify.IsReparsePoint,
                    ownedByCurrentUser = verify.OwnedByCurrentUser,
                };
            }

            case "shortcut_scan":
            {
                string? officialExePath = GetOptionalString(p, "officialExePath");
                IEnumerable<(string, string)>? customRoots = null;
                if (p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("searchDirs", out var dirsProp) &&
                    dirsProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<(string, string)>();
                    foreach (var item in dirsProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? dir = item.GetString();
                            if (!string.IsNullOrWhiteSpace(dir)) list.Add((dir, "custom"));
                        }
                        else if (item.ValueKind == JsonValueKind.Object)
                        {
                            string? dir = GetOptionalString(item, "path");
                            string loc = GetOptionalString(item, "location") ?? "custom";
                            if (!string.IsNullOrWhiteSpace(dir)) list.Add((dir, loc));
                        }
                    }
                    customRoots = list;
                }

                var shortcuts = ShortcutManager.ScanShortcuts(officialExePath, customRoots)
                    .Select(s => new
                    {
                        path = s.Path,
                        location = s.Location,
                        name = s.Name,
                        targetPath = s.TargetPath,
                        arguments = s.Arguments,
                        workingDirectory = s.WorkingDirectory,
                        hasDebugPort = s.HasDebugPort,
                    })
                    .ToArray();
                return new { ok = true, shortcuts };
            }

            case "shortcut_inspect":
            {
                string lnkPath = GetRequiredString(p, "path");
                string loc = GetOptionalString(p, "location") ?? "custom";
                var s = ShortcutManager.InspectShortcut(lnkPath, loc)
                    ?? throw new FileNotFoundException("Shortcut file not found or invalid.");
                return new
                {
                    ok = true,
                    path = s.Path,
                    location = s.Location,
                    name = s.Name,
                    targetPath = s.TargetPath,
                    arguments = s.Arguments,
                    workingDirectory = s.WorkingDirectory,
                    hasDebugPort = s.HasDebugPort,
                };
            }

            case "shortcut_set_args":
            {
                string lnkPath = GetRequiredString(p, "path");
                string arguments = GetOptionalString(p, "arguments") ?? string.Empty;
                ShortcutManager.SetShortcutArguments(lnkPath, arguments);
                return new { ok = true };
            }

            case "shortcut_create":
            {
                string lnkPath = GetRequiredString(p, "path");
                string targetPath = GetRequiredString(p, "targetPath");
                string arguments = GetOptionalString(p, "arguments") ?? string.Empty;
                string workingDir = GetOptionalString(p, "workingDirectory") ?? string.Empty;
                ShortcutManager.CreateShortcut(lnkPath, targetPath, arguments, workingDir);
                return new { ok = true };
            }

            case "recycle_delete":
            {
                var paths = new List<string>();
                if (p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("paths", out var pathsProp) &&
                    pathsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in pathsProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? s = item.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) paths.Add(s);
                        }
                    }
                }
                bool ok = RecycleBinHelper.MoveToRecycleBin(paths);
                return new { ok };
            }

            case "ui_languages":
            {
                var locales = SystemLocaleHelper.GetPreferredUiLanguages();
                return new { ok = true, locales };
            }

            default:
                throw new InvalidOperationException($"Unsupported WinHelper method: {method}");
        }
    }

    private static string BuildSuccessJson(string? id, object payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (id != null)
            {
                writer.WriteString("id", id);
            }

            byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            using var doc = JsonDocument.Parse(payloadBytes);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildErrorJson(string? id, string error)
    {
        return JsonSerializer.Serialize(new
        {
            id,
            ok = false,
            error,
        }, JsonOptions);
    }

    private static string SanitizeErrorMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "WinHelper operation failed";
        }
        return HostPathRegex.Replace(raw.Trim(), "<path>");
    }

    private static string GetRequiredString(JsonElement p, string name)
    {
        string? val = GetOptionalString(p, name);
        if (val == null)
        {
            throw new ArgumentException($"Missing required string parameter: {name}");
        }
        return val;
    }

    private static string? GetOptionalString(JsonElement p, string name)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static int GetRequiredInt(JsonElement p, string name)
    {
        int? val = GetOptionalInt(p, name);
        if (!val.HasValue)
        {
            throw new ArgumentException($"Missing required integer parameter: {name}");
        }
        return val.Value;
    }

    private static int? GetOptionalInt(JsonElement p, string name)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt32(out int v))
        {
            return v;
        }
        return null;
    }

    private static long? GetOptionalLong(JsonElement p, string name)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt64(out long v))
        {
            return v;
        }
        return null;
    }

    private static bool? GetOptionalBool(JsonElement p, string name)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }
}
