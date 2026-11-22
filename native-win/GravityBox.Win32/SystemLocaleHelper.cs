using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GravityBox.Win32;

public static class SystemLocaleHelper
{
    private const uint MUI_LANGUAGE_NAME = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserPreferredUILanguages(
        uint dwFlags,
        out uint pulNumLanguages,
        char[]? pwszLanguagesBuffer,
        ref uint pcchLanguagesBuffer);

    /// <summary>
    /// 调用 Win32 GetUserPreferredUILanguages(MUI_LANGUAGE_NAME) 获取当前用户 BCP-47 首选语言列表（如 ["zh-CN", "en-US"]）。
    /// </summary>
    public static IReadOnlyList<string> GetPreferredUiLanguages()
    {
        var list = new List<string>();
        try
        {
            uint cchBuffer = 0;
            if (GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, null, ref cchBuffer) && cchBuffer > 0)
            {
                var buffer = new char[cchBuffer];
                if (GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, buffer, ref cchBuffer))
                {
                    int start = 0;
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        if (buffer[i] == '\0')
                        {
                            int len = i - start;
                            if (len == 0)
                            {
                                break;
                            }
                            string lang = new string(buffer, start, len).Trim();
                            if (!string.IsNullOrEmpty(lang))
                            {
                                list.Add(lang);
                            }
                            start = i + 1;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback below
        }

        if (list.Count == 0)
        {
            string fallback = CultureInfo.CurrentUICulture.Name;
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                list.Add(fallback);
            }
        }

        return list;
    }
}
