using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using FrameSync;

/// <summary>
/// 角色选择 UI — Canvas 制作的选角界面。
///
/// 左侧：所有可选角色头像网格（点击选择）
/// 右侧：已选角色展示（P1 和 P2）
///
/// 支持两种模式：
///   - 网络对战：玩家只能为自己的队伍选角，对手选择实时显示
///   - 本地对战：玩家依次为双方选角（先对手后自己）
///
/// 通过 OnCharacterPicked 回调通知外部（BattleEntry/LocalBattleEntry）。
/// </summary>
public class CharacterSelectUI : MonoBehaviour
{
    // ── 配置 ──────────────────────────────────────────────────
    public bool IsLocalMode;
    public byte LocalPlayerId = 1;
    public int TeamSize = 2;

    // ── 回调：外部监听选角 ──────────────────────────────────
    /// <summary>选角回调：(playerId, charType)。</summary>
    public Action<byte, CharacterType> OnCharacterPicked;

    // ── 运行时状态 ──────────────────────────────────────────
    bool _selectingForP2 = true; // 本地模式下先为P2选角
    readonly List<CharacterType> _p1Selections = new();
    readonly List<CharacterType> _p2Selections = new();
    bool _hidden;

    // ── UI 引用 ──────────────────────────────────────────────
    Canvas _canvas;
    GameObject _root;
    Transform _charGrid;           // 左侧角色列表容器
    Transform _p1SlotsParent;      // 右侧 P1 已选容器
    Transform _p2SlotsParent;      // 右侧 P2 已选容器
    Text _titleText;
    Text _hintText;
    Text _charInfoText;           // 角色详情文本
    readonly List<GameObject> _p1SlotIcons = new();
    readonly List<GameObject> _p2SlotIcons = new();

    // ── 可选角色列表（排除召唤物） ──────────────────────────
    static readonly CharacterType[] SelectableTypes =
    {
        CharacterType.Warrior,
        CharacterType.Archer,
        CharacterType.Assassin,
        CharacterType.Mage,
        CharacterType.Healer,
        CharacterType.Witch,
        CharacterType.Barbarian,
        CharacterType.LightningMage,
        CharacterType.Paladin,
        CharacterType.ThornWarrior,
        CharacterType.SkeletonKing,
    };

    // ── 角色中文名 ──────────────────────────────────────────
    static string GetCharName(CharacterType t)
    {
        return t switch
        {
            CharacterType.Warrior       => "剑士",
            CharacterType.Archer        => "弓手",
            CharacterType.Assassin      => "刺客",
            CharacterType.Mage          => "法师",
            CharacterType.Healer        => "医疗师",
            CharacterType.Witch         => "巫师",
            CharacterType.Barbarian     => "野蛮人",
            CharacterType.LightningMage => "闪电法师",
            CharacterType.Paladin       => "圣骑士",
            CharacterType.ThornWarrior  => "荆棘战士",
            CharacterType.SkeletonKing  => "骷髅王",
            _ => t.ToString(),
        };
    }

    static Color GetCharColor(CharacterType t)
    {
        return t switch
        {
            CharacterType.Warrior       => new Color(0.85f, 0.2f, 0.2f),
            CharacterType.Archer        => new Color(0.2f, 0.6f, 0.95f),
            CharacterType.Assassin      => new Color(0.6f, 0.2f, 0.85f),
            CharacterType.Mage          => new Color(0.95f, 0.5f, 0.1f),
            CharacterType.Healer        => new Color(0.3f, 0.9f, 0.4f),
            CharacterType.Witch         => new Color(0.5f, 0.3f, 0.7f),
            CharacterType.Barbarian     => new Color(0.7f, 0.4f, 0.2f),
            CharacterType.LightningMage => new Color(0.4f, 0.6f, 1.0f),
            CharacterType.Paladin       => new Color(0.9f, 0.8f, 0.2f),
            CharacterType.ThornWarrior  => new Color(0.4f, 0.7f, 0.3f),
            CharacterType.SkeletonKing  => new Color(0.5f, 0.2f, 0.5f),
            _ => Color.white,
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  构建 UI
    // ═══════════════════════════════════════════════════════════

    void Awake()
    {
        BuildUI();
    }

    void BuildUI()
    {
        // ── Canvas ──
        var canvasGo = new GameObject("CharSelectCanvas");
        canvasGo.transform.SetParent(transform);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;
        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1920, 1080);
        canvasGo.GetComponent<CanvasScaler>().screenMatchMode=CanvasScaler.ScreenMatchMode.Expand;
        canvasGo.AddComponent<GraphicRaycaster>();

        _root = canvasGo;

        // ── 全屏半透明背景 ──
        var bg = CreatePanel(canvasGo.transform, "BG", Color.black * 0.85f);
        Stretch(bg);

        // ── 标题 ──
        _titleText = CreateText(bg.transform, "Title", "══ 选择角色 ══", 32, TextAnchor.UpperCenter);
        SetAnchors(_titleText.gameObject, 0.3f, 0.92f, 0.7f, 0.98f);

        // ── 提示文本 ──
        _hintText = CreateText(bg.transform, "Hint", "", 18, TextAnchor.UpperCenter);
        SetAnchors(_hintText.gameObject, 0.25f, 0.87f, 0.75f, 0.92f);
        _hintText.color = Color.yellow;

        // ── 左侧：角色列表区域 ──
        var leftPanel = CreatePanel(bg.transform, "LeftPanel", new Color(0.12f, 0.12f, 0.18f, 0.9f));
        SetAnchors(leftPanel, 0.02f, 0.05f, 0.55f, 0.85f);

        var leftTitle = CreateText(leftPanel.transform, "LeftTitle", "可选角色", 20, TextAnchor.UpperCenter);
        SetAnchors(leftTitle.gameObject, 0f, 0.92f, 1f, 1f);

        // 角色网格（使用 GridLayoutGroup）
        var gridGo = new GameObject("CharGrid");
        gridGo.transform.SetParent(leftPanel.transform, false);
        SetAnchors(gridGo, 0.02f, 0.02f, 0.98f, 0.9f);

        var grid = gridGo.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(150, 180);
        grid.spacing = new Vector2(10, 10);
        grid.padding = new RectOffset(10, 10, 5, 5);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 3;
        grid.childAlignment = TextAnchor.UpperLeft;

        _charGrid = gridGo.transform;

        // 创建角色按钮
        foreach (var ct in SelectableTypes)
            CreateCharButton(ct);

        // ── 右侧上方：角色详情 ──
        var infoPanel = CreatePanel(bg.transform, "InfoPanel", new Color(0.1f, 0.14f, 0.2f, 0.9f));
        SetAnchors(infoPanel, 0.58f, 0.45f, 0.98f, 0.85f);

        _charInfoText = CreateText(infoPanel.transform, "InfoText", "点击左侧角色查看详情", 16, TextAnchor.UpperLeft);
        SetAnchors(_charInfoText.gameObject, 0.05f, 0.05f, 0.95f, 0.95f);
        _charInfoText.color = new Color(0.8f, 0.85f, 0.9f);

        // ── 右侧下方：已选角色 ──
        var selectedPanel = CreatePanel(bg.transform, "SelectedPanel", new Color(0.1f, 0.14f, 0.2f, 0.9f));
        SetAnchors(selectedPanel, 0.58f, 0.05f, 0.98f, 0.43f);

        // P1区
        var p1Label = CreateText(selectedPanel.transform, "P1Label", "", 18, TextAnchor.MiddleLeft);
        SetAnchors(p1Label.gameObject, 0.03f, 0.75f, 0.97f, 0.95f);
        p1Label.color = Color.cyan;
        p1Label.name = "P1Label";

        var p1Slots = new GameObject("P1Slots");
        p1Slots.transform.SetParent(selectedPanel.transform, false);
        SetAnchors(p1Slots, 0.03f, 0.52f, 0.97f, 0.75f);
        var p1Layout = p1Slots.AddComponent<HorizontalLayoutGroup>();
        p1Layout.spacing = 8;
        p1Layout.childAlignment = TextAnchor.MiddleLeft;
        p1Layout.childForceExpandWidth = false;
        p1Layout.childForceExpandHeight = false;
        _p1SlotsParent = p1Slots.transform;

        // 分割线
        var divider = CreatePanel(selectedPanel.transform, "Divider", new Color(0.5f, 0.5f, 0.5f, 0.5f));
        SetAnchors(divider, 0.05f, 0.495f, 0.95f, 0.505f);

        // P2区
        var p2Label = CreateText(selectedPanel.transform, "P2Label", "", 18, TextAnchor.MiddleLeft);
        SetAnchors(p2Label.gameObject, 0.03f, 0.3f, 0.97f, 0.48f);
        p2Label.color = new Color(1f, 0.5f, 0.5f);
        p2Label.name = "P2Label";

        var p2Slots = new GameObject("P2Slots");
        p2Slots.transform.SetParent(selectedPanel.transform, false);
        SetAnchors(p2Slots, 0.03f, 0.05f, 0.97f, 0.28f);
        var p2Layout = p2Slots.AddComponent<HorizontalLayoutGroup>();
        p2Layout.spacing = 8;
        p2Layout.childAlignment = TextAnchor.MiddleLeft;
        p2Layout.childForceExpandWidth = false;
        p2Layout.childForceExpandHeight = false;
        _p2SlotsParent = p2Slots.transform;

        UpdateUI();
    }

    void CreateCharButton(CharacterType ct)
    {
        var cfg = CharacterConfig.Get(ct);
        var color = GetCharColor(ct);
        var charName = GetCharName(ct);

        // 按钮容器
        var btnGo = new GameObject($"Btn_{ct}");
        btnGo.transform.SetParent(_charGrid, false);
        var btnImg = btnGo.AddComponent<Image>();
        btnImg.color = new Color(0.2f, 0.22f, 0.28f);

        var btn = btnGo.AddComponent<Button>();
        var ct2 = ct; // capture
        btn.onClick.AddListener(() => OnClickCharacter(ct2));

        // 角色颜色标志（模拟头像）
        var iconGo = new GameObject("Icon");
        iconGo.transform.SetParent(btnGo.transform, false);
        var iconImg = iconGo.AddComponent<Image>();

        // 尝试加载头像
        Sprite headSprite = null;
        if (!string.IsNullOrEmpty(cfg.HeadIcon))
            headSprite = Resources.Load<Sprite>(cfg.HeadIcon);

        if (headSprite != null)
        {
            iconImg.sprite = headSprite;
            iconImg.color = Color.white;
        }
        else
        {
            iconImg.color = color;
        }
        SetAnchors(iconGo, 0.1f, 0.3f, 0.9f, 0.95f);

        // 角色名称
        var nameText = CreateText(btnGo.transform, "Name", charName, 14, TextAnchor.MiddleCenter);
        SetAnchors(nameText.gameObject, 0f, 0f, 1f, 0.28f);
        nameText.color = color;

        // 鼠标悬停高亮和详情
        var trigger = btnGo.AddComponent<CharSelectButtonHover>();
        trigger.CharType = ct;
        trigger.UI = this;
        trigger.BtnImage = btnImg;
        trigger.NormalColor = new Color(0.2f, 0.22f, 0.28f);
        trigger.HoverColor = new Color(0.3f, 0.35f, 0.45f);
    }

    // ═══════════════════════════════════════════════════════════
    //  交互逻辑
    // ═══════════════════════════════════════════════════════════

    void OnClickCharacter(CharacterType ct)
    {
        if (_hidden) return;

        byte targetPlayerId;

        if (IsLocalMode)
        {
            // 本地模式：先为P2选角，选满后为P1选角
            if (_selectingForP2)
            {
                if (_p2Selections.Count >= TeamSize)
                {
                    _selectingForP2 = false;
                }
                else
                {
                    targetPlayerId = 2;
                    _p2Selections.Add(ct);
                    OnCharacterPicked?.Invoke(targetPlayerId, ct);

                    if (_p2Selections.Count >= TeamSize)
                        _selectingForP2 = false;

                    UpdateUI();
                    return;
                }
            }

            // 为 P1 选角
            if (_p1Selections.Count >= TeamSize) return;
            targetPlayerId = 1;
            _p1Selections.Add(ct);
        }
        else
        {
            // 网络模式：只能为自己的队伍选角
            var mySelections = LocalPlayerId == 1 ? _p1Selections : _p2Selections;
            if (mySelections.Count >= TeamSize) return;
            targetPlayerId = LocalPlayerId;
            mySelections.Add(ct);
        }

        OnCharacterPicked?.Invoke(targetPlayerId, ct);
        UpdateUI();
    }

    /// <summary>外部通知对手选了角色（网络模式下对手的选择通过事件到来）。</summary>
    public void OnOpponentSelected(byte playerId, CharacterType ct)
    {
        if (playerId == 1)
            _p1Selections.Add(ct);
        else if (playerId == 2)
            _p2Selections.Add(ct);
        UpdateUI();
    }

    /// <summary>显示角色详情（鼠标悬停时调用）。</summary>
    public void ShowCharInfo(CharacterType ct)
    {
        if (_charInfoText == null) return;
        var cfg = CharacterConfig.Get(ct);
        var atkSkill = SkillConfigLoader.Get(cfg.NormalAttack);
        var sk2 = SkillConfigLoader.Get(cfg.Skill2);
        var ult = SkillConfigLoader.Get(cfg.Ultimate);
        var passive = SkillConfigLoader.Get(cfg.Passive);

        string atkType = atkSkill != null &&
            (atkSkill.Type == "RangedAttack" || atkSkill.Type == "AoEProjectile" || atkSkill.Type == "PierceLine")
            ? "远程" : "近战";

        string info = $"<b><color=#{ColorUtility.ToHtmlStringRGB(GetCharColor(ct))}>{GetCharName(ct)}</color></b>\n\n";
        info += $"生命值: {cfg.MaxHp}    移速: {cfg.MoveSpeed}    抗性: {cfg.Resistance}%\n";
        info += $"攻击类型: {atkType}    职业: {cfg.Profession}\n\n";

        if (atkSkill != null)
            info += $"<b>普攻</b>: {atkSkill.Name}  伤害{atkSkill.Damage}  CD{atkSkill.Cooldown}帧\n";
        if (sk2 != null)
            info += $"<b>副技能</b>: {sk2.Name}  CD{sk2.Cooldown}帧\n";
        if (ult != null)
            info += $"<b>大招</b>: {ult.Name}  CD{ult.Cooldown}帧\n";
        if (passive != null)
            info += $"<b>被动</b>: {passive.Name}\n";

        _charInfoText.text = info;
    }

    /// <summary>隐藏整个选角UI（战斗开始后调用）。</summary>
    public void Hide()
    {
        _hidden = true;
        if (_root != null)
            _root.SetActive(false);
    }

    /// <summary>显示选角UI。</summary>
    public void Show()
    {
        _hidden = false;
        _p1Selections.Clear();
        _p2Selections.Clear();
        _selectingForP2 = true;
        if (_root != null)
            _root.SetActive(true);
        UpdateUI();
    }

    // ═══════════════════════════════════════════════════════════
    //  UI 更新
    // ═══════════════════════════════════════════════════════════

    void UpdateUI()
    {
        // 更新标题提示
        if (_hintText != null)
        {
            if (IsLocalMode)
            {
                if (_selectingForP2)
                    _hintText.text = $"请为对手(P2)选择角色 ({_p2Selections.Count}/{TeamSize})";
                else
                    _hintText.text = $"请为自己(P1)选择角色 ({_p1Selections.Count}/{TeamSize})";
            }
            else
            {
                var mySelections = LocalPlayerId == 1 ? _p1Selections : _p2Selections;
                _hintText.text = $"请选择你的角色 ({mySelections.Count}/{TeamSize})";
            }
        }

        // 更新P1/P2标签
        var p1Label = _root.transform.Find("BG/SelectedPanel/P1Label")?.GetComponent<Text>();
        var p2Label = _root.transform.Find("BG/SelectedPanel/P2Label")?.GetComponent<Text>();

        if (p1Label != null)
        {
            string marker = (!IsLocalMode && LocalPlayerId == 1) ? " (你)" : "";
            p1Label.text = $"队伍1{marker} ({_p1Selections.Count}/{TeamSize})";
        }
        if (p2Label != null)
        {
            string marker = (!IsLocalMode && LocalPlayerId == 2) ? " (你)" : "";
            p2Label.text = $"队伍2{marker} ({_p2Selections.Count}/{TeamSize})";
        }

        // 更新已选角色图标
        RefreshSlotIcons(_p1SlotsParent, _p1SlotIcons, _p1Selections);
        RefreshSlotIcons(_p2SlotsParent, _p2SlotIcons, _p2Selections);
    }

    void RefreshSlotIcons(Transform parent, List<GameObject> icons, List<CharacterType> selections)
    {
        // 清除旧图标
        for (int i = icons.Count - 1; i >= 0; i--)
        {
            if (icons[i] != null) Destroy(icons[i]);
        }
        icons.Clear();

        // 创建新图标
        foreach (var ct in selections)
        {
            var go = new GameObject($"Slot_{ct}");
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = 60;
            layout.preferredHeight = 60;

            var img = go.AddComponent<Image>();
            var cfg = CharacterConfig.Get(ct);
            Sprite headSprite = null;
            if (!string.IsNullOrEmpty(cfg.HeadIcon))
                headSprite = Resources.Load<Sprite>(cfg.HeadIcon);

            if (headSprite != null)
            {
                img.sprite = headSprite;
                img.color = Color.white;
            }
            else
            {
                img.color = GetCharColor(ct);
            }

            // 名称标签
            var nameGo = new GameObject("Name");
            nameGo.transform.SetParent(go.transform, false);
            var nameText = nameGo.AddComponent<Text>();
            nameText.text = GetCharName(ct);
            nameText.fontSize = 11;
            nameText.alignment = TextAnchor.LowerCenter;
            nameText.color = Color.white;
            nameText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var nameRect = nameGo.GetComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = Vector2.one;
            nameRect.offsetMin = Vector2.zero;
            nameRect.offsetMax = Vector2.zero;

            icons.Add(go);
        }

        // 空位占位符
        for (int i = selections.Count; i < TeamSize; i++)
        {
            var go = new GameObject($"EmptySlot_{i}");
            go.transform.SetParent(parent, false);
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = 60;
            layout.preferredHeight = 60;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.3f, 0.3f, 0.3f, 0.5f);

            var qm = new GameObject("QM");
            qm.transform.SetParent(go.transform, false);
            var qmText = qm.AddComponent<Text>();
            qmText.text = "?";
            qmText.fontSize = 24;
            qmText.alignment = TextAnchor.MiddleCenter;
            qmText.color = new Color(0.6f, 0.6f, 0.6f);
            qmText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var qmRect = qm.GetComponent<RectTransform>();
            qmRect.anchorMin = Vector2.zero;
            qmRect.anchorMax = Vector2.one;
            qmRect.offsetMin = Vector2.zero;
            qmRect.offsetMax = Vector2.zero;

            icons.Add(go);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  UI 工具方法
    // ═══════════════════════════════════════════════════════════

    static GameObject CreatePanel(Transform parent, string name, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        return go;
    }

    static Text CreateText(Transform parent, string name, string content, int fontSize, TextAnchor alignment)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.text = content;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = Color.white;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.supportRichText = true;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    static void SetAnchors(GameObject go, float minX, float minY, float maxX, float maxY)
    {
        var rt = go.GetComponent<RectTransform>();
        if (rt == null) rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(minX, minY);
        rt.anchorMax = new Vector2(maxX, maxY);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void Stretch(GameObject go)
    {
        SetAnchors(go, 0, 0, 1, 1);
    }

    void OnDestroy()
    {
        if (_root != null)
            Destroy(_root);
    }
}

/// <summary>角色按钮悬停效果（挂在角色按钮上）。</summary>
public class CharSelectButtonHover : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
{
    public CharacterType CharType;
    public CharacterSelectUI UI;
    public Image BtnImage;
    public Color NormalColor;
    public Color HoverColor;

    public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
    {
        if (BtnImage != null) BtnImage.color = HoverColor;
        if (UI != null) UI.ShowCharInfo(CharType);
    }

    public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
    {
        if (BtnImage != null) BtnImage.color = NormalColor;
    }
}
