using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RoundsP2P.Plugin;

internal sealed class MatchHud : IDisposable
{
    private RoundCounter? _counter;
    private RectTransform? _root;
    private TextMeshProUGUI? _team0;
    private TextMeshProUGUI? _team1;
    private TextMeshProUGUI? _ping;
    private int _shownTeam0 = -1;
    private int _shownTeam1 = -1;
    private int? _shownPing;

    internal void Refresh(RoundCounter counter, int team0Wins, int team1Wins, int? peerPingMs)
    {
        Transform first = counter.p1Parent;
        Transform second = counter.p2Parent;
        if (!counter.isActiveAndEnabled || first == null || second == null)
        {
            Hide();
            return;
        }

        if (_counter != counter || _root == null)
        {
            Dispose();
            var parent = counter.transform as RectTransform ?? counter.GetComponentInParent<Canvas>()?.transform as RectTransform;
            if (parent == null)
                return;

            _root = (RectTransform)new GameObject("RoundsP2P.MatchHud", typeof(RectTransform), typeof(LayoutElement)).transform;
            _root.GetComponent<LayoutElement>().ignoreLayout = true;
            _root.SetParent(parent, false);
            _root.anchorMin = _root.anchorMax = parent.pivot;
            _root.anchoredPosition = Vector2.zero;
            _root.sizeDelta = Vector2.zero;
            _team0 = CreateLabel(_root, "Team0MatchWins");
            _team1 = CreateLabel(_root, "Team1MatchWins");
            _ping = CreateLabel(_root, "PeerPing");
            _ping.text = "Ping —";
            _counter = counter;
        }

        RectTransform root = _root;
        TextMeshProUGUI team0 = _team0!;
        TextMeshProUGUI team1 = _team1!;
        TextMeshProUGUI ping = _ping!;
        Vector2 firstRow = GetRowEnd(first, root, counter.p1Platform, out float firstHeight);
        Vector2 secondRow = GetRowEnd(second, root, counter.p2Platform, out float secondHeight);
        float size = Mathf.Clamp(Mathf.Max(firstHeight, secondHeight), 16, 48);
        float countX = Mathf.Max(firstRow.x, secondRow.x) + size * 0.35f;

        Place(team0, countX, firstRow.y, size * 2.5f, size);
        Place(team1, countX, secondRow.y, size * 2.5f, size);
        Place(ping, countX + size * 2.8f, (firstRow.y + secondRow.y) * 0.5f, size * 6, size * 0.82f);
        team0.color = PlayerSkinBank.GetPlayerSkinColors(0).winText;
        team1.color = PlayerSkinBank.GetPlayerSkinColors(1).winText;

        if (_shownTeam0 != team0Wins)
        {
            team0.text = team0Wins.ToString(CultureInfo.InvariantCulture);
            _shownTeam0 = team0Wins;
        }
        if (_shownTeam1 != team1Wins)
        {
            team1.text = team1Wins.ToString(CultureInfo.InvariantCulture);
            _shownTeam1 = team1Wins;
        }
        if (_shownPing != peerPingMs)
        {
            ping.text = peerPingMs is int milliseconds ? "Ping " + milliseconds.ToString(CultureInfo.InvariantCulture) + " ms" : "Ping —";
            _shownPing = peerPingMs;
        }
        if (!root.gameObject.activeSelf)
            root.gameObject.SetActive(true);
    }

    private static TextMeshProUGUI CreateLabel(RectTransform root, string name)
    {
        var label = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
        label.transform.SetParent(root, false);
        label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        label.rectTransform.pivot = new Vector2(0, 0.5f);
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.font = TMP_Settings.defaultFontAsset;
        label.enableWordWrapping = false;
        label.raycastTarget = false;
        return label;
    }

    private static void Place(TextMeshProUGUI label, float x, float y, float width, float size)
    {
        label.rectTransform.anchoredPosition = new Vector2(x, y);
        label.rectTransform.sizeDelta = new Vector2(width, size * 1.25f);
        label.fontSize = size;
    }

    private static Vector2 GetRowEnd(Transform row, RectTransform root, Image platform, out float height)
    {
        float right = float.NegativeInfinity;
        Vector2 rowEnd = row is RectTransform rectRow ? new Vector2(rectRow.rect.xMax, rectRow.rect.center.y) : Vector2.zero;
        Vector3 fallback = root.InverseTransformPoint(row.TransformPoint(rowEnd));
        float centerY = fallback.y;
        height = 0;
        Matrix4x4 rowToHud = root.worldToLocalMatrix * row.localToWorldMatrix;
        foreach (Transform child in row)
        {
            if (child is not RectTransform marker || !marker.gameObject.activeSelf)
                continue;

            // ROUNDS scales empty circles down. Use each marker's full rect so the
            // count stays aligned as points and rounds change the circle sizes.
            Matrix4x4 matrix = rowToHud * Matrix4x4.TRS(marker.localPosition, marker.localRotation, Vector3.one);
            Rect rect = marker.rect;
            Vector3 bottomLeft = matrix.MultiplyPoint3x4(new Vector3(rect.xMin, rect.yMin));
            Vector3 topRight = matrix.MultiplyPoint3x4(new Vector3(rect.xMax, rect.yMax));
            if (topRight.x > right)
            {
                right = topRight.x;
                centerY = (bottomLeft.y + topRight.y) * 0.5f;
            }
            height = Mathf.Max(height, Mathf.Abs(topRight.y - bottomLeft.y));
        }

        if (float.IsNegativeInfinity(right))
            right = fallback.x;
        if (platform != null && platform.isActiveAndEnabled)
        {
            RectTransform icon = platform.rectTransform;
            float iconRight = root.InverseTransformPoint(icon.TransformPoint(new Vector2(icon.rect.xMax, icon.rect.center.y))).x;
            right = Mathf.Max(right, iconRight);
        }
        return new Vector2(right, centerY);
    }

    internal void Hide()
    {
        if (_root != null && _root.gameObject.activeSelf)
            _root.gameObject.SetActive(false);
    }

    public void Dispose()
    {
        if (_root != null)
        {
            Hide();
            Object.Destroy(_root.gameObject);
        }
        _root = null;
        _counter = null;
        _team0 = _team1 = _ping = null;
        _shownTeam0 = _shownTeam1 = -1;
        _shownPing = null;
    }
}
