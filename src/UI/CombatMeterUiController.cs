using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Snapshot;
using UnityEngine;
using UnityEngine.UI;

namespace DiagnosticDamageProbe.UI;

internal sealed class CombatMeterUiController
{
    private const float RowHeight = 22f;
    private readonly CombatMeterPresenter _presenter = new CombatMeterPresenter();
    private readonly CombatMeterVisibilityState _visibility = new CombatMeterVisibilityState();
    private readonly CombatMeterEditModeState _edit = new CombatMeterEditModeState();
    private readonly List<RowView> _rows = new List<RowView>();
    private GameObject _canvasRoot, _panel;
    private RectTransform _canvasRect, _panelRect, _dragHandle;
    private Image _panelImage;
    private Text _title, _state, _time, _playerHeader, _damageHeader, _percentHeader, _dpsHeader, _takenHeader;
    private Font _font;
    private bool _creationWarningLogged, _lastVisible, _previewRendered;
    private Vector2 _dragStartPointer, _dragStartPosition;
    private float _width, _scale, _opacity, _canvasWidth, _canvasHeight;
    private float _barOpacity;
    private bool _showBars, _showPercent;
    private long _lastLoggedEncounter;
    private string _lastLoggedState;
    private float _nextRenderLogAt;

    internal bool IsCreated => _canvasRoot;
    internal bool UserHidden => _visibility.UserHidden;
    internal bool IsEditing => _edit.IsEditing;

    internal void Tick(CombatSnapshotStore store, bool worldReady, bool enabled, bool togglePressed, bool resetPressed,
        bool editTogglePressed, bool escapePressed, bool vanillaModalVisible,
        float positionX, float positionY, float scale, float width, float opacity,
        bool showBars, bool showPercent, float barOpacity, Action<float, float> persistPosition)
    {
        if (!worldReady || !enabled) { ExitEditMode("Lifecycle", persistPosition); Destroy(); return; }
        if (!IsCreated && !TryCreate()) return;
        ApplyLayout(positionX, positionY, scale, width, opacity, showBars, showPercent, barOpacity, persistPosition);
        if (editTogglePressed)
        {
            if (_edit.IsEditing) ExitEditMode("Toggle", persistPosition);
            else if (!vanillaModalVisible) EnterEditMode();
        }
        else if (_edit.IsEditing && escapePressed) ExitEditMode("Escape", persistPosition);
        else if (_edit.IsEditing && vanillaModalVisible) ExitEditMode("VanillaModal", persistPosition);
        if (resetPressed) ResetPosition(persistPosition);
        if (togglePressed && _edit.AcceptVisibilityToggle)
        {
            _visibility.Toggle();
            Plugin.UiLog("CombatMeterUiVisibilityChanged visible=" + (!_visibility.UserHidden).ToString().ToLowerInvariant());
        }
        CombatSnapshot snapshot = null;
        bool hasSnapshot = store != null && store.TryGetLatest(out snapshot);
        bool hasEncounter = hasSnapshot && snapshot.EncounterState != Encounter.EncounterState.NoEncounter;
        if (!hasEncounter)
        {
            _presenter.Reset();
            if (_edit.IsEditing)
            {
                if (!_previewRendered) { Render(CombatMeterPreview.Build(), 0, persistPosition); _previewRendered = true; }
            }
            else _previewRendered = false;
            SetPanelVisible(_edit.ShouldShow(enabled, _visibility.UserHidden, false)); UpdateDrag(persistPosition); return;
        }
        _previewRendered = false;
        if (_presenter.TryBuild(snapshot, out CombatMeterViewModel model)) Render(model, snapshot.Sequence, persistPosition);
        SetPanelVisible(_edit.ShouldShow(enabled, _visibility.UserHidden, true));
        UpdateDrag(persistPosition);
    }

    internal void ExitEditMode(string reason, Action<float, float> persistPosition)
    {
        if (!_edit.IsEditing) return;
        if (reason == "Escape") Plugin.MarkUiEscapeConsumed();
        if (_edit.IsDragging) FinishDrag(persistPosition, false);
        _edit.Exit(); SetEditAppearance(false); Plugin.RestoreVanillaCursor();
        Plugin.UiLog("CombatMeterEditModeExited reason=" + reason);
    }

    private void EnterEditMode()
    {
        if (!_edit.Enter()) return;
        if (!Plugin.AcquireUiCursor())
        {
            _edit.Exit(); Plugin.Warn("CombatMeterEditModeFailed reason=CursorUnavailable"); return;
        }
        SetEditAppearance(true); Plugin.UiLog("CombatMeterEditModeEntered");
    }

    private void SetEditAppearance(bool editing)
    {
        if (_title) _title.text = editing ? "Combat Meter [EDIT MODE] — drag header — Ctrl+F8 / Esc to finish" : "Combat Meter";
        if (_dragHandle) _dragHandle.GetComponent<Image>().color = editing
            ? new Color(0.32f, 0.22f, 0.08f, 0.96f) : new Color(0.13f, 0.13f, 0.16f, 0.9f);
    }

    private bool TryCreate()
    {
        try
        {
            _font = ResolveFont();
            _canvasRoot = new GameObject("CombatMeterCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_canvasRoot); _canvasRect = (RectTransform)_canvasRoot.transform;
            Canvas canvas = _canvasRoot.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 40;
            CanvasScaler scaler = _canvasRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f); scaler.matchWidthOrHeight = 0.5f;
            _panel = new GameObject("CombatMeterPanel", typeof(RectTransform), typeof(Image));
            _panel.transform.SetParent(_canvasRoot.transform, false); _panelRect = (RectTransform)_panel.transform;
            SetRect(_panelRect, CombatMeterLayout.DefaultX, CombatMeterLayout.DefaultY, CombatMeterLayout.DefaultWidth, 96f);
            _panelImage = _panel.GetComponent<Image>(); _panelImage.raycastTarget = false;

            GameObject header = new GameObject("DragHandle", typeof(RectTransform), typeof(Image)); header.transform.SetParent(_panel.transform, false);
            _dragHandle = (RectTransform)header.transform; SetRect(_dragHandle, 0f, 0f, CombatMeterLayout.DefaultWidth, 32f);
            Image headerImage = header.GetComponent<Image>(); headerImage.color = new Color(0.13f, 0.13f, 0.16f, 0.9f); headerImage.raycastTarget = false;
            _title = AddText(header.transform, "Title", "Combat Meter", 12f, 0f, 456f, 32f, 18, TextAnchor.MiddleLeft, FontStyle.Bold);
            _state = AddText(_panel.transform, "State", "State: NoEncounter", 12f, -36f, 220f, 22f, 14, TextAnchor.MiddleLeft);
            _time = AddText(_panel.transform, "Time", "Time: 0.0s", 244f, -36f, 224f, 22f, 14, TextAnchor.MiddleRight);
            _playerHeader = AddText(_panel.transform, "PlayerHeader", "Player", 12f, -61f, 218f, 22f, 13, TextAnchor.MiddleLeft, FontStyle.Bold);
            _damageHeader = AddText(_panel.transform, "DamageHeader", "Damage", 238f, -61f, 74f, 22f, 13, TextAnchor.MiddleRight, FontStyle.Bold);
            _percentHeader = AddText(_panel.transform, "PercentHeader", "%", 312f, -61f, 58f, 22f, 13, TextAnchor.MiddleRight, FontStyle.Bold);
            _dpsHeader = AddText(_panel.transform, "DpsHeader", "DPS", 318f, -61f, 66f, 22f, 13, TextAnchor.MiddleRight, FontStyle.Bold);
            _takenHeader = AddText(_panel.transform, "TakenHeader", "Taken", 390f, -61f, 78f, 22f, 13, TextAnchor.MiddleRight, FontStyle.Bold);
            _panel.SetActive(false); _creationWarningLogged = false; Plugin.UiLog("CombatMeterUiCreated"); return true;
        }
        catch (Exception ex)
        {
            DestroyObjects();
            if (!_creationWarningLogged) { _creationWarningLogged = true; Plugin.Warn("CombatMeterUiCreationFailed exception=" + ex.GetType().Name); }
            return false;
        }
    }

    private void ApplyLayout(float x, float y, float scale, float width, float opacity,
        bool showBars, bool showPercent, float barOpacity, Action<float, float> persist)
    {
        scale = CombatMeterLayout.SanitizeScale(scale); width = CombatMeterLayout.SanitizeWidth(width);
        opacity = CombatMeterLayout.SanitizeOpacity(opacity); barOpacity = CombatMeterLayout.SanitizeOpacity(barOpacity);
        if (!Near(_scale, scale))
        { _scale = scale; _panelRect.localScale = new Vector3(scale, scale, 1f); Plugin.UiLog("CombatMeterUiScaleChanged scale=" + F(scale)); }
        bool columnsChanged = _showPercent != showPercent;
        _showBars = showBars; _showPercent = showPercent; _barOpacity = barOpacity;
        if (!Near(_width, width) || columnsChanged) { _width = width; ApplyWidth(width); }
        if (!Near(_opacity, opacity)) { _opacity = opacity; _panelImage.color = new Color(0.035f, 0.035f, 0.045f, opacity); }
        _percentHeader.gameObject.SetActive(_showPercent);
        foreach (RowView row in _rows) row.ApplyStyle(_showBars, _showPercent, _barOpacity);
        if (!_edit.IsDragging && (!Near(_panelRect.anchoredPosition.x, x) || !Near(_panelRect.anchoredPosition.y, y))) _panelRect.anchoredPosition = new Vector2(x, y);
        Vector2 canvas = _canvasRect.rect.size;
        bool resolutionChanged = !Near(_canvasWidth, canvas.x) || !Near(_canvasHeight, canvas.y);
        _canvasWidth = canvas.x; _canvasHeight = canvas.y;
        if (!_edit.IsDragging && (resolutionChanged || !PositionInsideBounds())) ClampAndPersist(persist, true);
    }

    private void ApplyWidth(float width)
    {
        _panelRect.sizeDelta = new Vector2(width, _panelRect.sizeDelta.y); SetRect(_dragHandle, 0f, 0f, width, 32f);
        SetRect((RectTransform)_title.transform, 12f, 0f, width - 24f, 32f);
        float half = (width - 24f) / 2f; SetRect((RectTransform)_state.transform, 12f, -36f, half, 22f); SetRect((RectTransform)_time.transform, 12f + half, -36f, half, 22f);
        float takenX = width - 72f, dpsX = width - 136f, percentX = width - 198f;
        float damageX = width - (_showPercent ? 268f : 206f);
        SetRect((RectTransform)_playerHeader.transform, 12f, -61f, Math.Max(70f, damageX - 20f), 22f);
        SetRect((RectTransform)_damageHeader.transform, damageX, -61f, 66f, 22f);
        SetRect((RectTransform)_percentHeader.transform, percentX, -61f, 58f, 22f);
        SetRect((RectTransform)_dpsHeader.transform, dpsX, -61f, 58f, 22f); SetRect((RectTransform)_takenHeader.transform, takenX, -61f, 60f, 22f);
        foreach (RowView row in _rows) row.ApplyWidth(width, _showPercent);
    }

    private void Render(CombatMeterViewModel model, long sequence, Action<float, float> persist)
    {
        _state.text = "State: " + model.StateText; _time.text = model.TimeText;
        while (_rows.Count < model.Rows.Count && _rows.Count < CombatSnapshotBuilder.MaxPlayers) _rows.Add(CreateRow(_rows.Count));
        for (int i = 0; i < _rows.Count; i++)
        { bool active = i < model.Rows.Count; _rows[i].Root.SetActive(active); if (active) _rows[i].Set(model.Rows[i], _width, _showBars, _showPercent, _barOpacity); }
        _panelRect.sizeDelta = new Vector2(_width, 96f + RowHeight * model.Rows.Count); ClampAndPersist(persist, true);
        bool meaningful = model.EncounterId != _lastLoggedEncounter || model.StateText != _lastLoggedState || Time.unscaledTime >= _nextRenderLogAt;
        if (meaningful)
        {
            _lastLoggedEncounter = model.EncounterId; _lastLoggedState = model.StateText; _nextRenderLogAt = Time.unscaledTime + 5f;
            Plugin.UiLog("CombatMeterUiSnapshotRendered sequence=" + sequence + " encounterId=" + model.EncounterId + " state=" + model.StateText + " rows=" + model.Rows.Count);
        }
    }

    private RowView CreateRow(int index)
    {
        var root = new GameObject("Row" + index, typeof(RectTransform)); root.transform.SetParent(_panel.transform, false);
        SetRect((RectTransform)root.transform, 0f, -86f - index * RowHeight, _width, RowHeight);
        Image background = AddRowImage(root.transform, "Background", new Color(1f, 1f, 1f, index % 2 == 0 ? 0.035f : 0.065f));
        Image fill = AddRowImage(root.transform, "DamageFill", Color.clear);
        var row = new RowView(root, AddText(root.transform, "Player", "", 12f, 0f, 100f, RowHeight, 14, TextAnchor.MiddleLeft),
            AddText(root.transform, "Damage", "", 0f, 0f, 66f, RowHeight, 14, TextAnchor.MiddleRight),
            AddText(root.transform, "Percent", "", 0f, 0f, 58f, RowHeight, 14, TextAnchor.MiddleRight),
            AddText(root.transform, "DPS", "", 0f, 0f, 58f, RowHeight, 14, TextAnchor.MiddleRight),
            AddText(root.transform, "Taken", "", 0f, 0f, 60f, RowHeight, 14, TextAnchor.MiddleRight), background, fill);
        row.ApplyWidth(_width, _showPercent); row.ApplyStyle(_showBars, _showPercent, _barOpacity); return row;
    }

    private void UpdateDrag(Action<float, float> persist)
    {
        if (!_lastVisible || !_edit.IsEditing) { _edit.EndDrag(); return; }
        if (!_edit.IsDragging && Input.GetMouseButtonDown(0) && RectTransformUtility.RectangleContainsScreenPoint(_dragHandle, Input.mousePosition, null) &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, Input.mousePosition, null, out _dragStartPointer))
        { _edit.BeginDrag(); _dragStartPosition = _panelRect.anchoredPosition; }
        if (!_edit.IsDragging) return;
        if (Input.GetMouseButton(0) && RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, Input.mousePosition, null, out Vector2 pointer))
            _panelRect.anchoredPosition = _dragStartPosition + pointer - _dragStartPointer;
        if (Input.GetMouseButtonUp(0))
        {
            FinishDrag(persist, true);
        }
    }

    private void FinishDrag(Action<float, float> persist, bool logMoved)
    {
        if (!_edit.EndDrag()) return;
        ClampAndPersist(persist, true); Vector2 p = _panelRect.anchoredPosition; persist?.Invoke(p.x, p.y);
        if (logMoved) Plugin.UiLog("CombatMeterUiMoved x=" + F(p.x) + " y=" + F(p.y));
    }

    private void ResetPosition(Action<float, float> persist)
    {
        UiPoint value = CombatMeterLayout.DefaultPosition(); _panelRect.anchoredPosition = new Vector2(value.X, value.Y);
        ClampAndPersist(persist, false); Vector2 p = _panelRect.anchoredPosition; persist?.Invoke(p.x, p.y);
        Plugin.UiLog("CombatMeterUiPositionReset x=" + F(p.x) + " y=" + F(p.y));
    }

    private bool PositionInsideBounds()
    { UiPoint c = GetClamped(); Vector2 p = _panelRect.anchoredPosition; return Near(p.x, c.X) && Near(p.y, c.Y); }
    private void ClampAndPersist(Action<float, float> persist, bool log)
    {
        UiPoint c = GetClamped(); Vector2 p = _panelRect.anchoredPosition;
        if (Near(p.x, c.X) && Near(p.y, c.Y)) return;
        _panelRect.anchoredPosition = new Vector2(c.X, c.Y); persist?.Invoke(c.X, c.Y);
        if (log) Plugin.UiLog("CombatMeterUiPositionClamped x=" + F(c.X) + " y=" + F(c.Y));
    }
    private UiPoint GetClamped() => CombatMeterLayout.Clamp(new UiPoint(_panelRect.anchoredPosition.x, _panelRect.anchoredPosition.y),
        new UiSize(_panelRect.rect.width, _panelRect.rect.height), new UiSize(_canvasRect.rect.width, _canvasRect.rect.height), _scale);

    private Text AddText(Transform parent, string name, string value, float x, float y, float width, float height, int size, TextAnchor alignment, FontStyle style = FontStyle.Normal)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(Text)); gameObject.transform.SetParent(parent, false); SetRect((RectTransform)gameObject.transform, x, y, width, height);
        Text text = gameObject.GetComponent<Text>(); text.font = _font; text.text = value; text.fontSize = size; text.fontStyle = style; text.alignment = alignment;
        text.color = Color.white; text.raycastTarget = false; text.horizontalOverflow = HorizontalWrapMode.Overflow; text.verticalOverflow = VerticalWrapMode.Truncate; return text;
    }
    private static Image AddRowImage(Transform parent, string name, Color color)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(Image)); gameObject.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)gameObject.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        Image image = gameObject.GetComponent<Image>(); image.color = color; image.raycastTarget = false; return image;
    }
    private static void SetRect(RectTransform rect, float x, float y, float width, float height)
    { rect.anchorMin = new Vector2(0f, 1f); rect.anchorMax = new Vector2(0f, 1f); rect.pivot = new Vector2(0f, 1f); rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(width, height); }
    private static Font ResolveFont()
    {
        foreach (Font font in Resources.FindObjectsOfTypeAll<Font>()) if (font && font.HasCharacter('Ж')) return font;
        try { Font font = Resources.GetBuiltinResource<Font>("Arial.ttf"); if (font) return font; } catch { }
        try { Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); if (font) return font; } catch { }
        throw new InvalidOperationException("No runtime font with Cyrillic support was found");
    }
    private void SetPanelVisible(bool visible)
    { if (!_panel || _lastVisible == visible) return; _lastVisible = visible; _panel.SetActive(visible); if (!visible) _edit.EndDrag(); Plugin.UiLog("CombatMeterUiVisibilityChanged visible=" + visible.ToString().ToLowerInvariant()); }
    internal void Destroy()
    { if (!IsCreated) { _presenter.Reset(); return; } ExitEditMode("Lifecycle", null); DestroyObjects(); _presenter.Reset(); Plugin.UiLog("CombatMeterUiDestroyed"); }
    private void DestroyObjects()
    {
        if (_canvasRoot) UnityEngine.Object.Destroy(_canvasRoot);
        _canvasRoot = null; _panel = null; _canvasRect = null; _panelRect = null; _dragHandle = null; _panelImage = null;
        _title = null; _state = null; _time = null; _playerHeader = null; _damageHeader = null; _percentHeader = null; _dpsHeader = null; _takenHeader = null; _font = null;
        _rows.Clear(); _lastVisible = false; _previewRendered = false; _edit.Exit(); _width = _scale = _opacity = _canvasWidth = _canvasHeight = 0f;
        _barOpacity = 0f; _showBars = _showPercent = false;
        _lastLoggedEncounter = 0; _lastLoggedState = null; _nextRenderLogAt = 0f;
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;
    private static string F(float value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class RowView
    {
        internal readonly GameObject Root;
        private readonly Text _name, _damage, _percent, _dps, _taken;
        private readonly Image _background, _fill;
        private double _contribution;
        private uint _playerColorRgb;
        internal RowView(GameObject root, Text name, Text damage, Text percent, Text dps, Text taken, Image background, Image fill)
        { Root = root; _name = name; _damage = damage; _percent = percent; _dps = dps; _taken = taken; _background = background; _fill = fill; }
        internal void ApplyWidth(float width, bool showPercent)
        {
            ((RectTransform)Root.transform).sizeDelta = new Vector2(width, RowHeight);
            float damageX = width - (showPercent ? 268f : 206f);
            SetRect((RectTransform)_name.transform, 12f, 0f, Math.Max(70f, damageX - 20f), RowHeight);
            SetRect((RectTransform)_damage.transform, damageX, 0f, 66f, RowHeight);
            SetRect((RectTransform)_percent.transform, width - 198f, 0f, 58f, RowHeight);
            SetRect((RectTransform)_dps.transform, width - 136f, 0f, 58f, RowHeight);
            SetRect((RectTransform)_taken.transform, width - 72f, 0f, 60f, RowHeight);
        }
        internal void ApplyStyle(bool showBars, bool showPercent, float opacity)
        {
            _percent.gameObject.SetActive(showPercent); _background.raycastTarget = false; _fill.raycastTarget = false;
            _fill.color = new Color(((_playerColorRgb >> 16) & 255) / 255f, ((_playerColorRgb >> 8) & 255) / 255f, (_playerColorRgb & 255) / 255f, opacity);
            _fill.gameObject.SetActive(showBars && _contribution > 0d);
        }
        internal void Set(CombatMeterRowModel row, float width, bool showBars, bool showPercent, float opacity)
        {
            _playerColorRgb = row.PlayerColorRgb;
            _contribution = Math.Max(0d, Math.Min(1d, row.DamageContribution));
            int max = Math.Max(8, Math.Min(64, (int)((width - (showPercent ? 296f : 234f)) / 8f)));
            _name.text = Ellipsize(row.NameText, max); _damage.text = row.DamageText; _percent.text = row.PercentText; _dps.text = row.DpsText; _taken.text = row.TakenText;
            RectTransform rect = (RectTransform)_fill.transform; rect.anchorMax = new Vector2((float)_contribution, 1f); rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            ApplyStyle(showBars, showPercent, opacity);
        }
        private static string Ellipsize(string value, int max)
        { if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? ""; int length = max - 3; if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--; return value.Substring(0, length) + "..."; }
    }
}
