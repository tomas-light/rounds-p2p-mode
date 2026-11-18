using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RoundsP2P.Plugin;

/// <summary>Steam pages on the game's own menu stack, using its button style.</summary>
internal sealed class NativeSteamMenu : IDisposable
{
    private readonly Plugin _plugin;
    private ListMenuPage? _sourcePage;
    private ListMenuPage? _onlinePage;
    private ListMenuPage? _lobbyPage;
    private Button? _createButton;
    private Button? _inviteButton;
    private TextMeshProUGUI? _onlineStatus;
    private TextMeshProUGUI? _lobbyStatus;

    internal NativeSteamMenu(Plugin plugin) => _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

    internal void OpenOnline()
    {
        EnsurePages();
        Object.FindObjectOfType<SkipIntro>(true)?.Skip();
        MainMenuHandler.instance.Open();
        ListMenu.instance.Unfocus = false;
        Open(_plugin.HasSteamLobby ? _lobbyPage! : _onlinePage!);
        Refresh();
    }

    internal void ShowLobby()
    {
        EnsurePages();
        MainMenuHandler.instance.Open();
        ListMenu.instance.Unfocus = false;
        Open(_lobbyPage!);
        Refresh();
    }

    internal void Refresh()
    {
        if (_onlinePage == null || _lobbyPage == null) return;
        string status = _plugin.SteamStatus ?? string.Empty;
        if (_onlineStatus != null && _onlineStatus.text != status) _onlineStatus.text = status;
        if (_lobbyStatus != null && _lobbyStatus.text != status) _lobbyStatus.text = status;
        if (_createButton != null) _createButton.interactable = !_plugin.HasSteamLobby;
        if (_inviteButton != null) _inviteButton.interactable = _plugin.CanInviteSteamFriend;
    }

    internal bool TryClickCreateLobbyForSmoke()
    {
        if (_onlinePage == null || !IsTop(_onlinePage) || _createButton == null || !_createButton.isActiveAndEnabled || !_createButton.interactable)
            return false;
        _createButton.onClick.Invoke();
        return true;
    }

    private void EnsurePages()
    {
        MainMenuHandler menu = MainMenuHandler.instance;
        if (menu == null || menu.MainMenu == null || ListMenu.instance == null)
            throw new InvalidOperationException("Главное меню ещё загружается.");
        if (_onlinePage != null && _lobbyPage != null && _sourcePage == menu.MainMenu) return;
        DestroyPages();
        _sourcePage = menu.MainMenu;
        Button? template = FindButtonTemplate(_sourcePage);
        if (template == null)
            throw new InvalidOperationException("Не удалось подготовить меню онлайн-игры.");

        try
        {
            _onlinePage = CreatePage("RoundsP2P.Online", _sourcePage, out Transform onlineGrid);
            _onlineStatus = CreateStatus(template, onlineGrid, "OnlineStatus");
            _createButton = CreateButton(template, onlineGrid, "Создать лобби", () =>
            {
                _plugin.CreateSteamLobby();
                ShowLobby();
            });
            CreateButton(template, onlineGrid, "Назад", () => ListMenu.instance.CloseTopPage());
            FinishPage(_onlinePage, _createButton);

            _lobbyPage = CreatePage("RoundsP2P.Lobby", _sourcePage, out Transform lobbyGrid);
            _lobbyStatus = CreateStatus(template, lobbyGrid, "LobbyStatus");
            _inviteButton = CreateButton(template, lobbyGrid, "Пригласить друга", _plugin.InviteSteamFriend);
            CreateButton(template, lobbyGrid, "Покинуть лобби", () =>
            {
                _plugin.LeaveSteamLobby();
                CloseOwnedTopPages();
                OpenOnline();
            });
            FinishPage(_lobbyPage, _inviteButton);
        }
        catch
        {
            DestroyPages();
            throw;
        }
    }

    private static Button? FindButtonTemplate(ListMenuPage page)
    {
        Button? first = null;
        foreach (Button button in page.GetComponentsInChildren<Button>(true))
        {
            if (button.GetComponent<ListMenuButton>() == null || button.GetComponentInChildren<TextMeshProUGUI>(true) == null)
                continue;
            first ??= button;
            if (button.GetComponent<CheckUserPrivilegeButton>() == null && button.GetComponent<QuitButton>() == null &&
                button.GetComponent<OptionsButton>() == null)
                return button;
        }
        return first;
    }

    private static ListMenuPage CreatePage(string name, ListMenuPage source, out Transform grid)
    {
        if (source.transform.childCount == 0)
            throw new InvalidOperationException("Штатная страница меню не содержит список кнопок.");
        var root = new GameObject(name, typeof(RectTransform));
        root.SetActive(false);
        root.transform.SetParent(source.transform.parent, false);
        CopyTransform(source.transform, root.transform);

        // The parent remains inactive while cloned listeners and children are removed.
        // This prevents copied menu actions from running during construction.
        GameObject clonedGrid = Object.Instantiate(source.transform.GetChild(0).gameObject, root.transform, false);
        clonedGrid.name = "Grid";
        grid = clonedGrid.transform;
        while (grid.childCount > 0) Object.DestroyImmediate(grid.GetChild(0).gameObject);
        StripGameActions(clonedGrid, preserveMenuStyle: false);

        VerticalLayoutGroup? vertical = clonedGrid.GetComponent<VerticalLayoutGroup>();
        if (vertical == null)
        {
            foreach (LayoutGroup layout in clonedGrid.GetComponents<LayoutGroup>()) Object.DestroyImmediate(layout);
            vertical = clonedGrid.AddComponent<VerticalLayoutGroup>();
            vertical.spacing = 12f;
            vertical.childAlignment = TextAnchor.UpperLeft;
        }
        vertical.childControlHeight = true;
        vertical.childControlWidth = true;
        vertical.childForceExpandHeight = false;
        vertical.childForceExpandWidth = true;
        var page = root.AddComponent<ListMenuPage>();
        page.barHeight = source.barHeight;
        return page;
    }

    private static void CopyTransform(Transform source, Transform target)
    {
        target.localPosition = source.localPosition;
        target.localRotation = source.localRotation;
        target.localScale = source.localScale;
        if (source is RectTransform sourceRect && target is RectTransform targetRect)
        {
            targetRect.anchorMin = sourceRect.anchorMin;
            targetRect.anchorMax = sourceRect.anchorMax;
            targetRect.pivot = sourceRect.pivot;
            targetRect.sizeDelta = sourceRect.sizeDelta;
            targetRect.anchoredPosition3D = sourceRect.anchoredPosition3D;
        }
    }

    private static Button CreateButton(Button template, Transform parent, string label, UnityAction action)
    {
        GameObject clone = Object.Instantiate(template.gameObject, parent, false);
        clone.name = "RoundsP2P." + label;
        StripGameActions(clone, preserveMenuStyle: true);
        Button button = clone.GetComponent<Button>();
        // A new event removes persistent Inspector listeners as well as runtime ones.
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(action);
        // Explicit serialized navigation may point at buttons on the stock page.
        button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
        foreach (Button childButton in clone.GetComponentsInChildren<Button>(true))
            if (childButton != button) childButton.onClick = new Button.ButtonClickedEvent();
        SetLabel(clone, label);
        LayoutElement layout = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
        layout.minHeight = 80f;
        layout.preferredHeight = Math.Max(80f, template.GetComponent<RectTransform>().rect.height);
        layout.flexibleHeight = 0f;
        clone.SetActive(true);
        return button;
    }

    private static TextMeshProUGUI CreateStatus(Button template, Transform parent, string name)
    {
        GameObject clone = Object.Instantiate(template.gameObject, parent, false);
        clone.name = "RoundsP2P." + name;
        StripGameActions(clone, preserveMenuStyle: true);
        foreach (Button button in clone.GetComponentsInChildren<Button>(true)) Object.DestroyImmediate(button);
        foreach (ListMenuButton style in clone.GetComponentsInChildren<ListMenuButton>(true)) Object.DestroyImmediate(style);
        SetLabel(clone, string.Empty);
        TextMeshProUGUI text = clone.GetComponentInChildren<TextMeshProUGUI>(true);
        text.fontStyle &= ~FontStyles.Bold;
        text.fontSize = Math.Min(text.fontSize, 30f);
        text.enableWordWrapping = true;
        text.raycastTarget = false;
        foreach (Graphic graphic in clone.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
        LayoutElement layout = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
        layout.minHeight = 100f;
        layout.preferredHeight = 120f;
        layout.flexibleHeight = 0f;
        clone.SetActive(true);
        return text;
    }

    private static void SetLabel(GameObject root, string label)
    {
        foreach (UILocalizedString localized in root.GetComponentsInChildren<UILocalizedString>(true))
        {
            // Text marks the component as modified before its first Awake, preventing
            // an empty localization reference from clearing our label on activation.
            localized.Text.text = label;
            localized.ResetReference(label);
            AccessTools.Field(typeof(UILocalizedString), "m_onStringUpdated").SetValue(localized, new UnityEvent());
        }
        TextMeshProUGUI text = root.GetComponentInChildren<TextMeshProUGUI>(true);
        text.text = label;
    }

    private static void StripGameActions(GameObject root, bool preserveMenuStyle)
    {
        foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (component == null) continue;
            if (preserveMenuStyle && (component is ListMenuButton || component is UILocalizedString)) continue;
            if (component is EventTrigger || component.GetType().Assembly == typeof(MainMenuHandler).Assembly)
                Object.DestroyImmediate(component);
        }
    }

    private static void FinishPage(ListMenuPage page, Button first)
    {
        AccessTools.Field(typeof(ListMenuPage), "firstSelected").SetValue(page, first.GetComponent<ListMenuButton>());
        AccessTools.Field(typeof(ListMenuPage), "firstActiveChild").SetValue(page, first.transform.parent);
        // Awake now runs only after grid and sanitized children exist.
        page.gameObject.SetActive(true);
        page.Hide();
    }

    private static void Open(ListMenuPage page)
    {
        if (!IsTop(page)) page.Open();
        else page.Show();
        ListMenu.instance.UpdateBar(page);
    }

    private static bool IsTop(ListMenuPage page)
    {
        var stack = (Stack<ListMenuPage>)AccessTools.Field(typeof(ListMenu), "m_menuStack").GetValue(ListMenu.instance);
        return stack.Count > 0 && stack.Peek() == page;
    }

    private void CloseOwnedTopPages()
    {
        while ((_lobbyPage != null && IsTop(_lobbyPage)) || (_onlinePage != null && IsTop(_onlinePage)))
            ListMenu.instance.CloseTopPage();
    }

    private void DestroyPages()
    {
        if (ListMenu.instance != null) CloseOwnedTopPages();
        if (_onlinePage != null) Object.Destroy(_onlinePage.gameObject);
        if (_lobbyPage != null) Object.Destroy(_lobbyPage.gameObject);
        _sourcePage = null;
        _onlinePage = null;
        _lobbyPage = null;
        _createButton = null;
        _inviteButton = null;
        _onlineStatus = null;
        _lobbyStatus = null;
    }

    internal string DumpHierarchy()
    {
        var result = new StringBuilder();
        MainMenuHandler menu = MainMenuHandler.instance;
        if (menu == null || menu.MainMenu == null) return "Main menu not available.";
        AppendHierarchy(result, menu.MainMenu.transform, 0);
        if (_onlinePage != null) AppendHierarchy(result, _onlinePage.transform, 0);
        if (_lobbyPage != null) AppendHierarchy(result, _lobbyPage.transform, 0);
        return result.ToString();
    }

    private static void AppendHierarchy(StringBuilder output, Transform transform, int depth)
    {
        output.Append(' ', depth * 2).Append(transform.name).Append(" [");
        bool first = true;
        foreach (Component component in transform.GetComponents<Component>())
        {
            if (component == null) continue;
            if (!first) output.Append(", ");
            output.Append(component.GetType().Name);
            first = false;
        }
        output.AppendLine("]");
        for (int index = 0; index < transform.childCount; index++) AppendHierarchy(output, transform.GetChild(index), depth + 1);
    }

    public void Dispose() => DestroyPages();
}
