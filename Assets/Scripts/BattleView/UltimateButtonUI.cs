using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using FrameSync;

/// <summary>
/// 大招释放按钮UI — 屏幕上方中心区域。
/// 为本地玩家的每个角色创建一个圆形按钮，显示角色头像，
/// CD期间显示径向遮罩和倒计时文字，点击后触发大招释放。
/// </summary>
public class UltimateButtonUI : MonoBehaviour
{
    /// <summary>点击大招按钮时的回调，参数为角色 PlayerId。</summary>
    public System.Action<byte> OnUltRequested;

    class UltSlot
    {
        public byte PlayerId;
        public Button Button;
        public Image CdFillImage;
        public Text CdText;
        public Image BorderImage;
        public int CdLeft;
        public int CdTotal;
    }

    readonly List<UltSlot> _slots = new();

    void Awake()
    {
        // Canvas
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        canvas.pixelPerfect = false;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        // 射线检测组件 — Canvas 响应点击必须挂载
        gameObject.AddComponent<GraphicRaycaster>();

        // 确保场景中存在 EventSystem（UI 事件分发必需）
        if (FindObjectOfType<EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.transform.SetParent(transform.parent);
            esGO.AddComponent<EventSystem>();
            esGO.AddComponent<InputSystemUIInputModule>();
        }

        // 顶部居中容器
        var container = new GameObject("UltContainer");
        container.transform.SetParent(transform, false);
        var cRect = container.AddComponent<RectTransform>();
        cRect.anchorMin = new Vector2(0.5f, 1f);
        cRect.anchorMax = new Vector2(0.5f, 1f);
        cRect.pivot = new Vector2(0.5f, 1f);
        cRect.anchoredPosition = new Vector2(0, -20);
        cRect.sizeDelta = new Vector2(200, 100);

        var layout = container.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 12;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
    }

    /// <summary>为本地玩家队伍中的角色添加大招按钮。</summary>
    public void AddCharacter(byte playerId, Sprite headIcon)
    {
        var slot = new UltSlot { PlayerId = playerId };

        // 根节点
        var root = new GameObject($"UltBtn_P{playerId}");
        root.transform.SetParent(transform.Find("UltContainer"), false);
        var le = root.AddComponent<LayoutElement>();
        le.preferredWidth = 80;
        le.preferredHeight = 80;

        // Button 组件
        slot.Button = root.AddComponent<Button>();
        slot.Button.onClick.AddListener(() => OnUltRequested?.Invoke(playerId));
        slot.Button.transition = Selectable.Transition.None;

        // 金色边框（最底层，就绪时显示）
        var border = CreateChild(root, "Border", new Vector2(-4, -4));
        slot.BorderImage = border.AddComponent<Image>();
        slot.BorderImage.sprite = CircleSprite;
        slot.BorderImage.color = new Color(1f, 0.85f, 0.2f, 0.9f);
        slot.BorderImage.raycastTarget = false;
        border.transform.SetAsFirstSibling();

        // 深色背景
        var bg = CreateChild(root, "Bg", Vector2.zero);
        var bgImg = bg.AddComponent<Image>();
        bgImg.sprite = CircleSprite;
        bgImg.color = new Color(0.12f, 0.12f, 0.22f, 0.9f);
        bgImg.raycastTarget = true;
        slot.Button.targetGraphic = bgImg;

        // 角色头像
        var icon = CreateChild(root, "Icon", new Vector2(6, 6));
        var iconImg = icon.AddComponent<Image>();
        iconImg.sprite = headIcon;
        iconImg.raycastTarget = false;

        // CD 径向遮罩
        var cd = CreateChild(root, "CdFill", new Vector2(6, 6));
        slot.CdFillImage = cd.AddComponent<Image>();
        slot.CdFillImage.sprite = CircleSprite;
        slot.CdFillImage.color = new Color(0, 0, 0, 0.65f);
        slot.CdFillImage.fillMethod = Image.FillMethod.Radial360;
        slot.CdFillImage.fillOrigin = (int)Image.Origin360.Top;
        slot.CdFillImage.fillClockwise = false;
        slot.CdFillImage.fillAmount = 0f;
        slot.CdFillImage.raycastTarget = false;

        // CD 倒计时文字
        var txt = CreateChild(root, "CdText", Vector2.zero);
        slot.CdText = txt.AddComponent<Text>();
        slot.CdText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        slot.CdText.fontSize = 20;
        slot.CdText.alignment = TextAnchor.MiddleCenter;
        slot.CdText.color = Color.white;
        slot.CdText.raycastTarget = false;
        slot.CdText.text = "";

        _slots.Add(slot);
    }

    GameObject CreateChild(GameObject parent, string name, Vector2 padding)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = padding;
        rt.offsetMax = -padding;
        return go;
    }

    /// <summary>更新指定角色的大招CD状态。</summary>
    public void UpdateCooldown(byte playerId, int cdLeft, int cdTotal)
    {
        foreach (var s in _slots)
        {
            if (s.PlayerId != playerId) continue;
            s.CdLeft = cdLeft;
            if (cdTotal > 0) s.CdTotal = cdTotal;
            return;
        }
    }

    /// <summary>清除所有按钮（新游戏时调用）。</summary>
    public void Clear()
    {
        foreach (var s in _slots)
            if (s.Button != null) Destroy(s.Button.gameObject);
        _slots.Clear();
    }

    void Update()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];
            if (s.CdFillImage == null) continue;

            bool onCd = s.CdLeft > 0;
            float fill = (s.CdTotal > 0 && onCd) ? (float)s.CdLeft / s.CdTotal : 0f;
            s.CdFillImage.fillAmount = fill;

            if (onCd)
            {
                float sec = FrameTime.ToSec(s.CdLeft);
                s.CdText.text = sec > 0 ? $"{sec:F1}" : "";
            }
            else
            {
                s.CdText.text = "";
            }

            // 就绪时显示金色边框（带脉冲动画）
            if (s.BorderImage != null)
            {
                s.BorderImage.enabled = !onCd;
                if (!onCd)
                {
                    float pulse = 0.6f + 0.4f * Mathf.Sin(Time.time * 3f);
                    var c = s.BorderImage.color;
                    s.BorderImage.color = new Color(c.r, c.g, c.b, pulse);
                }
            }
        }
    }

    static Sprite _circleSprite;
    static Sprite CircleSprite
    {
        get
        {
            if (_circleSprite != null) return _circleSprite;
            int sz = 64;
            var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            float c = sz * 0.5f, r = c - 1;
            for (int y = 0; y < sz; y++)
                for (int x = 0; x < sz; x++)
                {
                    float dx = x - c, dy = y - c;
                    tex.SetPixel(x, y, Mathf.Sqrt(dx * dx + dy * dy) <= r ? Color.white : Color.clear);
                }
            tex.Apply();
            _circleSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 64);
            return _circleSprite;
        }
    }

    void OnDestroy() => Clear();
}
