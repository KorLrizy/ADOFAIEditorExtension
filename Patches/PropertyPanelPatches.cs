using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Utils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Patches
{
    /// <summary>
    /// 游戏原生不支持 Export(按钮)型控件，这里替换 PropertiesPanel.RenderControl，
    /// 为 Export 型属性实例化按钮控件（照原版实现 + MultiTrackHelper 的同名补丁）。
    /// </summary>
    [HarmonyPatch(typeof(PropertiesPanel), "RenderControl")]
    internal static class RenderControlPatch
    {
        /// <summary>PropertiesPanel.UpdateEnabledButton(Property, bool) 是 private，缓存一次，免得每次点击都走 AccessTools。</summary>
        private static readonly MethodInfo UpdateEnabledButtonMethod = AccessTools.Method(typeof(PropertiesPanel), "UpdateEnabledButton");

        internal static bool Prefix(PropertiesPanel __instance, string propertyKey, ADOFAI.PropertyInfo propertyInfo)
        {
            // 分组归属键（invisible 属性）不建控件 ⇒ SetProperties 的循环里 properties 表没有它 ⇒ 面板不会多出行
            if (Features.DecoGrouping.DecoGroupState.IsMembershipKey(propertyKey))
                return false;
            if (propertyInfo.type != PropertyType.Export)
                return true;

            LevelEventInfo levelEventInfo = propertyInfo.levelEventInfo;
            GameObject original = ADOBase.gc.prefab_controlExport;
            GameObject gameObject = UnityEngine.Object.Instantiate<GameObject>(ADOBase.gc.prefab_property);
            gameObject.transform.SetParent(__instance.content, false);
            ADOFAI.Property property = gameObject.GetComponent<ADOFAI.Property>();
            property.gameObject.name = propertyKey;
            property.key = propertyKey;
            property.info = propertyInfo;
            GameObject gameObject2 = UnityEngine.Object.Instantiate<GameObject>(original);
            gameObject2.GetComponent<RectTransform>().SetParent(property.controlContainer, false);
            property.control = gameObject2.GetComponent<PropertyControl>();
            property.control.propertyInfo = propertyInfo;
            property.control.propertiesPanel = __instance;
            property.control.propertyTransform = property.GetComponent<RectTransform>();

            // 原版 r148 RenderControl 只在帮助按钮之后调用一次 control.Setup(true)（IL_0B52；IL_0BCD 是 randomControl.Setup），
            // 这里保持同样的顺序与次数（之前多调的一次会让 Setup 后置补丁重复挂监听）
            string key2 = "editor." + property.key + ".help";
            string helpString = RDString.GetWithCheck(key2, out bool flag4, null);
            if (flag4)
            {
                UnityEngine.UI.Button helpButton = property.helpButton;
                helpButton.transform.parent.gameObject.SetActive(true);
                string buttonText = RDString.GetWithCheck("editor." + property.key + ".help.buttonText", out flag4, null);
                string buttonURL = RDString.GetWithCheck("editor." + property.key + ".help.buttonURL", out flag4, null);
                helpButton.onClick.AddListener(delegate ()
                {
                    ADOBase.editor.ShowPropertyHelp(true, helpButton.transform, helpString, buttonText, buttonURL);
                });
            }

            property.control.Setup(true);
            if (property.info.hasRandomValue && levelEventInfo != null)
            {
                string randValueKey = property.info.randValueKey;
                property.control.randomControl.propertyInfo = levelEventInfo.propertiesInfo[randValueKey];
                property.control.randomControl.propertiesPanel = __instance;
                property.control.randomControl.Setup(true);
                UnityEngine.UI.Button randomButton = property.randomButton;
                randomButton.gameObject.SetActive(true);
                randomButton.onClick.AddListener(delegate ()
                {
                    string randModeKey = property.info.randModeKey;
                    int num = ((int)__instance.inspectorPanel.selectedEvent[randModeKey] + 1) % 3;
                    __instance.inspectorPanel.selectedEvent[randModeKey] = (RandomMode)num;
                    property.control.SetRandomLayout();
                });
            }

            property.enabledButton.onClick.AddListener(delegate ()
            {
                bool isFake = __instance.inspectorPanel.selectedEvent.isFake;
                using (new SaveStateScope(ADOBase.editor, false, true, false))
                {
                    bool flag5 = __instance.inspectorPanel.selectedEvent.disabled.TryGetValue(propertyKey, out bool flag6) && !flag6;
                    __instance.inspectorPanel.selectedEvent.disabled[propertyKey] = flag5;
                    property.offText.SetActive(flag5);
                    property.enabledCheckmark.SetActive(!flag5);
                    property.control.gameObject.SetActive(!flag5);
                    property.control.OnValueChange();
                    UpdateEnabledButtonMethod?.Invoke(__instance, new object[] { property, flag5 });
                    if (isFake)
                    {
                        property.enabledCheckmark.transform.parent.gameObject.SetActive(false);
                        property.enabledButton.gameObject.SetActive(false);
                        __instance.inspectorPanel.selectedEvent.ApplyPropertiesToRealEvents();
                    }
                    property.control.ApplyTileChanges();
                }
            });

            if (property.info.canBeDisabled)
            {
                UnityEngine.UI.Button component4 = property.enabledButton.GetComponent<UnityEngine.UI.Button>();
                ColorBlock colors = component4.colors;
                colors.selectedColor = InspectorPanel.selectionColor;
                component4.colors = colors;
                PropertiesPanel.PropertySelectable item = new PropertiesPanel.PropertySelectable(component4, property.control, property, true);
                __instance.propertySelectables.Add(item);
            }

            if (property.control.selectables != null)
            {
                foreach (UnityEngine.UI.Selectable sel in property.control.selectables)
                {
                    PropertiesPanel.PropertySelectable item2 = new PropertiesPanel.PropertySelectable(sel, property.control, property, false);
                    __instance.propertySelectables.Add(item2);
                }
            }

            __instance.properties.Add(propertyInfo.name, property);
            return false;
        }
    }

    /// <summary>Export 控件的 Setup：把 UnityAction 绑到按钮上并设置按钮文本。</summary>
    [HarmonyPatch]
    internal static class PropertyControl_ExportSetupPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ADOFAI.LevelEditor.Controls.PropertyControl), "Setup");
        }

        internal static void Postfix(object __instance)
        {
            // 这是挂在所有 PropertyControl.Setup 上的后置补丁：模组侧的异常一律吞掉记日志，不能打断原版面板构建
            try
            {
                Apply(__instance);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("Export 控件 Setup 后置补丁异常: " + e);
            }
        }

        private static void Apply(object __instance)
        {
            ADOFAI.PropertyInfo info = __instance.Get<ADOFAI.PropertyInfo>("propertyInfo");
            if (info == null || !(info.value_default is UnityAction action))
                return;

            UnityEngine.UI.Button button = __instance.Get<UnityEngine.UI.Button>("exportButton");
            if (button == null)
                return;

            button.onClick.RemoveAllListeners();
            // 包一层：模组被禁用后（面板/按钮可能还残留在场景里）点击直接 no-op；动作本身的异常也记日志
            button.onClick.AddListener(() =>
            {
                if (!Main.IsEnabled)
                    return;
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Main.Logger?.Log("Export 按钮动作异常 (" + info.name + "): " + e);
                }
            });
            button.name = info.name;

            object text = __instance.Get("buttonText");
            if (text == null)
                return;

            if (!string.IsNullOrEmpty(info.customLabel))
            {
                // 分组行的按钮文本带行号，构建时已经算好放在 customLabel 里
                text.Set("text", info.customLabel);
            }
            else if (info.customLocalizationKey == null)
            {
                string str = "editor." + info.levelEventInfo.name + "." + info.name;
                text.Set("text", RDString.GetWithCheck(str, out bool flag, null));
                if (!flag)
                    text.Set("text", RDString.GetWithCheck("editor." + info.name, out _, null));
            }
            else
            {
                text.Set("text", info.customLocalizationKey == "" ? "" : RDString.Get(info.customLocalizationKey, null));
            }
        }
    }

    /// <summary>回车不应触发面板上的“添加/删除分组”按钮（装饰 deleteGroupN 与事件 eventDeleteGroupN 两套）。</summary>
    [HarmonyPatch(typeof(UnityEngine.UI.Button), "OnSubmit")]
    internal static class ButtonOnSubmitPatch
    {
        internal static bool Prefix(UnityEngine.UI.Button __instance)
        {
            // 全局补丁（所有 Button 回车都会经过），只做几次序数字符串比较
            string name = __instance != null ? __instance.name : null;
            if (name == null)
                return true;
            if (name == "addGroup"
                || name.StartsWith("deleteGroup", StringComparison.Ordinal)
                || name.StartsWith("eventDeleteGroup", StringComparison.Ordinal))
                return false;
            return true;
        }
    }

    /// <summary>Export 型控件没有标签文本。</summary>
    [HarmonyPatch(typeof(ADOFAI.Property), "info", MethodType.Setter)]
    internal static class Propertyset_infoPatch
    {
        internal static void Postfix(ADOFAI.Property __instance)
        {
            if (__instance.info != null && __instance.info.type == PropertyType.Export && __instance.label != null)
                __instance.label.text = "";
        }
    }

    /// <summary>Bool / Enum 控件值变化后通知模组刷新（装饰分组需要立刻重建列表）。</summary>
    [HarmonyPatch]
    internal static class ValueChangePatch1
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo setValue = AccessTools.Method(typeof(ADOFAI.LevelEditor.Controls.PropertyControl_Bool), "SetValue");
            if (setValue != null)
                yield return setValue;
            MethodInfo selectVar = AccessTools.Method(typeof(ADOFAI.LevelEditor.Controls.PropertyControl_Toggle), "SelectVar");
            if (selectVar != null)
                yield return selectVar;
        }

        internal static void Postfix(object __instance, PropertiesPanel ___propertiesPanel, ADOFAI.PropertyInfo ___propertyInfo)
        {
            if (Main.Aee == null || ___propertiesPanel == null || ___propertyInfo == null || ___propertyInfo.levelEventInfo == null)
                return;
            if ((int)___propertyInfo.levelEventInfo.type != Main.Aee.type)
                return;
            try
            {
                // 非泛型 Get + is 判断：成员缺失时拿到 null 也不会在这里抛异常
                if (__instance.GetType() == typeof(ADOFAI.LevelEditor.Controls.PropertyControl_Toggle)
                    && __instance.Get("settingText") is bool settingText && settingText)
                    return;

                LevelEvent levelEvent = ___propertiesPanel.inspectorPanel != null ? ___propertiesPanel.inspectorPanel.selectedEvent : null;
                if (levelEvent == null)
                    return;
                Main.OnSettingChanged(levelEvent, ___propertyInfo.name, null, levelEvent[___propertyInfo.name]);
            }
            catch (Exception e)
            {
                // 模组侧的刷新失败不能冒进原版 SetValue / SelectVar
                Main.Logger?.Log("设置值变化回调异常: " + e);
            }
        }
    }

    /// <summary>文本输入控件（分组名称 / 匹配 tag）编辑完成后通知模组刷新。</summary>
    [HarmonyPatch]
    internal static class ValueChangePatch2
    {
        private static readonly Dictionary<TMP_InputField, UnityAction<string>> hooked = new Dictionary<TMP_InputField, UnityAction<string>>();

        internal static void Reset() => hooked.Clear();

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo longText = AccessTools.Method(typeof(ADOFAI.LevelEditor.Controls.PropertyControl_LongText), "Setup");
            if (longText != null)
                yield return longText;
            MethodInfo text = AccessTools.Method(typeof(ADOFAI.LevelEditor.Controls.PropertyControl_Text), "Setup");
            if (text != null)
                yield return text;
        }

        internal static void Postfix(object __instance, ADOFAI.PropertyInfo ___propertyInfo)
        {
            if (Main.Aee == null || ___propertyInfo == null || ___propertyInfo.levelEventInfo == null)
                return;
            if ((int)___propertyInfo.levelEventInfo.type != Main.Aee.type)
                return;

            try
            {
                TMP_InputField inputField = __instance.Get<TMP_InputField>("inputField");
                if (inputField == null)
                    return;

                if (hooked.TryGetValue(inputField, out UnityAction<string> previous) && previous != null)
                    inputField.onEndEdit.RemoveListener(previous);

                string key = ___propertyInfo.name;
                UnityAction<string> listener = value =>
                {
                    // 模组被禁用后输入框可能还残留在场景里：不再回调
                    if (!Main.IsEnabled)
                        return;
                    // onEndEdit 由原版输入框触发，同样不能让模组异常冒出去
                    try
                    {
                        Main.OnSettingChanged(Main.GetSettingsEvent(), key, null, value);
                    }
                    catch (Exception e)
                    {
                        Main.Logger?.Log("文本设置变化回调异常: " + e);
                    }
                };
                inputField.onEndEdit.AddListener(listener);
                hooked[inputField] = listener;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("文本控件 Setup 后置补丁异常: " + e);
            }
        }
    }

    /// <summary>面板属性刷新后重新套用控件可见性列表（自定义分组行的显示/隐藏）。</summary>
    [HarmonyPatch(typeof(PropertiesPanel), "SetProperties")]
    internal static class ActiveChildPatch
    {
        internal static void Postfix(LevelEvent levelEvent, bool checkIfEnabled = true)
        {
            if (levelEvent == null || (int)levelEvent.eventType != Main.ModEventType)
                return;
            try
            {
                Main.activeChilden();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("SetProperties 后置补丁异常: " + e);
            }
        }
    }
}
