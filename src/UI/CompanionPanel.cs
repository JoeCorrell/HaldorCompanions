using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Companions
{
    public class CompanionPanel
    {
        private const float UiScale = 1.0f;

        public GameObject Root { get; private set; }

        // ── Preview camera ────────────────────────────────────────────────────
        private static readonly Vector3 PreviewPos = new Vector3(10030f, 5000f, 10000f);
        private RenderTexture  _rt;
        private Camera         _cam;
        private GameObject     _camGO;
        private GameObject     _clone;
        private GameObject     _lightRig;
        private RawImage       _previewImg;
        private static readonly Vector3 PreviewCameraOffsetDir =
            new Vector3(0f, 0.18f, 1f).normalized;
        private Vector3 _previewCenter = PreviewPos + Vector3.up * 0.95f;
        private float   _previewDistance = 3.6f;
        private float   _previewBoundsTimer;
        private const float PreviewBoundsRefreshInterval = 0.5f;

        // Ambient override state
        private Color                             _savedAmbient;
        private float                             _savedAmbientIntensity;
        private UnityEngine.Rendering.AmbientMode _savedAmbientMode;

        // ── Customisation state ───────────────────────────────────────────────
        private CompanionAppearance _current;
        private List<string> _hairs  = new List<string>();
        private List<string> _beards = new List<string>();
        private int  _hairIndex;
        private int  _beardIndex;
        private bool _listsLoaded;

        // ── UI element references ─────────────────────────────────────────────
        private TextMeshProUGUI _hairNameText;
        private TextMeshProUGUI _beardNameText;
        private GameObject      _beardGroup;
        private TextMeshProUGUI _coinText;
        private Button          _buyButton;
        private TMP_Text        _buyButtonLabel;
        private Button          _maleBtn;
        private Button          _femaleBtn;
        private Slider          _skinSlider;
        private Slider          _hairHueSlider;
        private Slider          _hairBrightSlider;

        // ── Reflection ────────────────────────────────────────────────────────
        private static readonly System.Reflection.MethodInfo _updateVisuals =
            AccessTools.Method(typeof(VisEquipment), "UpdateVisuals");

        // ── Colour endpoints (Valheim character creator palette) ──────────────
        private static readonly Color SkinLight  = new Color(0.97f, 0.85f, 0.70f);
        private static readonly Color SkinDark   = new Color(0.52f, 0.32f, 0.18f);
        private static readonly Color HairBlonde = new Color(0.93f, 0.80f, 0.48f);
        private static readonly Color HairDark   = new Color(0.10f, 0.06f, 0.03f);
        private const float HairBrightMin = 0.1f;
        private const float HairBrightMax = 1.0f;

        // ── TraderUI-matching style constants ────────────────────────────────
        private static readonly Color ColBg         = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color GoldColor     = new Color(0.83f, 0.64f, 0.31f, 1f);
        private static readonly Color GoldTextColor = new Color(0.83f, 0.52f, 0.18f, 1f);
        private static readonly Color LabelText     = new Color(1f, 0.9f, 0.5f, 1f);
        private static readonly Color BtnTint       = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color TabInactive   = new Color(0.45f, 0.45f, 0.45f, 1f);

        // Bank balance key — must match TraderOverhaul exactly
        private const string BankDataKey = "TraderSharedBank_Balance";

        // ── Category button sprite — loaded from our own embedded texture ────
        private static Sprite _catBtnSprite;
        private static Sprite _sliderBgSprite;
        private static Sprite _genderBtnSprite;

        private static Sprite GetCatBtnSprite()
        {
            if (_catBtnSprite != null) return _catBtnSprite;
            var tex = TextureLoader.LoadUITexture("CategoryBackground");
            if (tex == null) return null;
            _catBtnSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(12f, 12f, 12f, 12f));
            _catBtnSprite.name = "CategoryBackground";
            return _catBtnSprite;
        }

        private static Sprite GetSliderBgSprite()
        {
            if (_sliderBgSprite != null) return _sliderBgSprite;
            var tex = TextureLoader.LoadUITexture("SliderBackground");
            if (tex == null) return null;
            _sliderBgSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            _sliderBgSprite.name = "SliderBackground";
            return _sliderBgSprite;
        }

        private static Sprite GetGenderBtnSprite()
        {
            if (_genderBtnSprite != null) return _genderBtnSprite;
            var tex = TextureLoader.LoadUITexture("ButtonBackground");
            if (tex == null) return null;
            _genderBtnSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            _genderBtnSprite.name = "ButtonBackground";
            return _genderBtnSprite;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Build
        // ═════════════════════════════════════════════════════════════════════

        public void Build(Transform parent, float colTopInset, float bottomPad,
                          TMP_FontAsset font, GameObject buttonTemplate, float buttonHeight,
                          float leftXL, float leftXR, float midXL, float midXR,
                          float rightXL, float rightXR, float panelHeight)
        {
            _current = CompanionAppearance.Default();
            if (buttonHeight <= 0f) buttonHeight = 30f;

            // Root — invisible container, columns have their own backgrounds
            Root = new GameObject("CompanionContent", typeof(RectTransform));
            Root.transform.SetParent(parent, false);
            var rootRT = Root.GetComponent<RectTransform>();
            rootRT.anchorMin = Vector2.zero;
            rootRT.anchorMax = Vector2.one;
            rootRT.offsetMin = Vector2.zero;
            rootRT.offsetMax = Vector2.zero;
            rootRT.localScale = Vector3.one * UiScale;

            // Three columns matching TraderUI positions exactly
            var leftCol  = CreateColumn("HC_LeftColumn",   Root.transform, leftXL,  leftXR,  bottomPad, colTopInset);
            var midCol   = CreateColumn("HC_MiddleColumn", Root.transform, midXL,   midXR,   bottomPad, colTopInset);
            var rightCol = CreateColumn("HC_RightColumn",  Root.transform, rightXL, rightXR, bottomPad, colTopInset);

            // Compute column dimensions for proportional RT
            float colW = rightXR - rightXL;
            float colH = panelHeight - colTopInset - bottomPad;

            BuildCustomColumn(leftCol, font, buttonTemplate);
            BuildMiddleColumn(midCol, font, buttonTemplate, buttonHeight);
            BuildPreviewColumn(rightCol, colW, colH);

            Root.SetActive(false);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Column factory — matches TraderUI.CreateColumn exactly
        // ═════════════════════════════════════════════════════════════════════

        private static RectTransform CreateColumn(string name, Transform parent,
            float xLeft, float xRight, float bottomPad, float colTopInset)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot     = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(xLeft, bottomPad);
            rt.offsetMax = new Vector2(xRight, -colTopInset);
            var img = go.GetComponent<Image>();
            img.sprite = null;
            img.color  = ColBg;
            return rt;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Middle column — title, description, coin display, buy button
        // ═════════════════════════════════════════════════════════════════════

        private void BuildMiddleColumn(RectTransform col, TMP_FontAsset font,
                                       GameObject buttonTemplate, float btnH)
        {
            // Title — matches TraderUI item name header
            var nameGO = new GameObject("Title", typeof(RectTransform));
            nameGO.transform.SetParent(col, false);
            var nameTmp = nameGO.AddComponent<TextMeshProUGUI>();
            if (font != null) nameTmp.font = font;
            nameTmp.fontSize      = 24f;
            nameTmp.color         = Color.white;
            nameTmp.alignment     = TextAlignmentOptions.Center;
            nameTmp.text          = "Companions";
            nameTmp.raycastTarget = false;
            var nameRT = nameGO.GetComponent<RectTransform>();
            nameRT.anchorMin        = new Vector2(0f, 1f);
            nameRT.anchorMax        = new Vector2(1f, 1f);
            nameRT.pivot            = new Vector2(0.5f, 1f);
            nameRT.sizeDelta        = new Vector2(-20f, 32f);
            nameRT.anchoredPosition = new Vector2(0f, -4f);

            // Bank balance display — above buy button
            float descBottom = btnH + 44f;
            var coinGO = new GameObject("BankDisplay", typeof(RectTransform));
            coinGO.transform.SetParent(col, false);
            _coinText = coinGO.AddComponent<TextMeshProUGUI>();
            if (font != null) _coinText.font = font;
            _coinText.fontSize      = 24f;
            _coinText.color         = GoldTextColor;
            _coinText.alignment     = TextAlignmentOptions.Center;
            _coinText.text          = "Bank: 0";
            _coinText.raycastTarget = false;
            var coinRT = coinGO.GetComponent<RectTransform>();
            coinRT.anchorMin        = new Vector2(0f, 0f);
            coinRT.anchorMax        = new Vector2(1f, 0f);
            coinRT.pivot            = new Vector2(0.5f, 0f);
            coinRT.sizeDelta        = new Vector2(-24f, 24f);
            coinRT.anchoredPosition = new Vector2(0f, btnH + 14f);

            // Buy button — from buttonTemplate with tint overlay (action button style)
            if (buttonTemplate != null)
            {
                var btnGO = CreateActionButton(buttonTemplate, col, "BuyButton",
                    $"Buy ({CompanionTierData.Price:N0})");

                _buyButton = btnGO.GetComponent<Button>();
                if (_buyButton != null)
                {
                    _buyButton.onClick.AddListener(OnBuyClicked);
                    _buyButton.interactable = true;
                }

                _buyButtonLabel = btnGO.GetComponentInChildren<TMP_Text>(true);

                var bRT = btnGO.GetComponent<RectTransform>();
                bRT.anchorMin        = new Vector2(0f, 0f);
                bRT.anchorMax        = new Vector2(1f, 0f);
                bRT.pivot            = new Vector2(0.5f, 0f);
                bRT.sizeDelta        = new Vector2(-24f, btnH);
                bRT.anchoredPosition = new Vector2(0f, 8f);
            }

            // Scrollable description area
            var descScrollGO = new GameObject("DescScrollArea", typeof(RectTransform), typeof(Image), typeof(Mask));
            descScrollGO.transform.SetParent(col, false);
            var dsRT = descScrollGO.GetComponent<RectTransform>();
            dsRT.anchorMin = Vector2.zero;
            dsRT.anchorMax = Vector2.one;
            dsRT.offsetMin = new Vector2(8f, descBottom);
            dsRT.offsetMax = new Vector2(-14f, -38f);
            descScrollGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.003f);
            descScrollGO.GetComponent<Mask>().showMaskGraphic = false;

            var descTextGO = new GameObject("DescText", typeof(RectTransform));
            descTextGO.transform.SetParent(descScrollGO.transform, false);
            var descTmp = descTextGO.AddComponent<TextMeshProUGUI>();
            if (font != null) descTmp.font = font;
            descTmp.fontSize         = 18f;
            descTmp.color            = Color.white;
            descTmp.alignment        = TextAlignmentOptions.TopLeft;
            descTmp.textWrappingMode = TextWrappingModes.Normal;
            descTmp.overflowMode     = TextOverflowModes.Overflow;
            descTmp.richText         = true;
            descTmp.raycastTarget    = false;
            descTmp.text             = "Hire a loyal companion to join your journey. "
                                     + "They will follow you across the world and fight "
                                     + "by your side against any threat.\n\n"
                                     + "Each companion has their own health, stamina "
                                     + "and inventory. You will need to equip them with "
                                     + "gear and keep them fed to stay battle-ready.\n\n"
                                     + "Interact with your companion to open their panel, "
                                     + "where you can manage their equipment and issue "
                                     + "commands.\n\n"
                                     + "Customise their appearance on the left, then "
                                     + "confirm your purchase.";
            var dtRT = descTextGO.GetComponent<RectTransform>();
            dtRT.anchorMin        = new Vector2(0f, 1f);
            dtRT.anchorMax        = new Vector2(1f, 1f);
            dtRT.pivot            = new Vector2(0.5f, 1f);
            dtRT.anchoredPosition = Vector2.zero;
            dtRT.sizeDelta        = Vector2.zero;
            descTextGO.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Hidden scrollbar
            var descSB = CreateHiddenScrollbar(col);
            var scrollRect = descScrollGO.AddComponent<ScrollRect>();
            scrollRect.content   = dtRT;
            scrollRect.viewport  = dsRT;
            scrollRect.vertical   = true;
            scrollRect.horizontal = false;
            scrollRect.movementType       = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity  = 40f;
            scrollRect.verticalScrollbar  = descSB;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Preview column — proportional RenderTexture
        // ═════════════════════════════════════════════════════════════════════

        private void BuildPreviewColumn(RectTransform col, float colW, float colH)
        {
            // RawImage fills entire column
            var imgGO = new GameObject("PreviewImg", typeof(RectTransform));
            imgGO.transform.SetParent(col, false);
            var imgRT = imgGO.GetComponent<RectTransform>();
            imgRT.anchorMin = Vector2.zero;
            imgRT.anchorMax = Vector2.one;
            imgRT.offsetMin = Vector2.zero;
            imgRT.offsetMax = Vector2.zero;

            _previewImg               = imgGO.AddComponent<RawImage>();
            _previewImg.color         = Color.white;
            _previewImg.raycastTarget = false;

            // RenderTexture — proportional to column dimensions, not square
            int rtScale = 4;
            int rtW = Mathf.Max(64, Mathf.RoundToInt(colW) * rtScale);
            int rtH = Mathf.Max(64, Mathf.RoundToInt(colH) * rtScale);
            _rt              = new RenderTexture(rtW, rtH, 24, RenderTextureFormat.ARGB32);
            _rt.antiAliasing = 4;
            _rt.filterMode   = FilterMode.Trilinear;
            _previewImg.texture = _rt;

            // Camera
            _camGO = new GameObject("HC_CompanionPreviewCam");
            UnityEngine.Object.DontDestroyOnLoad(_camGO);
            _cam               = _camGO.AddComponent<Camera>();
            _cam.targetTexture = _rt;
            _cam.clearFlags    = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.fieldOfView   = 30f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane  = 10f;
            _cam.depth         = -2;
            _cam.enabled       = false;

            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            int charNet = LayerMask.NameToLayer("character_net");
            int mask    = 1 << charLayer;
            if (charNet >= 0) mask |= 1 << charNet;
            _cam.cullingMask = mask;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Customisation column — uses button template for all controls
        // ═════════════════════════════════════════════════════════════════════

        private void BuildCustomColumn(RectTransform col, TMP_FontAsset font,
                                       GameObject buttonTemplate)
        {
            // Scrollable container
            var scrollGO = new GameObject("CustomScroll", typeof(RectTransform), typeof(Image), typeof(Mask));
            scrollGO.transform.SetParent(col, false);
            var scrollRT = scrollGO.GetComponent<RectTransform>();
            scrollRT.anchorMin = Vector2.zero;
            scrollRT.anchorMax = Vector2.one;
            scrollRT.offsetMin = Vector2.zero;
            scrollRT.offsetMax = Vector2.zero;
            scrollGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.003f);
            scrollGO.GetComponent<Mask>().showMaskGraphic = false;

            var contentGO = new GameObject("Content", typeof(RectTransform));
            contentGO.transform.SetParent(scrollGO.transform, false);
            var contentRT = contentGO.GetComponent<RectTransform>();
            contentRT.anchorMin        = new Vector2(0f, 1f);
            contentRT.anchorMax        = new Vector2(1f, 1f);
            contentRT.pivot            = new Vector2(0.5f, 1f);
            contentRT.anchoredPosition = Vector2.zero;

            var sb = CreateHiddenScrollbar(col);
            var sr = scrollGO.AddComponent<ScrollRect>();
            sr.content   = contentRT;
            sr.viewport  = scrollRT;
            sr.vertical   = true;
            sr.horizontal = false;
            sr.movementType      = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 40f;
            sr.verticalScrollbar = sb;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            Transform p = contentGO.transform;
            float y       = -6f;
            float gap     = 4f;
            float secGap  = 10f;
            float labelH  = 22f;
            float genderH = 42f;
            float pickerH = 36f;
            float sliderH = 24f;

            // ── Gender ──────────────────────────────────────────────────────
            y = PlacePlainLabel(p, font, "Gender", y, labelH);
            y -= gap;

            var gRow = MakeAnchoredRow(p, y, genderH);
            y -= genderH + secGap;
            _maleBtn   = AddToggleButton(gRow, "Male",   buttonTemplate, new Vector2(0.01f, 0f), new Vector2(0.49f, 1f), () => SetGender(0));
            _femaleBtn = AddToggleButton(gRow, "Female", buttonTemplate, new Vector2(0.51f, 0f), new Vector2(0.99f, 1f), () => SetGender(1));

            // ── Hair Style ──────────────────────────────────────────────────
            y = PlacePlainLabel(p, font, "Hair Style", y, labelH);
            y -= gap;

            BuildPickerRow(p, out _hairNameText, "Hair1", y, pickerH, font, buttonTemplate,
                () => CycleHair(-1), () => CycleHair(1));
            y -= pickerH + secGap;

            // ── Beard Style (hidden for female) ─────────────────────────────
            _beardGroup = new GameObject("BeardGroup", typeof(RectTransform));
            _beardGroup.transform.SetParent(p, false);
            var bgRT = _beardGroup.GetComponent<RectTransform>();
            bgRT.anchorMin        = new Vector2(0f, 1f);
            bgRT.anchorMax        = new Vector2(1f, 1f);
            bgRT.pivot            = new Vector2(0f, 1f);
            bgRT.anchoredPosition = new Vector2(0f, y);

            float by = 0f;
            by = PlacePlainLabel(_beardGroup.transform, font, "Beard Style", by, labelH);
            by -= gap;
            BuildPickerRow(_beardGroup.transform, out _beardNameText, "None", by, pickerH, font, buttonTemplate,
                () => CycleBeard(-1), () => CycleBeard(1));
            by -= pickerH + secGap;

            float beardGroupH = -by;
            bgRT.sizeDelta = new Vector2(0f, beardGroupH);
            y -= beardGroupH;

            // ── Skin Tone ───────────────────────────────────────────────────
            y = PlacePlainLabel(p, font, "Skin Tone", y, labelH);
            y -= gap;
            _skinSlider = MakeSlider(p, "SkinSlider", y, sliderH, 0.05f, v =>
            {
                _current.SkinColor = Utils.ColorToVec3(Color.Lerp(SkinLight, SkinDark, v));
                RefreshPreviewAppearance();
            });
            y -= sliderH + secGap;

            // ── Hair Tone ───────────────────────────────────────────────────
            y = PlacePlainLabel(p, font, "Hair Tone", y, labelH);
            y -= gap;
            _hairHueSlider = MakeSlider(p, "HairHue", y, sliderH, 0.45f, _ => UpdateHairColor());
            y -= sliderH + secGap;

            // ── Hair Shade ──────────────────────────────────────────────────
            y = PlacePlainLabel(p, font, "Hair Shade", y, labelH);
            y -= gap;
            _hairBrightSlider = MakeSlider(p, "HairBright", y, sliderH, 0.85f, _ => UpdateHairColor());
            y -= sliderH + secGap;

            // Set content height
            contentRT.sizeDelta = new Vector2(0f, -y + 8f);

            // Sync initial values
            _current.SkinColor = Utils.ColorToVec3(Color.Lerp(SkinLight, SkinDark, _skinSlider.value));
            UpdateHairColor();

            // Initial visual state
            RefreshGenderButtons();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public interface (called by TraderUIPatches)
        // ═════════════════════════════════════════════════════════════════════

        public void Refresh()
        {
            if (!_listsLoaded) LoadHairBeardLists();
            SetupPreviewClone();
            RefreshBankDisplay();
        }

        public void UpdatePerFrame()
        {
            UpdatePreviewBounds();
            UpdatePreviewCamera();
            LockPreviewCloneFacing();
            RefreshBankDisplay();

            if (_cam == null) return;
            _savedAmbient          = RenderSettings.ambientLight;
            _savedAmbientIntensity = RenderSettings.ambientIntensity;
            _savedAmbientMode      = RenderSettings.ambientMode;
            try
            {
                RenderSettings.ambientMode      = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight     = new Color(0.45f, 0.4f, 0.35f);
                RenderSettings.ambientIntensity = 1.2f;
                _cam.Render();
            }
            finally
            {
                RenderSettings.ambientMode      = _savedAmbientMode;
                RenderSettings.ambientLight     = _savedAmbient;
                RenderSettings.ambientIntensity = _savedAmbientIntensity;
            }
        }

        public void Teardown()
        {
            ClearPreviewClone();
            if (_camGO != null) { UnityEngine.Object.Destroy(_camGO); _camGO = null; _cam = null; }
            if (_rt    != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt);    _rt  = null; }
            if (Root   != null) { UnityEngine.Object.Destroy(Root);                  Root = null; }
            _listsLoaded = false;

            // Clear static sprite caches so they are recreated on next Build
            _catBtnSprite    = null;
            _sliderBgSprite  = null;
            _genderBtnSprite = null;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Preview clone
        // ═════════════════════════════════════════════════════════════════════

        private void SetupPreviewClone()
        {
            ClearPreviewClone();

            var prefab = ZNetScene.instance?.GetPrefab("Player");
            if (prefab == null) return;

            ZNetView.m_forceDisableInit = true;
            try   { _clone = UnityEngine.Object.Instantiate(prefab, PreviewPos, Quaternion.identity); }
            finally { ZNetView.m_forceDisableInit = false; }

            var rb = _clone.GetComponent<Rigidbody>();
            if (rb != null) UnityEngine.Object.Destroy(rb);

            foreach (var mb in _clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is VisEquipment) continue;
                mb.enabled = false;
            }
            foreach (var anim in _clone.GetComponentsInChildren<Animator>())
                anim.updateMode = AnimatorUpdateMode.Normal;

            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            foreach (var t in _clone.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = charLayer;

            SetupLightRig();

            _previewBoundsTimer = 0f;
            RecalculatePreviewBounds();
            UpdatePreviewCamera();
            LockPreviewCloneFacing();

            ApplyCurrentToClone();
            RecalculatePreviewBounds();
            UpdatePreviewCamera();
            LockPreviewCloneFacing();
        }

        private void ClearPreviewClone()
        {
            if (_lightRig != null) { UnityEngine.Object.Destroy(_lightRig); _lightRig = null; }
            if (_clone    != null) { UnityEngine.Object.Destroy(_clone);    _clone    = null; }
            _previewCenter = PreviewPos + Vector3.up * 0.95f;
            _previewDistance = 3.6f;
            _previewBoundsTimer = 0f;
        }

        private void SetupLightRig()
        {
            if (_lightRig != null) UnityEngine.Object.Destroy(_lightRig);
            _lightRig = new GameObject("HC_PreviewLightRig");
            UnityEngine.Object.DontDestroyOnLoad(_lightRig);
            _lightRig.transform.position = PreviewPos;

            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            int charNet   = LayerMask.NameToLayer("character_net");
            int lightMask = 1 << charLayer;
            if (charNet >= 0) lightMask |= 1 << charNet;

            AddLight(_lightRig.transform, "Key",    new Vector3( 1.5f,  2.5f,  3.5f), 2.0f, new Color(1.00f, 0.92f, 0.82f), 15f, lightMask);
            AddLight(_lightRig.transform, "Fill",   new Vector3(-2.5f,  1.5f,  3.0f), 1.2f, new Color(0.90f, 0.92f, 1.00f), 15f, lightMask);
            AddLight(_lightRig.transform, "Rim",    new Vector3( 0.0f,  3.0f, -2.5f), 1.2f, new Color(0.95f, 0.88f, 0.78f), 15f, lightMask);
            AddLight(_lightRig.transform, "Bottom", new Vector3( 0.0f, -0.5f,  3.0f), 0.5f, new Color(0.85f, 0.82f, 0.78f), 10f, lightMask);
        }

        private static void AddLight(Transform parent, string name,
            Vector3 pos, float intensity, Color color, float range, int mask)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var lt        = go.AddComponent<Light>();
            lt.type       = LightType.Point;
            lt.intensity  = intensity;
            lt.color      = color;
            lt.range      = range;
            lt.shadows    = LightShadows.None;
            lt.cullingMask = mask;
        }

        private void UpdatePreviewCamera()
        {
            if (_camGO == null) return;
            Vector3 offset = PreviewCameraOffsetDir * _previewDistance;
            _camGO.transform.position = _previewCenter + offset;
            _camGO.transform.LookAt(_previewCenter);
        }

        private void LockPreviewCloneFacing()
        {
            if (_clone == null) return;

            _clone.transform.position = PreviewPos;
            _clone.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        }

        private void UpdatePreviewBounds()
        {
            if (_clone == null || _cam == null) return;
            _previewBoundsTimer -= Time.deltaTime;
            if (_previewBoundsTimer > 0f) return;
            _previewBoundsTimer = PreviewBoundsRefreshInterval;
            RecalculatePreviewBounds();
        }

        private void RecalculatePreviewBounds()
        {
            if (_clone == null || _cam == null) return;

            bool hasBounds = false;
            Bounds bounds = default;
            foreach (var renderer in _clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
            {
                _previewCenter = PreviewPos + Vector3.up * 0.95f;
                _previewDistance = 3.6f;
                return;
            }

            _previewCenter = bounds.center + Vector3.up * (bounds.extents.y * 0.05f);
            float radius = Mathf.Max(0.6f, bounds.extents.magnitude * 1.05f);
            float aspect = (_rt != null && _rt.height > 0) ? (float)_rt.width / _rt.height : 1f;
            float halfFovV = _cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float halfFovH = Mathf.Atan(Mathf.Tan(halfFovV) * aspect);
            float limitingHalfFov = Mathf.Min(halfFovV, halfFovH);
            float safeHalfFov = Mathf.Max(0.2f, limitingHalfFov);

            _previewDistance = Mathf.Clamp(radius / Mathf.Sin(safeHalfFov), 2.2f, 6f);
            _cam.farClipPlane = Mathf.Max(10f, _previewDistance + 8f);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Apply appearance to preview clone
        // ═════════════════════════════════════════════════════════════════════

        private void ApplyCurrentToClone()
        {
            if (_clone == null) return;
            var ve = _clone.GetComponent<VisEquipment>();
            if (ve == null) return;

            ve.SetModel(_current.ModelIndex);
            ve.SetHairItem(string.IsNullOrEmpty(_current.HairItem) ? "Hair1" : _current.HairItem);
            ve.SetBeardItem(_current.ModelIndex == 0 ? (_current.BeardItem ?? "") : "");
            ve.SetSkinColor(_current.SkinColor);
            ve.SetHairColor(_current.HairColor);

            _updateVisuals?.Invoke(ve, null);

            var anim = _clone.GetComponentInChildren<Animator>(true);
            if (anim != null) anim.Update(0f);

            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            foreach (var t in _clone.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = charLayer;
        }

        private void RefreshPreviewAppearance() => ApplyCurrentToClone();

        // ═════════════════════════════════════════════════════════════════════
        //  Hair / beard list loader
        // ═════════════════════════════════════════════════════════════════════

        private void LoadHairBeardLists()
        {
            if (ObjectDB.instance == null) return;

            var rawHairs = ObjectDB.instance.GetAllItems(
                ItemDrop.ItemData.ItemType.Customization, "Hair");
            rawHairs.RemoveAll(x => x.name.Contains("_"));
            rawHairs.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
            _hairs.Clear();
            foreach (var h in rawHairs) _hairs.Add(h.name);
            if (_hairs.Count == 0) _hairs.Add("Hair1");

            var rawBeards = ObjectDB.instance.GetAllItems(
                ItemDrop.ItemData.ItemType.Customization, "Beard");
            rawBeards.RemoveAll(x => x.name.Contains("_"));
            rawBeards.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
            _beards.Clear();
            _beards.Add("");
            foreach (var b in rawBeards) _beards.Add(b.name);

            _hairIndex  = Mathf.Max(0, _hairs.IndexOf(_current.HairItem));
            _beardIndex = Mathf.Max(0, _beards.IndexOf(_current.BeardItem));

            RefreshPickerLabels();
            _listsLoaded = true;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Customisation callbacks
        // ═════════════════════════════════════════════════════════════════════

        private void SetGender(int model)
        {
            _current.ModelIndex = model;
            if (model == 1) { _current.BeardItem = ""; _beardIndex = 0; }
            if (_beardGroup != null) _beardGroup.SetActive(model == 0);
            RefreshGenderButtons();
            RefreshPickerLabels();
            RefreshPreviewAppearance();
        }

        private void CycleHair(int dir)
        {
            if (_hairs.Count == 0) return;
            _hairIndex        = (_hairIndex + dir + _hairs.Count) % _hairs.Count;
            _current.HairItem = _hairs[_hairIndex];
            RefreshPickerLabels();
            RefreshPreviewAppearance();
        }

        private void CycleBeard(int dir)
        {
            if (_beards.Count == 0) return;
            _beardIndex        = (_beardIndex + dir + _beards.Count) % _beards.Count;
            _current.BeardItem = _beards[_beardIndex];
            RefreshPickerLabels();
            RefreshPreviewAppearance();
        }

        private void UpdateHairColor()
        {
            float hue    = _hairHueSlider    != null ? _hairHueSlider.value    : 0.3f;
            float bright = _hairBrightSlider != null ? _hairBrightSlider.value : 0.8f;
            Color c      = Color.Lerp(HairBlonde, HairDark, hue) * Mathf.Lerp(HairBrightMin, HairBrightMax, bright);
            _current.HairColor = Utils.ColorToVec3(c);
            RefreshPreviewAppearance();
        }

        private void OnBuyClicked()
        {
            CompanionManager.Purchase(_current);
            RefreshBankDisplay();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI state refresh
        // ═════════════════════════════════════════════════════════════════════

        private void RefreshPickerLabels()
        {
            if (_hairNameText  != null)
                _hairNameText.text  = _hairs.Count > 0 ? _hairs[_hairIndex] : "\u2014";
            if (_beardNameText != null)
                _beardNameText.text = _beardIndex == 0 ? "None"
                    : (_beards.Count > _beardIndex ? _beards[_beardIndex] : "\u2014");
        }

        private void RefreshGenderButtons()
        {
            SetToggleActive(_maleBtn,   _current.ModelIndex == 0);
            SetToggleActive(_femaleBtn, _current.ModelIndex == 1);
        }

        /// <summary>
        /// Reads bank balance from Player.m_customData — same key as TraderOverhaul.
        /// </summary>
        private static int GetBankBalance()
        {
            var player = Player.m_localPlayer;
            if (player == null) return 0;
            if (player.m_customData.TryGetValue(BankDataKey, out string val) &&
                int.TryParse(val, out int balance))
                return balance;
            return 0;
        }

        private void RefreshBankDisplay()
        {
            int balance = GetBankBalance();
            int price   = CompanionTierData.Price;

            if (_coinText != null)
                _coinText.text = $"Bank: {balance:N0}";

            if (_buyButton != null)
                _buyButton.interactable = balance >= price;

            if (_buyButtonLabel != null)
                _buyButtonLabel.text = balance >= price
                    ? $"Buy ({price:N0})"
                    : $"Need {price:N0}";
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Button helpers — all from buttonTemplate (Valheim button look)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a button from the craft button template WITHOUT tint overlay.
        /// Used for gender toggles, picker arrows — gets the Valheim button sprite/look
        /// without the extra darkness that tint overlays cause inside dark columns.
        /// </summary>
        private static GameObject CreateButton(GameObject template, Transform parent,
            string name, string label)
        {
            var go = UnityEngine.Object.Instantiate(template, parent);
            go.name = name;
            go.SetActive(true);

            var btn = go.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.navigation = new Navigation { mode = Navigation.Mode.None };
            }

            var txt = go.GetComponentInChildren<TMP_Text>(true);
            if (txt != null)
            {
                txt.gameObject.SetActive(true);
                txt.text = label;
            }

            StripButtonHints(go, txt);
            return go;
        }

        /// <summary>
        /// Creates a button from template WITH tint overlay.
        /// Only used for the buy button (big action button), matching TraderUI's action button pattern.
        /// </summary>
        private static GameObject CreateActionButton(GameObject template, Transform parent,
            string name, string label)
        {
            var go = CreateButton(template, parent, name, label);

            // Tint overlay — matches TraderUI action button pattern
            var tintGO = new GameObject("Tint", typeof(RectTransform), typeof(Image));
            tintGO.transform.SetParent(go.transform, false);
            tintGO.transform.SetAsFirstSibling();
            var tintRT = tintGO.GetComponent<RectTransform>();
            tintRT.anchorMin = Vector2.zero;
            tintRT.anchorMax = Vector2.one;
            tintRT.offsetMin = Vector2.zero;
            tintRT.offsetMax = Vector2.zero;
            tintGO.GetComponent<Image>().color         = BtnTint;
            tintGO.GetComponent<Image>().raycastTarget = false;

            return go;
        }

        private static void StripButtonHints(GameObject btnGO, TMP_Text label)
        {
            var anim = btnGO.GetComponent<Animator>();
            if (anim != null) UnityEngine.Object.Destroy(anim);
            var csf = btnGO.GetComponent<ContentSizeFitter>();
            if (csf != null) UnityEngine.Object.Destroy(csf);
            var le = btnGO.GetComponent<LayoutElement>();
            if (le != null) UnityEngine.Object.Destroy(le);

            for (int i = btnGO.transform.childCount - 1; i >= 0; i--)
            {
                var child = btnGO.transform.GetChild(i);
                if (label != null && (child.gameObject == label.gameObject || label.transform.IsChildOf(child)))
                    continue;
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        /// <summary>
        /// Creates a gender toggle from the button template WITHOUT tint overlay.
        /// No tint avoids double-darkness inside the already-dark left column.
        /// </summary>
        private static Button AddToggleButton(Transform parent, string label,
            GameObject buttonTemplate, Vector2 anchorMin, Vector2 anchorMax, Action onClick)
        {
            var go = CreateButton(buttonTemplate, parent, label, label);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            // Use custom gender button background texture.
            var genderSprite = GetGenderBtnSprite();
            var bgImg = go.GetComponent<Image>();
            if (bgImg != null && genderSprite != null)
            {
                bgImg.sprite = genderSprite;
                bgImg.type = Image.Type.Simple;
                bgImg.preserveAspect = false;
                bgImg.color = Color.white;
            }

            // Bring back the dark tint overlay for gender buttons.
            var tintGO = new GameObject("Tint", typeof(RectTransform), typeof(Image));
            tintGO.transform.SetParent(go.transform, false);
            tintGO.transform.SetAsFirstSibling();
            var tintRT = tintGO.GetComponent<RectTransform>();
            tintRT.anchorMin = Vector2.zero;
            tintRT.anchorMax = Vector2.one;
            tintRT.offsetMin = Vector2.zero;
            tintRT.offsetMax = Vector2.zero;
            tintGO.GetComponent<Image>().color = BtnTint;
            tintGO.GetComponent<Image>().raycastTarget = false;

            // Slightly smaller gender label text.
            var txt = go.GetComponentInChildren<TMP_Text>(true);
            if (txt != null)
            {
                txt.enableAutoSizing = false;
                txt.fontSize = 16f;
            }

            var btn = go.GetComponent<Button>();
            if (btn != null)
            {
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => onClick());
            }

            return btn;
        }

        /// <summary>
        /// Highlights a toggle button: active = GoldColor, inactive = grey.
        /// Matches TraderUI tab highlight pattern.
        /// </summary>
        private static void SetToggleActive(Button btn, bool on)
        {
            if (btn == null) return;
            var img = btn.GetComponent<Image>();
            if (img != null)
                img.color = on ? GoldColor : Color.white;
            var txt = btn.GetComponentInChildren<TMP_Text>(true);
            if (txt != null) txt.color = GoldTextColor;
        }

        // ─── Copied from TraderUI.BuildCategoryFilterRow ───────────────────
        // HorizontalLayoutGroup row with childForceExpandWidth/Height = true
        // makes buttons square. Arrow buttons use CreateCategoryArrowButton
        // which is a line-for-line copy of TraderUI.CreateCategoryFilterButton.
        // ─────────────────────────────────────────────────────────────────────

        private static void BuildPickerRow(Transform parent, out TextMeshProUGUI nameLabel,
            string initialName, float y, float rowH, TMP_FontAsset font,
            GameObject buttonTemplate, Action onPrev, Action onNext)
        {
            // Container row — copied from TraderUI.BuildCategoryFilterRow
            var rowGO = new GameObject("PickerRow", typeof(RectTransform), typeof(Image));
            rowGO.transform.SetParent(parent, false);
            var rowRT = rowGO.GetComponent<RectTransform>();
            rowRT.anchorMin = new Vector2(0f, 1f);
            rowRT.anchorMax = new Vector2(1f, 1f);
            rowRT.pivot = new Vector2(0.5f, 1f);
            rowRT.sizeDelta = new Vector2(-4f, rowH);
            rowRT.anchoredPosition = new Vector2(0f, y);
            rowGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f); // transparent — let column bg show through

            var layout = rowGO.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 2f;
            layout.padding = new RectOffset(2, 2, 2, 2);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            // Prev arrow — CreateCategoryArrowButton (line-for-line copy of TraderUI.CreateCategoryFilterButton)
            var prevBtn = CreateCategoryArrowButton(buttonTemplate, rowGO.transform, "Prev", "<", font);
            if (prevBtn != null)
            {
                var prevLE = prevBtn.gameObject.AddComponent<LayoutElement>();
                prevLE.minWidth = rowH - 4f;
                prevLE.preferredWidth = rowH - 4f;
                prevLE.flexibleWidth = 0f;
                prevBtn.onClick.AddListener(() => onPrev());
            }

            // Name label — flexible width fills center
            var labelGO = new GameObject("Name", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            labelGO.transform.SetParent(rowGO.transform, false);
            labelGO.GetComponent<LayoutElement>().flexibleWidth = 1f;
            nameLabel = labelGO.GetComponent<TextMeshProUGUI>();
            nameLabel.text = initialName;
            nameLabel.fontSize = 16f;
            nameLabel.color = GoldTextColor;
            nameLabel.alignment = TextAlignmentOptions.Center;
            nameLabel.textWrappingMode = TextWrappingModes.NoWrap;
            nameLabel.overflowMode = TextOverflowModes.Ellipsis;
            nameLabel.raycastTarget = false;
            if (font != null) nameLabel.font = font;

            // Next arrow — CreateCategoryArrowButton (line-for-line copy of TraderUI.CreateCategoryFilterButton)
            var nextBtn = CreateCategoryArrowButton(buttonTemplate, rowGO.transform, "Next", ">", font);
            if (nextBtn != null)
            {
                var nextLE = nextBtn.gameObject.AddComponent<LayoutElement>();
                nextLE.minWidth = rowH - 4f;
                nextLE.preferredWidth = rowH - 4f;
                nextLE.flexibleWidth = 0f;
                nextBtn.onClick.AddListener(() => onNext());
            }
        }

        // ─── Copied from TraderUI.CreateCategoryFilterButton (lines 821-889) ──
        // Only change: text content instead of icon sprite.
        // Every other line is identical to the TraderUI source.
        // ───────────────────────────────────────────────────────────────────────

        private static Button CreateCategoryArrowButton(GameObject buttonTemplate, Transform parent,
            string name, string text, TMP_FontAsset font)
        {
            if (buttonTemplate == null) return null;

            var btnGO = UnityEngine.Object.Instantiate(buttonTemplate, parent);
            btnGO.name = name;
            btnGO.SetActive(true);

            // Keep the template's pre-configured TMP_Text (has proper Material/shader refs)
            var existingTxt = btnGO.GetComponentInChildren<TMP_Text>(true);

            // Strip all children EXCEPT the label text
            for (int i = btnGO.transform.childCount - 1; i >= 0; i--)
            {
                var child = btnGO.transform.GetChild(i);
                if (existingTxt != null &&
                    (child.gameObject == existingTxt.gameObject ||
                     existingTxt.transform.IsChildOf(child)))
                    continue;
                UnityEngine.Object.Destroy(child.gameObject);
            }

            // Strip components that cause stretching on hover
            var anim = btnGO.GetComponent<Animator>();
            if (anim != null) UnityEngine.Object.Destroy(anim);
            var csf = btnGO.GetComponent<ContentSizeFitter>();
            if (csf != null) UnityEngine.Object.Destroy(csf);
            var le = btnGO.GetComponent<LayoutElement>();
            if (le != null) UnityEngine.Object.Destroy(le);

            // Apply our own embedded CategoryBackground sprite as the button background
            var sprite = GetCatBtnSprite();
            var bgImg = btnGO.GetComponent<Image>();
            if (bgImg == null) bgImg = btnGO.AddComponent<Image>();
            if (bgImg != null)
            {
                if (sprite != null)
                {
                    bgImg.sprite = sprite;
                    bgImg.type = Image.Type.Simple;
                    bgImg.preserveAspect = false;
                }
                bgImg.color = Color.white;
                bgImg.raycastTarget = true;
            }

            var btn = btnGO.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.transition = Selectable.Transition.None;
                btn.navigation = new Navigation { mode = Navigation.Mode.None };
                if (bgImg != null) btn.targetGraphic = bgImg;
            }

            // Dark tint overlay — same as gender buttons
            var tintGO = new GameObject("Tint", typeof(RectTransform), typeof(Image));
            tintGO.transform.SetParent(btnGO.transform, false);
            tintGO.transform.SetAsFirstSibling();
            var tintRT = tintGO.GetComponent<RectTransform>();
            tintRT.anchorMin = Vector2.zero;
            tintRT.anchorMax = Vector2.one;
            tintRT.offsetMin = Vector2.zero;
            tintRT.offsetMax = Vector2.zero;
            tintGO.GetComponent<Image>().color         = BtnTint;
            tintGO.GetComponent<Image>().raycastTarget = false;

            // Configure the kept TMP label — richText off so < > render literally
            if (existingTxt != null)
            {
                existingTxt.gameObject.SetActive(true);
                existingTxt.text          = text;
                existingTxt.richText      = false;
                existingTxt.fontSize      = 22f;
                existingTxt.color         = GoldTextColor;
                existingTxt.alignment     = TextAlignmentOptions.Center;
                existingTxt.raycastTarget = false;
                if (font != null) existingTxt.font = font;
            }

            return btn;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Hidden scrollbar — matches TraderUI pattern
        // ═════════════════════════════════════════════════════════════════════

        private static Scrollbar CreateHiddenScrollbar(Transform parent)
        {
            float sbW = 10f;
            var sbGO = new GameObject("Scrollbar", typeof(RectTransform));
            sbGO.transform.SetParent(parent, false);
            var sbRT = sbGO.GetComponent<RectTransform>();
            sbRT.anchorMin = new Vector2(1f, 0f);
            sbRT.anchorMax = new Vector2(1f, 1f);
            sbRT.pivot     = new Vector2(1f, 0.5f);
            sbRT.sizeDelta = new Vector2(sbW, 0f);
            sbRT.offsetMin = new Vector2(-sbW, 4f);
            sbRT.offsetMax = new Vector2(-2f, -4f);
            sbGO.AddComponent<Image>().color = Color.clear;

            var slidingGO = new GameObject("Sliding Area", typeof(RectTransform));
            slidingGO.transform.SetParent(sbGO.transform, false);
            var sRT = slidingGO.GetComponent<RectTransform>();
            sRT.anchorMin = Vector2.zero; sRT.anchorMax = Vector2.one;
            sRT.offsetMin = Vector2.zero; sRT.offsetMax = Vector2.zero;

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(slidingGO.transform, false);
            var hRT = handleGO.GetComponent<RectTransform>();
            hRT.anchorMin = Vector2.zero; hRT.anchorMax = Vector2.one;
            hRT.offsetMin = Vector2.zero; hRT.offsetMax = Vector2.zero;
            handleGO.GetComponent<Image>().color = Color.clear;

            var sb = sbGO.AddComponent<Scrollbar>();
            sb.handleRect    = hRT;
            sb.direction     = Scrollbar.Direction.BottomToTop;
            sb.targetGraphic = handleGO.GetComponent<Image>();
            return sb;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI factory helpers
        // ═════════════════════════════════════════════════════════════════════

        private static TextMeshProUGUI MakeText(Transform parent, string name, string text,
            TMP_FontAsset font, float size, Color color, TextAlignmentOptions align)
        {
            var go  = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text             = text;
            tmp.fontSize         = size;
            tmp.color            = color;
            tmp.alignment        = align;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode     = TextOverflowModes.Ellipsis;
            tmp.raycastTarget    = false;
            if (font != null) tmp.font = font;
            return tmp;
        }

        /// <summary>
        /// Plain text label — no background tint. Just the label text.
        /// </summary>
        private static float PlacePlainLabel(Transform parent, TMP_FontAsset font,
            string text, float y, float h)
        {
            var tmp = MakeText(parent, text + "Label", text, font, 14f,
                LabelText, TextAlignmentOptions.MidlineLeft);
            tmp.fontStyle = FontStyles.Bold;
            var rt = tmp.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(1f, 1f);
            rt.pivot            = new Vector2(0f, 1f);
            rt.sizeDelta        = new Vector2(0f, h);
            rt.anchoredPosition = new Vector2(4f, y);

            return y - h;
        }


        private static Transform MakeAnchoredRow(Transform parent, float y, float h)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(1f, 1f);
            rt.pivot            = new Vector2(0f, 1f);
            rt.sizeDelta        = new Vector2(0f, h);
            rt.anchoredPosition = new Vector2(0f, y);
            return go.transform;
        }

        // ─── Slider: cloned from InventoryGui.instance.m_splitSlider ───────
        // Clones the actual vanilla Valheim slider to get proper game textures,
        // sprites, and styling. Reparents, repositions, and rewires callbacks.
        // ─────────────────────────────────────────────────────────────────────

        private static Slider MakeSlider(Transform parent, string name,
            float y, float sliderH, float initialValue, Action<float> onChange)
        {
            var srcSlider = InventoryGui.instance?.m_splitSlider;
            if (srcSlider == null)
            {
                CompanionsPlugin.Log.LogWarning("[CompanionPanel] m_splitSlider not available");
                return null;
            }
            var go = UnityEngine.Object.Instantiate(srcSlider.gameObject, parent);
            go.name = name;
            go.SetActive(true);

            // Reposition to fit our layout
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(1f, 1f);
            rt.pivot            = new Vector2(0.5f, 1f);
            rt.sizeDelta        = new Vector2(-4f, sliderH);
            rt.anchoredPosition = new Vector2(0f, y);

            // Strip layout constraints from the clone
            var le = go.GetComponent<LayoutElement>();
            if (le != null) UnityEngine.Object.Destroy(le);

            // Replace slider background with our custom texture and let it stretch.
            var sliderBgSprite = GetSliderBgSprite();
            if (sliderBgSprite != null)
            {
                Image bgImg = null;
                var bg = go.transform.Find("Background");
                if (bg != null) bgImg = bg.GetComponent<Image>();

                if (bgImg == null)
                {
                    foreach (var img in go.GetComponentsInChildren<Image>(true))
                    {
                        if (img != null &&
                            img.gameObject.name.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            bgImg = img;
                            break;
                        }
                    }
                }

                if (bgImg == null) bgImg = go.GetComponent<Image>();

                if (bgImg != null)
                {
                    bgImg.sprite = sliderBgSprite;
                    bgImg.type = Image.Type.Simple;
                    bgImg.preserveAspect = false;
                    bgImg.color = Color.white;
                }
            }

            // Rewire the slider for our 0-1 float range
            var slider = go.GetComponent<Slider>();
            slider.onValueChanged.RemoveAllListeners();
            slider.transition = Selectable.Transition.None;
            slider.wholeNumbers = false;
            slider.minValue     = 0f;
            slider.maxValue     = 1f;

            // Remove inherited dark tinting from cloned slider graphics.
            foreach (var img in go.GetComponentsInChildren<Image>(true))
            {
                if (img == null) continue;
                var c = img.color;
                img.color = new Color(1f, 1f, 1f, c.a);
            }

            // Gold fill for the inner bar while keeping vanilla frame/handle textures.
            if (slider.fillRect != null)
            {
                var fillImg = slider.fillRect.GetComponent<Image>();
                if (fillImg != null) fillImg.color = GoldColor;
            }

            // Dark tint overlay — inset horizontally to match the visible slider track
            var tintGO = new GameObject("Tint", typeof(RectTransform), typeof(Image));
            tintGO.transform.SetParent(go.transform, false);
            tintGO.transform.SetAsFirstSibling();
            var tintRT = tintGO.GetComponent<RectTransform>();
            tintRT.anchorMin = Vector2.zero;
            tintRT.anchorMax = Vector2.one;
            tintRT.offsetMin = new Vector2(8f, 0f);
            tintRT.offsetMax = new Vector2(-8f, 0f);
            tintGO.GetComponent<Image>().color         = BtnTint;
            tintGO.GetComponent<Image>().raycastTarget = false;

            slider.value        = initialValue;
            slider.onValueChanged.AddListener(v => onChange(v));

            return slider;
        }
    }
}
