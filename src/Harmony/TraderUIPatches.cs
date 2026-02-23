using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HaldorCompanions
{
    public static class TraderUIPatches
    {
        // ── Cached reflection ──
        private static Type _traderUIType;
        private static FieldInfo _mainPanelField;
        private static FieldInfo _buttonTemplateField;
        private static FieldInfo _activeTabField;
        private static FieldInfo _panelWidthField;
        private static FieldInfo _tabBtnHeightField;
        private static FieldInfo _leftColumnField;
        private static FieldInfo _middleColumnField;
        private static FieldInfo _rightColumnField;
        private static FieldInfo _bankContentPanelField;
        private static FieldInfo _searchFilterField;
        private static FieldInfo _searchInputField;
        private static FieldInfo _activeCategoryFilterField;
        private static FieldInfo _joyCategoryFocusIndexField;
        private static FieldInfo _colTopInsetField;
        private static FieldInfo _bottomPadField;
        private static FieldInfo _valheimFontField;
        private static FieldInfo _isVisibleField;
        private static FieldInfo _craftBtnHeightField;

        private static MethodInfo _refreshTabHighlightsMethod;
        private static MethodInfo _updateCategoryFilterVisualsMethod;
        private static MethodInfo _switchTabMethod;

        // ── Our state ──
        private static GameObject _tabCompanions;
        private static CompanionPanel _companionPanel;
        private static bool _reflectionCached;
        private static int _preUpdateTab = -1;

        /// <summary>Dynamic tab index — determined at runtime based on existing tabs.</summary>
        internal static int CompanionTabIndex { get; private set; } = -1;

        private static readonly Color GoldColor = new Color(0.83f, 0.64f, 0.31f, 1f);

        private static void CacheReflection()
        {
            if (_reflectionCached) return;

            _traderUIType = Type.GetType("HaldorOverhaul.TraderUI, HaldorOverhaul");
            if (_traderUIType == null)
            {
                HaldorCompanions.Log.LogError("[TraderUIPatches] Could not find TraderUI type!");
                return;
            }

            var bf = BindingFlags.Instance | BindingFlags.NonPublic;
            _mainPanelField                = _traderUIType.GetField("_mainPanel", bf);
            _buttonTemplateField           = _traderUIType.GetField("_buttonTemplate", bf);
            _activeTabField                = _traderUIType.GetField("_activeTab", bf);
            _panelWidthField               = _traderUIType.GetField("_panelWidth", bf);
            _tabBtnHeightField             = _traderUIType.GetField("_tabBtnHeight", bf);
            _leftColumnField               = _traderUIType.GetField("_leftColumn", bf);
            _middleColumnField             = _traderUIType.GetField("_middleColumn", bf);
            _rightColumnField              = _traderUIType.GetField("_rightColumn", bf);
            _bankContentPanelField         = _traderUIType.GetField("_bankContentPanel", bf);
            _searchFilterField             = _traderUIType.GetField("_searchFilter", bf);
            _searchInputField              = _traderUIType.GetField("_searchInput", bf);
            _activeCategoryFilterField     = _traderUIType.GetField("_activeCategoryFilter", bf);
            _joyCategoryFocusIndexField    = _traderUIType.GetField("_joyCategoryFocusIndex", bf);
            _colTopInsetField              = _traderUIType.GetField("_colTopInset", bf);
            _bottomPadField                = _traderUIType.GetField("_bottomPad", bf);
            _valheimFontField              = _traderUIType.GetField("_valheimFont", bf);
            _isVisibleField                = _traderUIType.GetField("_isVisible", bf);
            _craftBtnHeightField           = _traderUIType.GetField("_craftBtnHeight", bf);

            _refreshTabHighlightsMethod        = _traderUIType.GetMethod("RefreshTabHighlights", bf);
            _updateCategoryFilterVisualsMethod  = _traderUIType.GetMethod("UpdateCategoryFilterVisuals", bf);
            _switchTabMethod                   = _traderUIType.GetMethod("SwitchTab", bf);

            if (_mainPanelField     == null) HaldorCompanions.Log.LogWarning("[TraderUIPatches] _mainPanel field not found.");
            if (_buttonTemplateField == null) HaldorCompanions.Log.LogWarning("[TraderUIPatches] _buttonTemplate field not found.");
            if (_activeTabField     == null) HaldorCompanions.Log.LogWarning("[TraderUIPatches] _activeTab field not found.");
            if (_panelWidthField    == null) HaldorCompanions.Log.LogWarning("[TraderUIPatches] _panelWidth field not found.");
            if (_switchTabMethod    == null) HaldorCompanions.Log.LogWarning("[TraderUIPatches] SwitchTab method not found.");

            _reflectionCached = true;
            HaldorCompanions.Log.LogInfo("[TraderUIPatches] Reflection cached successfully.");
        }

        // ══════════════════════════════════════════
        //  PATCH A: After BuildUI — inject companions tab
        // ══════════════════════════════════════════

        [HarmonyPatch]
        private static class BuildUI_Patch
        {
            static MethodBase TargetMethod()
            {
                var t = Type.GetType("HaldorOverhaul.TraderUI, HaldorOverhaul");
                return t?.GetMethod("BuildUI", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            static void Postfix(object __instance)
            {
                try
                {
                    CacheReflection();
                    InjectCompanionTab(__instance);
                }
                catch (Exception ex)
                {
                    HaldorCompanions.Log.LogError($"[TraderUIPatches] BuildUI postfix error: {ex}");
                }
            }
        }

        private static void InjectCompanionTab(object traderUI)
        {
            if (_mainPanelField     == null || _buttonTemplateField == null || _activeTabField == null ||
                _panelWidthField    == null || _tabBtnHeightField   == null || _colTopInsetField == null ||
                _bottomPadField     == null || _valheimFontField    == null)
            {
                HaldorCompanions.Log.LogError("[TraderUIPatches] Cannot inject companion tab — critical reflection fields are missing.");
                return;
            }

            var mainPanel      = (GameObject)_mainPanelField.GetValue(traderUI);
            var buttonTemplate = (GameObject)_buttonTemplateField.GetValue(traderUI);
            float panelWidth   = (float)_panelWidthField.GetValue(traderUI);
            float tabBtnHeight = (float)_tabBtnHeightField.GetValue(traderUI);
            float colTopInset  = (float)_colTopInsetField.GetValue(traderUI);
            float bottomPad    = (float)_bottomPadField.GetValue(traderUI);
            var font           = _valheimFontField.GetValue(traderUI) as TMP_FontAsset;

            if (mainPanel == null || buttonTemplate == null) return;

            // Find all existing tab buttons under mainPanel
            var existingTabs = new List<GameObject>();
            for (int i = 0; i < mainPanel.transform.childCount; i++)
            {
                var child = mainPanel.transform.GetChild(i).gameObject;
                if (child.name.StartsWith("Tab_") && child.GetComponent<Button>() != null)
                    existingTabs.Add(child);
            }

            int totalTabs = existingTabs.Count + 1; // +1 for companions
            CompanionTabIndex = existingTabs.Count;  // 0-based index for our tab

            // Calculate new tab widths for totalTabs equal tabs
            const float outerPad = 6f;
            const float colGap = 4f;
            const float tabTopGap = 6f;
            float usable = panelWidth - outerPad * 2f - colGap * (totalTabs - 1);
            float tabW = usable / totalTabs;

            // Compute center positions for all tabs
            float[] centers = new float[totalTabs];
            for (int i = 0; i < totalTabs; i++)
                centers[i] = outerPad + tabW / 2f + i * (tabW + colGap);

            // Reposition and resize all existing tabs
            for (int i = 0; i < existingTabs.Count; i++)
                ResizeTab(existingTabs[i], centers[i], tabW);

            // Create companions tab button
            _tabCompanions = UnityEngine.Object.Instantiate(buttonTemplate, mainPanel.transform);
            _tabCompanions.name = "Tab_Companions";
            _tabCompanions.SetActive(true);

            var btn = _tabCompanions.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => SwitchToCompanions(traderUI));
                btn.navigation = new Navigation { mode = Navigation.Mode.None };
            }

            var txt = _tabCompanions.GetComponentInChildren<TMP_Text>(true);
            if (txt != null)
            {
                txt.text = "Companions";
                txt.gameObject.SetActive(true);
            }

            // Strip hint children from button (keep only text label)
            for (int i = _tabCompanions.transform.childCount - 1; i >= 0; i--)
            {
                var child = _tabCompanions.transform.GetChild(i);
                if (txt != null && (child.gameObject == txt.gameObject || txt.transform.IsChildOf(child)))
                    continue;
                UnityEngine.Object.Destroy(child.gameObject);
            }

            var tabRT = _tabCompanions.GetComponent<RectTransform>();
            tabRT.anchorMin = new Vector2(0f, 1f);
            tabRT.anchorMax = new Vector2(0f, 1f);
            tabRT.pivot = new Vector2(0.5f, 1f);
            tabRT.sizeDelta = new Vector2(tabW, tabBtnHeight);
            tabRT.anchoredPosition = new Vector2(centers[CompanionTabIndex], -tabTopGap);

            // Get button height from TraderUI
            float craftBtnHeight = 30f;
            if (_craftBtnHeightField != null)
                craftBtnHeight = (float)_craftBtnHeightField.GetValue(traderUI);

            // Build companion panel
            _companionPanel = new CompanionPanel();
            _companionPanel.Build(mainPanel.transform, colTopInset, bottomPad, font, buttonTemplate, craftBtnHeight);

            HaldorCompanions.Log.LogInfo($"[TraderUIPatches] Companion tab injected at index {CompanionTabIndex} ({totalTabs} total tabs).");
        }

        private static void ResizeTab(GameObject tab, float centerX, float width)
        {
            if (tab == null) return;
            var rt = tab.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
            rt.anchoredPosition = new Vector2(centerX, rt.anchoredPosition.y);
        }

        // ══════════════════════════════════════════
        //  PATCH B: SwitchTab — handle companion tab
        // ══════════════════════════════════════════

        [HarmonyPatch]
        private static class SwitchTab_Patch
        {
            static MethodBase TargetMethod()
            {
                var t = Type.GetType("HaldorOverhaul.TraderUI, HaldorOverhaul");
                return t?.GetMethod("SwitchTab", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            static bool Prefix(object __instance, int newTab)
            {
                if (CompanionTabIndex < 0) return true;
                CacheReflection();
                if (_activeTabField == null) return true;

                if (newTab == CompanionTabIndex)
                {
                    int currentTab = (int)_activeTabField.GetValue(__instance);
                    if (currentTab == CompanionTabIndex) return false; // already on companions

                    _activeTabField.SetValue(__instance, CompanionTabIndex);

                    // Clear search/filters
                    _searchFilterField?.SetValue(__instance, "");
                    var searchInput = _searchInputField?.GetValue(__instance) as TMP_InputField;
                    if (searchInput != null) searchInput.text = "";
                    _activeCategoryFilterField?.SetValue(__instance, null);
                    _joyCategoryFocusIndexField?.SetValue(__instance, -1);
                    _updateCategoryFilterVisualsMethod?.Invoke(__instance, null);

                    // Update tab highlights
                    RefreshAllTabHighlights(__instance);

                    // Hide columns, show companion panel
                    HideColumns(__instance);
                    if (_bankContentPanelField?.GetValue(__instance) is GameObject bankPanel)
                        bankPanel.SetActive(false);

                    if (_companionPanel?.Root != null)
                    {
                        _companionPanel.Root.SetActive(true);
                        _companionPanel.Refresh();
                    }

                    // Clear event system selection
                    if (UnityEngine.EventSystems.EventSystem.current != null)
                        UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);

                    return false; // skip original
                }

                // Not our tab — hide companion panel
                if (_companionPanel?.Root != null)
                    _companionPanel.Root.SetActive(false);

                return true; // run original
            }
        }

        // ══════════════════════════════════════════
        //  PATCH C: RefreshTabHighlights — include companion tab
        // ══════════════════════════════════════════

        [HarmonyPatch]
        private static class RefreshTabHighlights_Patch
        {
            static MethodBase TargetMethod()
            {
                var t = Type.GetType("HaldorOverhaul.TraderUI, HaldorOverhaul");
                return t?.GetMethod("RefreshTabHighlights", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            static void Postfix(object __instance)
            {
                CacheReflection();
                if (_tabCompanions == null || _activeTabField == null) return;

                int activeTab = (int)_activeTabField.GetValue(__instance);
                var btn = _tabCompanions.GetComponent<Button>();
                if (btn != null)
                {
                    btn.interactable = true;
                    btn.transition = Selectable.Transition.None;
                }
                var img = _tabCompanions.GetComponent<Image>();
                if (img != null)
                    img.color = (activeTab == CompanionTabIndex) ? GoldColor : new Color(0.45f, 0.45f, 0.45f, 1f);
            }
        }

        // ══════════════════════════════════════════
        //  PATCH D: RefreshTabPanels — hide companion panel
        // ══════════════════════════════════════════

        [HarmonyPatch]
        private static class RefreshTabPanels_Patch
        {
            static MethodBase TargetMethod()
            {
                var t = Type.GetType("HaldorOverhaul.TraderUI, HaldorOverhaul");
                return t?.GetMethod("RefreshTabPanels", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            static void Postfix(object __instance)
            {
                if (_companionPanel?.Root != null)
                    _companionPanel.Root.SetActive(false);
            }
        }

        // ══════════════════════════════════════════
        //  PATCH E: Update — extend Q/E and gamepad range
        // ══════════════════════════════════════════

        [HarmonyPatch]
        private static class Update_Patch
        {
            static MethodBase TargetMethod()
            {
                var t = Type.GetType("HaldorOverhaul.TraderUI, HaldorOverhaul");
                return t?.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            static void Prefix(object __instance)
            {
                CacheReflection();
                if (_activeTabField == null) { _preUpdateTab = -1; return; }
                _preUpdateTab = (int)_activeTabField.GetValue(__instance);
            }

            static void Postfix(object __instance)
            {
                if (CompanionTabIndex < 0) return;
                if (_isVisibleField == null || _activeTabField == null) return;

                bool isVisible = (bool)_isVisibleField.GetValue(__instance);
                if (!isVisible) return;

                int activeTab = (int)_activeTabField.GetValue(__instance);

                var searchInput = _searchInputField?.GetValue(__instance) as TMP_InputField;
                bool searchFocused = searchInput != null && searchInput.isFocused;
                if (searchFocused) return;

                // ── Companion tab protection ──
                // If we were on the companion tab and something moved us off
                // without Q/Left, undo the change.
                if (_preUpdateTab == CompanionTabIndex && activeTab != CompanionTabIndex)
                {
                    bool leftPressed = Input.GetKeyDown(KeyCode.Q) || ZInput.GetButtonDown("JoyTabLeft");
                    if (!leftPressed)
                    {
                        SwitchToCompanions(__instance);
                        activeTab = CompanionTabIndex;
                    }
                }
                // ── Extend right into companion tab ──
                // If we're on the tab just before companions and right is pressed,
                // navigate to companion tab.
                else if (_preUpdateTab == CompanionTabIndex - 1)
                {
                    // After all other postfixes run, the active tab may have been
                    // restored (e.g. HaldorBounties restores tab 3). Re-read it.
                    activeTab = (int)_activeTabField.GetValue(__instance);

                    if (activeTab == CompanionTabIndex - 1)
                    {
                        if (Input.GetKeyDown(KeyCode.E) || ZInput.GetButtonDown("JoyTabRight"))
                        {
                            SwitchToCompanions(__instance);
                            activeTab = CompanionTabIndex;
                        }
                    }
                }

                // Per-frame companion panel update
                activeTab = (int)_activeTabField.GetValue(__instance);
                if (activeTab == CompanionTabIndex)
                    _companionPanel?.UpdatePerFrame();
            }
        }

        // ══════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════

        private static void SwitchToCompanions(object traderUI)
        {
            _switchTabMethod?.Invoke(traderUI, new object[] { CompanionTabIndex });
        }

        private static void RefreshAllTabHighlights(object traderUI)
        {
            _refreshTabHighlightsMethod?.Invoke(traderUI, null);
        }

        private static void HideColumns(object traderUI)
        {
            var left = _leftColumnField?.GetValue(traderUI) as RectTransform;
            var mid = _middleColumnField?.GetValue(traderUI) as RectTransform;
            var right = _rightColumnField?.GetValue(traderUI) as RectTransform;

            if (left != null) left.gameObject.SetActive(false);
            if (mid != null) mid.gameObject.SetActive(false);
            if (right != null) right.gameObject.SetActive(false);
        }
    }
}
