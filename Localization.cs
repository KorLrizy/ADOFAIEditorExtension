using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ADOFAIEditorExtension
{
    internal class Localization
    {
        Dictionary<string, Dictionary<SystemLanguage, string>> localization;

        public Localization(string jsonFile)
        {
            string jsonString = File.ReadAllText(jsonFile);

            var raw = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(jsonString);
            localization = new Dictionary<string, Dictionary<SystemLanguage, string>>();

            foreach (var outer in raw)
            {
                var innerDict = new Dictionary<SystemLanguage, string>();

                foreach (var inner in outer.Value)
                {
                    if (Enum.TryParse(inner.Key, out SystemLanguage lang))
                    {
                        innerDict[lang] = inner.Value;
                    }
                    else
                    {
                        Main.Logger.Log($"警告: 未识别语言 {inner.Key}");
                    }
                }

                localization[outer.Key] = innerDict;
            }
        }

        public bool Get(string key, out string value, Dictionary<string, object> parameters = null)
        {
            value = null;

            if (localization.TryGetValue(key, out var languageDict))
            {
                SystemLanguage currentLanguage = RDString.language;
                if (!languageDict.TryGetValue(currentLanguage, out value))
                {
                    languageDict.TryGetValue(SystemLanguage.English, out value);
                }
            }
            return value != null;
        }

        public string GetValue(string key)
        {
            if (localization.TryGetValue(key, out var languageDict))
            {
                SystemLanguage currentLanguage = RDString.language;
                if (languageDict.TryGetValue(currentLanguage, out var result))
                {
                    return result;
                }
                if (languageDict.TryGetValue(SystemLanguage.English, out result))
                {
                    return result;
                }
            }
            return null;
        }

        /// <summary>
        /// 用已有模板生成一条“派生键”（三种语言都生成），用于带行号等无法预先写进 JSON 的文本，
        /// 例如自定义分组的“分组 N 名称 / 匹配 tag / 删除分组 N”。
        /// 派生键同时被标签（customLocalizationKey）与 Export 按钮文本用到，
        /// 这样即使某条路径拿不到 customLabel，也不会显示出未替换的 {0}。
        /// </summary>
        public void AddDerived(string key, string templateKey, params object[] args)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(templateKey))
                return;
            if (!localization.TryGetValue(templateKey, out var template) || template == null)
                return;

            var values = new Dictionary<SystemLanguage, string>();
            foreach (var pair in template)
            {
                string text = pair.Value;
                if (text == null)
                    continue;
                string formatted;
                try
                {
                    formatted = string.Format(text, args);
                }
                catch (FormatException)
                {
                    formatted = text;
                }
                for (int i = 0; i < args.Length; i++)
                    formatted = formatted.Replace("{" + i + "}", args[i]?.ToString() ?? "");
                values[pair.Key] = formatted;
            }
            localization[key] = values;
        }
    }
}
